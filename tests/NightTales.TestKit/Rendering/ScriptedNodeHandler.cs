using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Domain.Rendering;
using NightTales.RenderNode.Contract;

namespace NightTales.TestKit.Rendering;

/// <summary>
/// One scripted answer of a fake render node: a status with a body, or a call that never produces an answer
/// (D1: hand-written fakes, never a mocking library). The last step of a script repeats, so a test that must
/// drive a retry budget or a circuit breaker scripts one answer and lets the client call it as often as it
/// likes.
/// </summary>
/// <param name="Status">The status to answer with; meaningless for a failing step.</param>
/// <param name="Body">The body to answer with.</param>
/// <param name="Failure">The exception to raise before any answer arrives, or <c>null</c> for a real answer.</param>
public sealed record NodeHttpStep(int Status, string Body, Func<CancellationToken, Exception>? Failure)
{
    /// <summary>A plain answer.</summary>
    /// <param name="status">The status to answer with.</param>
    /// <param name="body">The body to answer with.</param>
    /// <returns>The step.</returns>
    public static NodeHttpStep Answer(int status, string body = "") => new(status, body, Failure: null);

    /// <summary>
    /// An answer whose body is a contract type serialised with the production profile, so the fake can never
    /// drift from the wire shape the client reads.
    /// </summary>
    /// <typeparam name="T">The contract type of the body.</typeparam>
    /// <param name="status">The status to answer with.</param>
    /// <param name="body">The body to serialise.</param>
    /// <returns>The step.</returns>
    public static NodeHttpStep Json<T>(int status, T body) =>
        Answer(status, JsonSerializer.Serialize(body, RenderNodeJson.Tolerant));

    /// <summary>A call that fails before any answer arrives, with the exception the fake raises.</summary>
    /// <param name="failure">Builds the exception; it receives the token the client sent.</param>
    /// <returns>The step.</returns>
    public static NodeHttpStep Fails(Func<CancellationToken, Exception> failure) =>
        new(0, string.Empty, failure);

    /// <summary>A transport failure: the exception's message carries the request URI exactly as the real one does.</summary>
    /// <param name="uri">The URI the message names, when a test wants to prove that the URI never travels.</param>
    /// <returns>The step.</returns>
    public static NodeHttpStep Transport(Uri? uri = null) =>
        Fails(_ => new HttpRequestException(
            $"No connection could be made because the target machine actively refused it: {uri?.ToString() ?? "the node"}"));
}

/// <summary>One call a fake node received.</summary>
/// <param name="Method">The HTTP verb.</param>
/// <param name="Path">The path and query of the request URI.</param>
/// <param name="ApiKey">The <c>x-api-key</c> header the client sent, when it sent one.</param>
/// <param name="Body">The request body, when it had one.</param>
public sealed record NodeRequest(string Method, string Path, string? ApiKey, string? Body);

/// <summary>
/// The scripted node of 09.6.2-T1's tests: it answers with the steps it was given (the last one repeats),
/// records every call, and can be re-scripted in place so one client instance can see a failure and then a
/// success — which is how a circuit breaker's half-open probe is driven without a socket.
/// </summary>
public sealed class ScriptedNodeHandler : HttpMessageHandler
{
    /// <summary>The header the node authenticates with (05 D4).</summary>
    private const string ApiKeyHeader = "x-api-key";

    private readonly List<NodeHttpStep> _steps;
    private readonly ConcurrentQueue<NodeRequest> _requests = new();
    private readonly Lock _gate = new();
    private int _served;

    /// <summary>Creates the fake with its script; the last step repeats.</summary>
    /// <param name="steps">At least one step.</param>
    public ScriptedNodeHandler(params NodeHttpStep[] steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        if (steps.Length == 0)
        {
            throw new ArgumentException("At least one step is required.", nameof(steps));
        }

        _steps = [.. steps];
    }

    /// <summary>Every call this node received, in order.</summary>
    public IReadOnlyList<NodeRequest> Requests => [.. _requests];

    /// <summary>Replaces the script, so the next call sees different answers.</summary>
    /// <param name="steps">The new script; the last step repeats.</param>
    public void Rescript(params NodeHttpStep[] steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        lock (_gate)
        {
            _steps.Clear();
            _steps.AddRange(steps);
            _served = 0;
        }
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        _requests.Enqueue(new NodeRequest(
            request.Method.Method,
            request.RequestUri?.PathAndQuery ?? string.Empty,
            request.Headers.TryGetValues(ApiKeyHeader, out var keys) ? keys.First() : null,
            body));

        NodeHttpStep step;
        lock (_gate)
        {
            step = _steps[Math.Min(_served, _steps.Count - 1)];
            _served++;
        }

        if (step.Failure is { } failure)
        {
            throw failure(cancellationToken);
        }

        return new HttpResponseMessage((HttpStatusCode)step.Status)
        {
            Content = new StringContent(step.Body, Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>
/// Locates and reads the frozen contract fixtures of 05.4.1-T2 (<c>worker-node/contract/fixtures/</c>): the
/// wire shapes a test may read but never edit. The directory is found by walking up from the test binary, so
/// no test needs to know where the repository is checked out.
/// </summary>
public static class ContractFixtures
{
    /// <summary>The fixtures directory.</summary>
    public static string Root { get; } = Locate();

    /// <summary>Every fixture file name, ordinally ordered.</summary>
    public static IReadOnlyList<string> Names { get; } =
        [.. Directory.GetFiles(Root, "*.json")
            .Select(file => Path.GetFileName(file)!)
            .OrderBy(name => name, StringComparer.Ordinal)];

    /// <summary>Reads one fixture verbatim.</summary>
    /// <param name="name">The fixture file name.</param>
    /// <returns>The fixture's text.</returns>
    public static string Read(string name) => File.ReadAllText(Path.Combine(Root, name));

    /// <summary>Walks up from <see cref="AppContext.BaseDirectory"/> to the fixtures directory.</summary>
    /// <returns>The directory.</returns>
    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "worker-node", "contract", "fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"The contract fixtures were not found above '{AppContext.BaseDirectory}'.");
    }
}

/// <summary>
/// An <see cref="IRenderNodeConnectionFactory"/> over a <see cref="ScriptedNodeHandler"/>: the client under
/// test never opens a socket, and a test can name the base address and the key the client is handed, which is
/// what makes "the key and the URL never travel into a log" assertable.
/// </summary>
/// <param name="handler">The handler every client sends through.</param>
/// <param name="apiKey">The <c>x-api-key</c> default header, when the test proves one never leaks.</param>
/// <param name="baseAddress">The base address to hand out instead of the node's own, for the sentinel case.</param>
public sealed class ScriptedNodeConnectionFactory(
    HttpMessageHandler handler,
    string? apiKey = null,
    Uri? baseAddress = null) : IRenderNodeConnectionFactory
{
    /// <summary>How many clients this factory built; a client is built per call and disposed by the caller.</summary>
    public int Clients { get; private set; }

    /// <inheritdoc />
    public HttpClient Create(WorkerNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = baseAddress ?? node.BaseUrl,
            Timeout = Timeout.InfiniteTimeSpan,
        };

        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add("x-api-key", apiKey);
        }

        Clients++;
        return client;
    }
}
