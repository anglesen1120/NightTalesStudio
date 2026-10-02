using System.Net;
using System.Net.Http;
using System.Text;

namespace NightTales.TestKit.Rendering;

/// <summary>
/// One scripted answer of a <see cref="ScriptedHttpMessageHandler"/>: the status and body the fake node
/// returns, an optional delay before it answers, or a failure it throws instead (11.3.3-T1 plan §9 —
/// "scripted <c>HttpMessageHandler</c>", no mocking library, 09 README D13).
/// </summary>
/// <param name="StatusCode">The response's status code; <see cref="HttpStatusCode.OK"/> by default.</param>
/// <param name="Body">The response body, served as <c>application/json</c>.</param>
/// <param name="Delay">A delay before the answer, which a short client timeout turns into a real timeout.</param>
/// <param name="Failure">An exception to throw instead of answering, standing in for a connect error or a pin mismatch.</param>
public sealed record ScriptedHttpStep(
    HttpStatusCode StatusCode = HttpStatusCode.OK,
    string Body = "{}",
    TimeSpan Delay = default,
    Exception? Failure = null);

/// <summary>One request a fake node received, as the handler saw it.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">The path and query, never the host.</param>
/// <param name="ApiKey">The <c>x-api-key</c> header value, or <c>null</c> when the caller sent none.</param>
public sealed record RecordedNodeRequest(string Method, string Path, string? ApiKey);

/// <summary>
/// A hand-written <see cref="HttpMessageHandler"/> (no mocking library, 09 README D13) that answers the
/// calls of one fake node in order: the n-th call gets the n-th <see cref="ScriptedHttpStep"/>, and every
/// call after the last keeps the last step. Each call is recorded, so a test asserts what the Worker sent
/// and how many times it sent it (11.3.3-T1 plan §9).
/// </summary>
/// <param name="steps">The answers, in call order; at least one is required.</param>
public sealed class ScriptedHttpMessageHandler(IReadOnlyList<ScriptedHttpStep> steps) : HttpMessageHandler
{
    private readonly List<RecordedNodeRequest> _requests = [];
    private readonly Lock _gate = new();
    private int _calls;

    /// <summary>Every call this node received, in order.</summary>
    public IReadOnlyList<RecordedNodeRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        ScriptedHttpStep step;
        lock (_gate)
        {
            _requests.Add(new RecordedNodeRequest(
                request.Method.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.TryGetValues("x-api-key", out var values) ? string.Join(",", values) : null));

            step = steps[Math.Min(_calls, steps.Count - 1)];
            _calls++;
        }

        if (step.Delay > TimeSpan.Zero)
        {
            await Task.Delay(step.Delay, cancellationToken).ConfigureAwait(false);
        }

        if (step.Failure is not null)
        {
            throw step.Failure;
        }

        return new HttpResponseMessage(step.StatusCode)
        {
            Content = new StringContent(step.Body, Encoding.UTF8, "application/json"),
        };
    }
}
