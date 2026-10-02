using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;

namespace NightTales.MptAdapter;

/// <summary>
/// The <c>mpt_*</c> error codes <see cref="MptClient"/> answers with. They never reach a status body: the
/// poller turns them into a state or an attempt, and the submit and delete paths map their own failure to
/// <c>adapter_node_unhealthy</c> (05.4.1-T2 §1 keeps the status vocabulary closed).
/// </summary>
public static class MptErrorCodes
{
    /// <summary>Transport failure or read timeout — the task stays non-terminal and the poller backs off.</summary>
    public const string Unreachable = "mpt_unreachable";

    /// <summary>MPT answered 401/403: the node's key is wrong or rotated. No state change, log once.</summary>
    public const string Unauthorized = "mpt_unauthorized";

    /// <summary>MPT answers 404 for a task it accepted: D6's <c>render_lost</c> / <c>mpt_task_missing</c>.</summary>
    public const string TaskNotFound = "mpt_task_not_found";

    /// <summary>MPT's queue is full (429 on submit, C10): no task was created, retrying later is safe.</summary>
    public const string QueueFull = "mpt_task_queue_full";

    /// <summary>MPT refuses a mutable request (409 on DELETE while the task still runs, C11).</summary>
    public const string Busy = "mpt_task_busy";

    /// <summary>Any other answer from MPT, including a 5xx.</summary>
    public const string ServerError = "mpt_server_error";

    /// <summary>MPT answered 200 with a body the contract cannot read.</summary>
    public const string InvalidAnswer = "mpt_invalid_answer";
}

/// <summary>
/// The HTTP client every <see cref="MptClient"/> must be built on (D6): MPT is loopback-only traffic that the
/// adapter owns, so the handler is created here instead of left to the host's defaults.
/// </summary>
/// <remarks>
/// Redirects are off because D6 freezes the two MPT routes and a 3xx means the node is talking to something
/// else; the connect timeout is D6's 2 s. The read timeout is <see cref="MptClient"/>'s per-request timeout, so
/// <see cref="HttpClient.Timeout"/> is infinite and the two never fight.
/// </remarks>
public static class MptHttpClient
{
    /// <summary>The handler behind <see cref="Create"/>; public so the two settings stay asserted.</summary>
    public static SocketsHttpHandler CreateHandler(AdapterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1, options.MptConnectTimeoutSeconds)),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
        };
    }

    /// <summary>The client to hand to <c>MptClient</c>.</summary>
    public static HttpClient Create(AdapterOptions options) =>
        new(CreateHandler(options)) { Timeout = Timeout.InfiniteTimeSpan };
}

/// <summary>
/// MPT's task observables plus its raw <c>error</c> text, which <see cref="MptStateV1"/> deliberately does not
/// carry: the snapshot is stored (05 D4), the raw text only goes to the journal (S4).
/// </summary>
public sealed record MptTaskStatus(MptStateV1 Snapshot, string? Error);

/// <summary>
/// The MPT side of <see cref="IRenderBackend"/> (11 README D6, 05 C3): submit, status and delete on loopback
/// with <c>x-api-key</c> and <c>x-task-id</c>. This is the only place the MPT key is read (S3/S6) and the only
/// component that writes <c>n_threads</c> (05 D15 P6).
/// </summary>
/// <remarks>
/// MPT has no cancel route and refuses to delete a running task (05 D4/C11), so <see cref="CancelAsync"/>
/// only notes the intent — the poller finalises the task when MPT reports a terminal state. The connect
/// timeout is a handler setting the host applies; the read timeout is enforced here per request, because a
/// half-open loopback socket must never hold the poller for longer than D6's 30 s.
/// </remarks>
public sealed class MptClient : IRenderBackend
{
    /// <summary>The environment variable the key is read from (D11). Never logged, never in a URL.</summary>
    public const string ApiKeyEnvironmentVariable = "NTS_MPT_API_KEY";

    /// <summary>05 C3's header carrying the adapter's own task id (MPT echoes it back).</summary>
    public const string TaskIdHeader = "x-task-id";

    /// <summary>05 C2's header carrying the node's MPT key.</summary>
    public const string ApiKeyHeader = "x-api-key";

    /// <summary>05.4.1-T2's submit route.</summary>
    public const string SubmitPath = "api/v1/videos";

    /// <summary>MPT's task route prefix; MPT's own id follows it (mpt-routes.pinned.json).</summary>
    public const string TaskPathPrefix = "api/v1/tasks/";

    /// <summary>MPT's liveness route (D7's health probe).</summary>
    public const string PingPath = "ping";

    /// <summary>MPT's authenticated list route, the D7 probe that proves the key (page size 1).</summary>
    public const string TasksProbePath = "api/v1/tasks?page=1&page_size=1";

    /// <summary>
    /// SEC-11.3.5-T2-01: the largest MPT answer body the adapter reads. The contract's largest answer is
    /// MPT's task dict (<c>script</c>, <c>terms</c>, <c>materials</c> — the S3 samples are a few kilobytes),
    /// so 8 MiB is two orders of magnitude of margin while still bounding what a broken or hostile node can
    /// make the adapter buffer. Over the cap answers <c>mpt_server_error</c> and the body is never logged.
    /// </summary>
    public const int MaxResponseBodyBytes = 8 * 1024 * 1024;

    /// <summary>The chunk size of the counted body read.</summary>
    private const int ReadChunkBytes = 16 * 1024;

    private readonly HttpClient _http;
    private readonly ILogger<MptClient> _log;
    private readonly Uri _baseAddress;
    private readonly string _apiKey;
    private readonly int _threads;
    private readonly TimeSpan _readTimeout;
    private readonly IMptProcessStartReader _startReader;
    private readonly TaskDirCleaner _cleaner;

    private readonly AdapterOptions _options;
    private readonly FileOutputSource? _outputs;

    public MptClient(
        HttpClient http,
        IOptions<AdapterOptions> options,
        IOptions<RenderNodeServerOptions> serverOptions,
        IMptProcessStartReader startReader,
        TaskDirCleaner cleaner,
        ILogger<MptClient> log,
        FileOutputSource? outputs = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serverOptions);
        ArgumentNullException.ThrowIfNull(startReader);
        ArgumentNullException.ThrowIfNull(cleaner);
        ArgumentNullException.ThrowIfNull(log);

        _http = http;
        _log = log;
        _startReader = startReader;
        _cleaner = cleaner;
        _outputs = outputs;

        var bound = options.Value;
        _options = bound;
        _baseAddress = AdapterOptions.ParseLoopbackMptBaseUrl(bound.MptBaseUrl);
        _readTimeout = TimeSpan.FromSeconds(Math.Max(1, bound.MptReadTimeoutSeconds));
        _threads = bound.ResolveMptThreads(serverOptions.Value.MaxConcurrentTasks);

        // S3/S6: read once at construction and never log the value (only whether it exists).
        _apiKey = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable) ?? string.Empty;
        if (_apiKey.Length == 0)
        {
            _log.MptApiKeyMissing(ApiKeyEnvironmentVariable);
        }
    }

    /// <summary>05 D15 P6's resolved <c>n_threads</c>; exposed for the host's startup log line.</summary>
    public int Threads => _threads;

    /// <inheritdoc />
    public async Task<ErrorOr<BackendAccepted>> SubmitAsync(
        RenderTaskRecord record,
        MptSubmitFields mpt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(mpt);

        using var request = CreateRequest(HttpMethod.Post, SubmitPath, record.TaskId);
        request.Content = new StringContent(BuildSubmitBody(mpt), Encoding.UTF8, "application/json");

        var answer = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (answer.IsError)
        {
            return answer.Errors;
        }

        var status = answer.Value;
        if (status.Status != HttpStatusCode.OK)
        {
            return Error.Failure(CodeFor(status.Status, submit: true), Describe("submit", status.Status));
        }

        var payload = ReadPayload(status.Body);
        if (payload?.Data?.TaskId is not { } mptTaskId)
        {
            return Error.Failure(MptErrorCodes.InvalidAnswer, "MPT's submit answer carries no data.task_id.");
        }

        // D6's lost rule compares a later process start against this instant, so it is read here, at submit.
        var startedAt = await _startReader.ReadAsync(cancellationToken).ConfigureAwait(false);

        // T3: the check's --aspect/--subtitles come from this body (D6a); persisted so a restart re-checks alike.
        try
        {
            await SubmitMarker.WriteAsync(_options, record.TaskId, mpt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The post-processor fails the check (check_failed) when the marker is missing.
            _log.PathRemoveFailed(SubmitMarker.PathFor(_options, record.TaskId), exception.GetType().Name);
        }

        return new BackendAccepted(mptTaskId.ToString("D", CultureInfo.InvariantCulture), startedAt);
    }

    /// <inheritdoc />
    /// <remarks>MPT has no cancel route (05 D4, F08): nothing is called, the poller finalises the task.</remarks>
    public Task CancelAsync(RenderTaskRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        _log.CancelNoted(record.TaskId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<ErrorOr<Deleted>> DeleteAsync(RenderTaskRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        // C11/F08: MPT refuses to delete a running task, so the adapter answers busy before calling anything.
        if (!record.IsTerminal)
        {
            return Error.Conflict(AdapterErrorCodes.TaskBusy, "MPT still runs this task.");
        }

        if (record.BackendTaskId is { Length: > 0 } mptTaskId)
        {
            using var request = CreateRequest(
                HttpMethod.Delete,
                TaskPath(mptTaskId),
                record.TaskId);

            var answer = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (answer.IsError)
            {
                // 05.4.1-T2 §1 keeps the Worker's status vocabulary closed: an mpt_* code stays in the
                // journal and the delete answers the adapter's own backend-unavailable code.
                return DeleteFailed(record.TaskId, answer.FirstError.Code);
            }

            var status = answer.Value;
            switch (status.Status)
            {
                case HttpStatusCode.OK:
                case HttpStatusCode.NotFound:
                    // S13: a 404 means the task (and its dir) are already gone — the goal of the delete.
                    break;
                case HttpStatusCode.Conflict:
                    return Error.Conflict(AdapterErrorCodes.TaskBusy, "MPT refuses to delete a running task.");
                default:
                    return DeleteFailed(record.TaskId, CodeFor(status.Status, submit: false));
            }
        }

        // The dirs are removed after MPT: the task dir, the two relay files and the adapter's own report dir.
        await _cleaner.CleanAsync(record.TaskId, record.BackendTaskId, cancellationToken).ConfigureAwait(false);
        return Result.Deleted;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Served by <see cref="FileOutputSource"/> (11.3.5-T3) from the stored output and the stored
    /// <c>backend_task_id</c>; a client built without it (the T1/T2 tests) has no published artefact.
    /// </remarks>
    public Task<ErrorOr<OutputFile>> OpenOutputAsync(Guid taskId, OutputKind kind, CancellationToken cancellationToken)
    {
        if (_outputs is not null)
        {
            return _outputs.OpenAsync(taskId, kind, cancellationToken);
        }

        _log.OutputNotPublished(taskId, kind);
        return Task.FromResult<ErrorOr<OutputFile>>(
            Error.NotFound(AdapterErrorCodes.OutputAbsent, "The adapter publishes outputs after the check (11.3.5-T3)."));
    }

    /// <summary>
    /// MPT's task observables, or the error the poller maps (a 404 is the lost rule's first half, D6).
    /// </summary>
    public async Task<ErrorOr<MptTaskStatus>> GetStatusAsync(
        string mptTaskId,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(mptTaskId);

        using var request = CreateRequest(
            HttpMethod.Get,
            TaskPath(mptTaskId),
            taskId);

        var answer = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (answer.IsError)
        {
            return answer.Errors;
        }

        var status = answer.Value;
        switch (status.Status)
        {
            case HttpStatusCode.OK:
                var payload = ReadPayload(status.Body);
                if (payload?.Data is not { } data)
                {
                    return Error.Failure(MptErrorCodes.InvalidAnswer, "MPT's task answer carries no data block.");
                }

                return new MptTaskStatus(
                    // T69: MPT's free-form warnings are projected onto the closed code set, never stored raw.
                    new MptStateV1(
                        data.State,
                        data.Progress,
                        data.FailedStage,
                        MptWarnings.Project(data.Warnings)),
                    data.Error);
            case HttpStatusCode.NotFound:
                return Error.NotFound(MptErrorCodes.TaskNotFound, "MPT no longer knows this task.");
            case HttpStatusCode.Unauthorized:
            case HttpStatusCode.Forbidden:
                return Error.Unauthorized(MptErrorCodes.Unauthorized, "MPT rejected the node's key.");
            default:
                return Error.Failure(MptErrorCodes.ServerError, Describe("status", status.Status));
        }
    }

    /// <summary>MPT's liveness (<c>GET /ping</c>, D7) — the probe that does not need the key to mean anything.</summary>
    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, PingPath, taskId: null);
        var answer = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return !answer.IsError && answer.Value.Status == HttpStatusCode.OK;
    }

    /// <summary>
    /// MPT's authenticated probe (D7: <c>GET /api/v1/tasks?page=1&amp;page_size=1</c>), so the health source can
    /// tell <c>mpt_unreachable</c> from <c>mpt_unauthorized</c>. Unauthorized is not an error here.
    /// </summary>
    public async Task<ErrorOr<Success>> CheckApiKeyAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, TasksProbePath, taskId: null);
        var answer = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (answer.IsError)
        {
            return answer.Errors;
        }

        var status = answer.Value;
        return status.Status switch
        {
            HttpStatusCode.OK => Result.Success,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                Error.Unauthorized(MptErrorCodes.Unauthorized, "MPT rejected the node's key."),
            _ => Error.Failure(MptErrorCodes.ServerError, Describe("probe", status.Status)),
        };
    }

    /// <summary>
    /// The D3 key set plus P6's <c>n_threads</c>. <see cref="MptSubmitFields"/> carries every other field with
    /// its frozen name, so the only way an extra member appears is an explicit add here.
    /// </summary>
    internal string BuildSubmitBody(MptSubmitFields mpt)
    {
        ArgumentNullException.ThrowIfNull(mpt);

        var node = JsonSerializer.SerializeToNode(mpt, RenderNodeJson.Tolerant)?.AsObject()
            ?? throw new InvalidOperationException("The MPT submit body could not be built.");
        node["n_threads"] = _threads;
        return node.ToJsonString(RenderNodeJson.Tolerant);
    }

    private async Task<ErrorOr<MptHttpAnswer>> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_readTimeout);

        try
        {
            // SEC-11.3.5-T2-01: headers first, then a byte-counted read. The declared length is an early
            // rejection, but the count is what decides — the chunked shape (no Content-Length) must obey the
            // same cap, and a body the adapter will not use is never buffered.
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (response.Content.Headers.ContentLength is { } declared && declared > MaxResponseBodyBytes)
            {
                return AnswerTooLarge(request);
            }

            var body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            return body is null
                ? AnswerTooLarge(request)
                : new MptHttpAnswer(response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log.MptTimeout(request.Method.Method, request.RequestUri?.AbsolutePath, _readTimeout.TotalSeconds);
            return Error.Failure(MptErrorCodes.Unreachable, "MPT timed out.");
        }
        catch (HttpRequestException exception)
        {
            // Only the transport error's classification is logged: never the body, never the key (S7).
            _log.MptUnreachable(
                request.Method.Method,
                request.RequestUri?.AbsolutePath,
                exception.HttpRequestError);
            return Error.Failure(MptErrorCodes.Unreachable, "MPT is unreachable.");
        }
    }

    /// <summary>
    /// Reads the body under the request's timeout while counting the bytes (SEC-11.3.5-T2-01). Null means the
    /// answer went over <see cref="MaxResponseBodyBytes"/>: the rest of the body is dropped unread.
    /// </summary>
    private static async Task<string?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[ReadChunkBytes];
        using var captured = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (captured.Length + read > MaxResponseBodyBytes)
            {
                return null;
            }

            captured.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(captured.GetBuffer(), 0, (int)captured.Length);
    }

    /// <summary>
    /// The fixed answer for a body over the cap: the shape is logged (method, path, cap) and never a byte of
    /// the body (S7), and the caller sees a backend error, not a parse failure.
    /// </summary>
    private ErrorOr<MptHttpAnswer> AnswerTooLarge(HttpRequestMessage request)
    {
        _log.MptResponseTooLarge(request.Method.Method, request.RequestUri?.AbsolutePath, MaxResponseBodyBytes);
        return Error.Failure(MptErrorCodes.ServerError, "MPT's answer is larger than the adapter reads.");
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, Guid? taskId)
    {
        var request = new HttpRequestMessage(method, new Uri(_baseAddress, path));
        request.Headers.TryAddWithoutValidation(ApiKeyHeader, _apiKey);
        if (taskId is { } id)
        {
            request.Headers.TryAddWithoutValidation(TaskIdHeader, id.ToString("D", CultureInfo.InvariantCulture));
        }

        return request;
    }

    private MptAnswer? ReadPayload(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<MptAnswer>(body, RenderNodeJson.Tolerant);
        }
        catch (JsonException exception)
        {
            // SEC-11.3.5-T2-02: the parser reason quotes the offending body, so it is neutralised like every
            // other MPT-supplied string before it reaches the journal.
            _log.MptUnreadableBody(AdapterText.Sanitize(exception.Message));
            return null;
        }
    }

    /// <summary>
    /// Maps a failed MPT delete to the Worker's contract: the raw <c>mpt_*</c> code is logged and the status
    /// body carries only <c>adapter_node_unhealthy</c> (503), the node's code for a render backend it cannot
    /// reach (05.4.1-T2 §1).
    /// </summary>
    private Error DeleteFailed(Guid taskId, string mptCode)
    {
        _log.DeleteBackendFailed(taskId, mptCode);
        return Error.Failure(AdapterErrorCodes.NodeUnhealthy, "MPT is unavailable for the delete.");
    }

    private static string CodeFor(HttpStatusCode status, bool submit) => status switch
    {
        HttpStatusCode.TooManyRequests when submit => MptErrorCodes.QueueFull,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => MptErrorCodes.Unauthorized,
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => MptErrorCodes.Unreachable,
        _ => MptErrorCodes.ServerError,
    };

    private static string Describe(string operation, HttpStatusCode status) =>
        FormattableString.Invariant($"MPT answered {(int)status} to the {operation}.");

    private static string TaskPath(string mptTaskId) => string.Concat(TaskPathPrefix, mptTaskId);

    /// <summary>MPT's own answer: an HTTP status plus the small envelope of 05.4.1-T1's samples.</summary>
    private readonly record struct MptHttpAnswer(HttpStatusCode Status, string Body);

    /// <summary>
    /// The envelope of <c>mpt-api.md</c> S3/S11/S13. Every member but <c>data</c> is ignored — the contract's
    /// authority is the HTTP status — and the read is tolerant, so MPT's much larger task dict (script, terms,
    /// materials, paths) is never materialised here and can never reach a log or the store (S7).
    /// </summary>
    internal sealed record MptAnswer(
        [property: JsonPropertyName("status")] int Status,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("data")] MptTaskBody? Data);

    /// <summary>The five task-dict members the adapter keeps; everything else stays in MPT.</summary>
    internal sealed record MptTaskBody(
        [property: JsonPropertyName("task_id")] Guid? TaskId,
        [property: JsonPropertyName("state")] int? State,
        [property: JsonPropertyName("progress")] int Progress,
        [property: JsonPropertyName("failed_stage")] string? FailedStage,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("warnings")] IReadOnlyList<string>? Warnings);
}
