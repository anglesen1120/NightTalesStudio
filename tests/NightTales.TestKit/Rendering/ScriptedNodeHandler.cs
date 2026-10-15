using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Domain.Rendering;
using NightTales.RenderNode.Contract;
using NightTales.TestKit.Time;

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

    /// <summary>
    /// A refused pinned handshake: the platform wraps the handshake's <c>AuthenticationException</c> in the
    /// transport exception, exactly as a real pin mismatch does.
    /// </summary>
    /// <param name="uri">The URI the message names, when a test wants to prove that the URI never travels.</param>
    /// <returns>The step.</returns>
    public static NodeHttpStep TlsFailure(Uri? uri = null) =>
        Fails(_ => new HttpRequestException(
            $"The SSL connection could not be established: {uri?.ToString() ?? "the node"}",
            new AuthenticationException("The remote certificate is invalid according to the validation procedure.")));

    /// <summary>
    /// An answer whose body is a content the test owns, built fresh for every call: a counting content, a body
    /// that resets mid-read, or a length the node declares and never serves.
    /// </summary>
    /// <param name="status">The status to answer with.</param>
    /// <param name="content">Builds the content, once per call, so a retry never reads a consumed one.</param>
    /// <returns>The step.</returns>
    public static NodeHttpStep Content(int status, Func<HttpContent> content) =>
        new(status, string.Empty, Failure: null) { BodyFactory = content };

    /// <summary>
    /// The body one call is answered with, when the step carries one; a step without it answers the
    /// <see cref="Body"/> string, as a fresh <see cref="StringContent"/>.
    /// </summary>
    public Func<HttpContent>? BodyFactory { get; init; }
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
            Content = step.BodyFactory?.Invoke()
                ?? new StringContent(step.Body, Encoding.UTF8, "application/json"),
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
/// <param name="createFailure">Refuses to build a client when it answers an exception, standing in for a node
/// row without a pin, without a key or with an http URL outside Development.</param>
public sealed class ScriptedNodeConnectionFactory(
    HttpMessageHandler handler,
    string? apiKey = null,
    Uri? baseAddress = null,
    Func<WorkerNode, Exception?>? createFailure = null) : IRenderNodeConnectionFactory
{
    /// <summary>How many clients this factory built; one is built per logical call and disposed by the caller.</summary>
    public int Clients { get; private set; }

    /// <inheritdoc />
    public HttpClient Create(WorkerNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (createFailure?.Invoke(node) is { } refusal)
        {
            throw refusal;
        }

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

/// <summary>
/// Drives the render clients' manual clock (D3): the retry backoff, the attempt timeout and the total timeout
/// are all timers on <see cref="ManualTimeProvider"/>, so a call settles in virtual milliseconds while the test
/// itself waits in bounded real time and never hangs the run.
/// </summary>
public static class ManualClockDrive
{
    /// <summary>The real time one driven call gets before the test fails instead of waiting forever.</summary>
    public static readonly TimeSpan RealTimeBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Settles <paramref name="call"/> by advancing <paramref name="time"/> in steps. After every advance the
    /// drive yields real time — never the manual clock — so the pipeline has registered the timer the next
    /// advance is meant to fire; without it a single-threaded pipeline could be driven past a wait nothing was
    /// observing yet. Once the real-time budget is spent the call is reported as unsettled instead of hanging.
    /// </summary>
    /// <typeparam name="T">The call's success type.</typeparam>
    /// <param name="call">The call to settle.</param>
    /// <param name="time">The clock to advance.</param>
    /// <param name="step">The virtual span of one advance; 250 ms by default.</param>
    /// <returns>The call's result.</returns>
    public static async Task<T> DriveAsync<T>(Task<T> call, ManualTimeProvider time, TimeSpan? step = null)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(time);

        var advance = step ?? TimeSpan.FromMilliseconds(250);
        var started = Stopwatch.GetTimestamp();
        var iterations = 0;

        while (!call.IsCompleted)
        {
            if (iterations > 0 && Stopwatch.GetElapsedTime(started) > RealTimeBudget)
            {
                throw new TimeoutException(
                    $"The call did not settle within {RealTimeBudget} of real time ({iterations} clock advances of {advance}).");
            }

            time.Advance(advance);
            iterations++;

            await Task.Delay(1);
        }

        return await call;
    }

    /// <summary>Waits in bounded real time for a condition a driven client is expected to reach.</summary>
    /// <param name="condition">The condition to wait for.</param>
    /// <param name="because">What the test expects to happen, for the failure message.</param>
    /// <returns>A task that completes once the condition holds.</returns>
    public static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var started = Stopwatch.GetTimestamp();

        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) > RealTimeBudget)
            {
                throw new TimeoutException($"Waited {RealTimeBudget} of real time for: {because}");
            }

            await Task.Delay(1);
        }
    }
}

/// <summary>
/// A scripted body that counts how often its bytes are actually pulled, so a test can prove that a body was
/// never read — a declared length over the client's answer cap, or a 2xx whose body the client has no use for.
/// </summary>
/// <param name="declaredLength">The length the node declares and serves.</param>
/// <param name="declareLength">Whether the content declares that length at all: <c>false</c> stands in for a
/// chunked answer the client can only bound by counting the bytes it reads.</param>
public sealed class ProbeContent(long declaredLength, bool declareLength = true) : HttpContent
{
    /// <summary>How many times the bytes of this content were requested.</summary>
    public int Reads { get; private set; }

    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Reads++;
        stream.Write(new byte[declaredLength]);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override bool TryComputeLength(out long length)
    {
        length = declaredLength;

        return declareLength;
    }
}

/// <summary>
/// A scripted body that serves a few bytes and then throws <see cref="IOException"/>, standing in for the
/// connection reset or truncation a node can produce in the middle of its answer. The content declares no
/// length, so the client can only notice it by reading.
/// </summary>
/// <param name="healthyBytes">How many bytes are served before the reset.</param>
public sealed class ResettingBodyContent(int healthyBytes) : HttpContent
{
    /// <inheritdoc />
    protected override Task<Stream> CreateContentReadStreamAsync() =>
        Task.FromResult<Stream>(new ResettingStream(healthyBytes));

    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        throw new IOException("The node's answer was truncated mid-body (scripted reset).");

    /// <inheritdoc />
    protected override bool TryComputeLength(out long length)
    {
        length = 0;

        return false;
    }

    private sealed class ResettingStream(int healthyBytes) : Stream
    {
        private int _remaining = healthyBytes;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);

            return ReadCore(buffer.AsSpan(offset, count));
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReadCore(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int ReadCore(Span<byte> destination)
        {
            if (_remaining <= 0)
            {
                throw new IOException("The node's answer ended before its body was complete (scripted reset).");
            }

            var served = Math.Min(_remaining, destination.Length);
            _remaining -= served;
            destination[..served].Clear();

            return served;
        }
    }
}
