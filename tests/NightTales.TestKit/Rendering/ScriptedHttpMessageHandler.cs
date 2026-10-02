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
/// <param name="OnRequest">
/// A hook that runs inside the request — after the registry read of the caller's tick and before the
/// response is handed back — so a test can inject the mid-pull race a code review needs (for example an
/// operator's <c>set-state</c> landing while the pull is in flight).
/// </param>
/// <param name="OmitContentLength">Serve <paramref name="Body"/> without a <c>Content-Length</c> header, like a chunked answer.</param>
/// <param name="StallBody">
/// Answer the headers immediately, then never complete the body: the health-pull deadline, not the client
/// timeout, is what must end such an answer.
/// </param>
public sealed record ScriptedHttpStep(
    HttpStatusCode StatusCode = HttpStatusCode.OK,
    string Body = "{}",
    TimeSpan Delay = default,
    Exception? Failure = null,
    Func<CancellationToken, Task>? OnRequest = null,
    bool OmitContentLength = false,
    bool StallBody = false);

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
    private IReadOnlyList<ScriptedHttpStep> _steps = steps;
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

    /// <summary>
    /// Replaces the script from the next call on. A client that was already handed out keeps this same
    /// handler, so a mid-test re-script is honoured (the poller pools its clients per node).
    /// </summary>
    /// <param name="steps">The new answers, in call order; at least one is required.</param>
    internal void Rescript(IReadOnlyList<ScriptedHttpStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
        {
            throw new ArgumentException("At least one scripted step is required.", nameof(steps));
        }

        lock (_gate)
        {
            _steps = steps;
            _calls = 0;
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

            step = _steps[Math.Min(_calls, _steps.Count - 1)];
            _calls++;
        }

        if (step.Delay > TimeSpan.Zero)
        {
            await Task.Delay(step.Delay, cancellationToken).ConfigureAwait(false);
        }

        if (step.OnRequest is { } onRequest)
        {
            await onRequest(cancellationToken).ConfigureAwait(false);
        }

        if (step.Failure is not null)
        {
            throw step.Failure;
        }

        if (step.StallBody)
        {
            return new HttpResponseMessage(step.StatusCode)
            {
                Content = new StallingBodyContent(),
            };
        }

        var content = new StringContent(step.Body, Encoding.UTF8, "application/json");
        if (step.OmitContentLength)
        {
            // SocketsHttpHandler serves responses without a Content-Length as chunked; the fake mirrors
            // that by simply dropping the computed header, so the reader must count bytes itself.
            content.Headers.ContentLength = null;
        }

        return new HttpResponseMessage(step.StatusCode)
        {
            Content = content,
        };
    }
}

/// <summary>An answer whose headers exist but whose body never does; cancellation ends it (no busy-wait).</summary>
internal sealed class StallingBodyContent : HttpContent
{
    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        Task.Delay(Timeout.InfiniteTimeSpan);

    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken) =>
        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    /// <inheritdoc />
    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new StallingStream());

    /// <inheritdoc />
    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
        Task.FromResult<Stream>(new StallingStream());

    /// <inheritdoc />
    protected override bool TryComputeLength(out long length)
    {
        length = 0;

        return false;
    }

    /// <summary>A stream whose reads block until the reader's token cancels, like a chunked body that stalls mid-flight.</summary>
    private sealed class StallingStream : Stream
    {
        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => 0;

        /// <inheritdoc />
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("The stalling body answers async reads only.");

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);

            return 0;
        }

        /// <inheritdoc />
        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            Task.FromException<int>(new NotSupportedException("The stalling body answers async reads only."));

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
