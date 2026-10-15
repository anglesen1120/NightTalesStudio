using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Domain.Rendering;
using NightTales.RenderNode.Contract;

namespace NightTales.Infrastructure.Rendering;

/// <summary>
/// The streaming download client of 09.6.2-T1 (09 README BD6, WF08): it copies one render output from a node's
/// <c>mpt-adapter</c> straight into the caller's stream — never buffering the file, never allocating it —
/// hashing what it wrote incrementally and answering the byte count, the SHA-256 and the media type the
/// ingest of 09.6.3-T2 verifies against the adapter's own report.
/// </summary>
/// <remarks>
/// <para>
/// The transport is the node client <see cref="IRenderNodeConnectionFactory"/> builds (11 README F13/D13): the
/// base URL, the <c>x-api-key</c> header and the pinned certificate are decided there and nowhere else, so
/// this class never constructs an <see cref="HttpClient"/> and never reads the key. Everything it logs — or
/// puts into a failure — is the node's name, the task id, the kind, the status and a byte count: never the
/// URL, never the key, never a body excerpt (BD16/S4).
/// </para>
/// <para>
/// It deliberately has no timeout of its own: the factory's per-pull timeout is replaced by
/// <see cref="Timeout.InfiniteTimeSpan"/>, because a 2 GiB video over a slow node link legitimately takes
/// minutes (WF08). Two timers bound the transfer instead, both created through <see cref="TimeProvider"/> so a
/// test drives them with a manual provider: an idle timer (<c>Render:Download:IdleSeconds</c>, 60 s) that is
/// re-armed on every chunk that arrives and ends a stalled node, and a one-shot total budget
/// (<c>Render:DownloadMaxMinutes</c>, 20 min) that ends a download which moves but never finishes.
/// </para>
/// <para>
/// Redirects are never followed — the client the factory builds has <c>AllowAutoRedirect = false</c> — and a
/// 3xx that still arrives is mapped to a terminal <c>render_node_error</c> rather than followed off the pinned
/// host (05 D4).
/// </para>
/// <para>
/// The caller's cancellation is never swallowed: it leaves this class as an
/// <see cref="OperationCanceledException"/>. Every other failure leaves whatever bytes already reached
/// <paramref name="destination"/> in that stream — the caller owns it (the ingest's temp file) and discards
/// it, then retries from scratch (05 D4, BD7). A <see cref="System.IO.IOException"/> raised by
/// <paramref name="destination"/> itself is the caller's own stream failing and is never reported as a node
/// fault.
/// </para>
/// </remarks>
/// <param name="connectionFactory">The only producer of node clients (11 README F13).</param>
/// <param name="options">The <c>Render</c> section: the idle timer, the total budget and the two byte caps.</param>
/// <param name="timeProvider">The clock the idle timer and the total budget run on (09 README D3).</param>
/// <param name="logger">The client's log; it carries no node secret and no URL.</param>
public sealed partial class RenderDownloadClient(
    IRenderNodeConnectionFactory connectionFactory,
    IOptions<RenderClientOptions> options,
    TimeProvider timeProvider,
    ILogger<RenderDownloadClient> logger) : IRenderDownloadClient
{
    /// <summary>
    /// The name of the download client in the host's HTTP convention. The request itself never goes through
    /// the factory's named clients: only <see cref="IRenderNodeConnectionFactory"/> can build a client that
    /// carries the node's base URL, its key and its pinned certificate, and redirects are off there (F13).
    /// </summary>
    public const string HttpClientName = "render-node-download";

    /// <summary>The copy chunk: 80 KiB, hashed and written as it arrives, so the file is never materialised.</summary>
    private const int ChunkBytes = 80 * 1024;

    /// <summary>The most of an error body that is ever read to find the adapter's <c>error.code</c>.</summary>
    private const int ErrorBodyMaxBytes = 64 * 1024;

    /// <summary>The response header carrying the SHA-256 the adapter promises for the bytes it serves.</summary>
    private const string ContentSha256Header = "X-Content-SHA256";

    /// <summary>The media type of the video output (05 D4).</summary>
    private const string VideoContentType = "video/mp4";

    /// <summary>The media type of the subtitle output; a charset parameter is tolerated (05 D4).</summary>
    private const string SubRipContentType = "application/x-subrip";

    /// <summary>The longest accepted task id: an adapter task id is a UUID, so anything longer is not one.</summary>
    private const int TaskIdMaxLength = 64;

    private const string UnsafeTaskIdDetail = "render_task_id_invalid";

    private const string InvalidCapDetail = "render_download_cap_invalid";

    private const string ContentTypeDetail = "render_output_content_type_mismatch";

    private const string TooLargeDetail = "render_output_too_large";

    private const string HashMismatchDetail = "render_output_sha256_mismatch";

    /// <summary>
    /// The download client's named-client configuration, so the host's registration of
    /// <see cref="HttpClientName"/> and a test build the same client: it carries no timeout of its own, because
    /// the idle timer and the total budget inside this class are what bound a transfer (WF08).
    /// </summary>
    /// <param name="client">The named client being registered.</param>
    public static void ConfigureHttpClient(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        client.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    /// The download client's primary-handler configuration, applied by the same registration: a node may never
    /// bounce the Worker to another host, so no redirect is followed and a 3xx that still arrives is mapped
    /// instead (05 D4).
    /// </summary>
    /// <param name="handler">The primary handler the named client is built over.</param>
    public static void ConfigurePrimaryHandler(SocketsHttpHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        handler.AllowAutoRedirect = false;
    }

    /// <inheritdoc />
    public async Task<ErrorOr<DownloadResult>> DownloadAsync(
        WorkerNode node,
        string taskId,
        RenderOutputKind kind,
        Stream destination,
        long maxBytes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(destination);

        // The task id becomes a path segment, so it is validated and escaped before any URL is built: an id
        // that is not a plain adapter id never reaches the node as a path (S8/A03).
        if (!IsSafeTaskId(taskId))
        {
            return Failed(
                node.Name, taskId, kind, null, 0, AdapterErrorMapper.OutputInvalid(UnsafeTaskIdDetail));
        }

        var cap = CapFor(kind, maxBytes);
        if (cap <= 0L)
        {
            return Failed(
                node.Name, taskId, kind, null, 0, AdapterErrorMapper.OutputInvalid(InvalidCapDetail));
        }

        using var client = connectionFactory.Create(node);
        client.Timeout = Timeout.InfiniteTimeSpan;

        using var request = new HttpRequestMessage(HttpMethod.Get, RouteFor(taskId, kind));

        // The caller's cancellation must end a read that is stuck in the body too, so every read runs on a
        // source linked to it: whichever comes first — the caller, the idle timer or the total budget — ends
        // the transfer, and the catches below tell the three apart.
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var idle = TimeSpan.FromSeconds(options.Value.Download.IdleSeconds);
        var budget = TimeSpan.FromMinutes(options.Value.DownloadMaxMinutes);

        // Both timers cancel the same source: one link covers the header phase and every chunk of the body, so
        // whatever the transport is waiting for, no phase of the download is unbounded (WF08).
        using var idleTimer = timeProvider.CreateTimer(CancelQuietly, abort, idle, Timeout.InfiniteTimeSpan);
        using var budgetTimer = timeProvider.CreateTimer(CancelQuietly, abort, budget, Timeout.InfiniteTimeSpan);

        HttpResponseMessage response;
        try
        {
            response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, abort.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();

            return Failed(node.Name, taskId, kind, null, 0, TimeoutFailure());
        }
        catch (HttpRequestException exception)
        {
            return Failed(node.Name, taskId, kind, null, 0, Transport(), exception.GetType().Name);
        }

        using (response)
        {
            var status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, abort.Token, ct).ConfigureAwait(false);

                return Failed(node.Name, taskId, kind, status, 0, AdapterErrorMapper.Map(error, taskSubmitted: true));
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!IsExpected(contentType, kind))
            {
                return Failed(
                    node.Name,
                    taskId,
                    kind,
                    status,
                    0,
                    AdapterErrorMapper.OutputInvalid(ContentTypeDetail));
            }

            // A Content-Length the node declared is checked before a single byte is read; an absent or lying one
            // is caught by the running count below.
            if (response.Content.Headers.ContentLength is { } length && length > cap)
            {
                return Failed(node.Name, taskId, kind, status, 0, AdapterErrorMapper.OutputInvalid(TooLargeDetail));
            }

            return await StreamAsync(
                    node,
                    taskId,
                    kind,
                    destination,
                    cap,
                    contentType!,
                    status,
                    response,
                    idleTimer,
                    idle,
                    abort,
                    ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Copies the response body into <paramref name="destination"/> in <see cref="ChunkBytes"/> chunks, hashing
    /// each chunk as it is written, and verifies the declared SHA-256 before answering.
    /// </summary>
    private async Task<ErrorOr<DownloadResult>> StreamAsync(
        WorkerNode node,
        string taskId,
        RenderOutputKind kind,
        Stream destination,
        long cap,
        string contentType,
        int status,
        HttpResponseMessage response,
        ITimer idleTimer,
        TimeSpan idle,
        CancellationTokenSource abort,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        var total = 0L;
        var writingToDestination = false;

        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var body = await response.Content.ReadAsStreamAsync(abort.Token).ConfigureAwait(false);

            while (true)
            {
                idleTimer.Change(idle, Timeout.InfiniteTimeSpan);

                var read = await body
                    .ReadAsync(buffer.AsMemory(0, ChunkBytes), abort.Token)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                // The cap is enforced on the bytes that are actually arriving, so a node that omits or lies
                // about its Content-Length cannot hand the Worker a file it did not ask for (BD6).
                if (total + read > cap)
                {
                    return Failed(node.Name, taskId, kind, status, total, AdapterErrorMapper.OutputInvalid(TooLargeDetail));
                }

                hasher.AppendData(buffer, 0, read);

                writingToDestination = true;
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                writingToDestination = false;

                total += read;
            }

            var sha256 = Convert.ToHexStringLower(hasher.GetHashAndReset());

            // 05 D4: the adapter takes X-Content-SHA256 from its own measured check and never re-hashes, so a
            // header that is present must match the bytes that were served. Compared without a data-dependent
            // exit, exactly like the certificate pin of 11.3.3-T1.
            if (DeclaredSha256(response) is { } declared && !HashMatches(declared, sha256))
            {
                return Failed(node.Name, taskId, kind, status, total, AdapterErrorMapper.OutputInvalid(HashMismatchDetail));
            }

            LogDownloaded(node.Name, taskId, kind, status, total);

            return new DownloadResult(total, sha256, contentType);
        }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();

            return Failed(node.Name, taskId, kind, status, total, TimeoutFailure());
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            // A read that breaks mid-body is the node's stream failing (a reset, a truncated answer); a write
            // that breaks is the caller's own destination (a full disk in the ingest), which is never a node
            // fault and leaves as it came in.
            if (writingToDestination)
            {
                throw;
            }

            return Failed(node.Name, taskId, kind, status, total, Transport(), exception.GetType().Name);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads the failed answer's envelope, bounded, to name the adapter's own <c>error.code</c>: an unparsable
    /// or over-long body answers <c>null</c>, which the mapper treats by its status alone. Nothing of the body
    /// is copied out (BD16).
    /// </summary>
    private static async Task<RenderClientError> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken abort,
        CancellationToken ct)
    {
        var status = (int)response.StatusCode;

        try
        {
            var body = await ReadBoundedAsync(response.Content, abort).ConfigureAwait(false);

            return JsonSerializer.Deserialize<AdapterErrorV1>(body, RenderNodeJson.Tolerant) is { Error.Code: { Length: > 0 } adapterCode }
                ? RenderClientError.Status(status, adapterCode, response.Headers.RetryAfter?.Delta)
                : RenderClientError.Status(status, null, response.Headers.RetryAfter?.Delta);
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException or IOException or OperationCanceledException)
        {
            // A failed answer whose envelope never arrived (a stall, a truncated body) is still a failed answer:
            // it is named by its status alone, which the mapper fails closed on for anything it does not know —
            // except that the caller's own cancellation is never swallowed here either.
            ct.ThrowIfCancellationRequested();

            return RenderClientError.Status(status, null, response.Headers.RetryAfter?.Delta);
        }
    }

    /// <summary>Reads at most <see cref="ErrorBodyMaxBytes"/> of a body, so a broken node cannot stream one forever.</summary>
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ErrorBodyMaxBytes);

        try
        {
            await using var body = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var bounded = new MemoryStream();

            while (bounded.Length < ErrorBodyMaxBytes)
            {
                var room = (int)Math.Min(buffer.Length, ErrorBodyMaxBytes - bounded.Length);
                var read = await body.ReadAsync(buffer.AsMemory(0, room), ct).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                bounded.Write(buffer, 0, read);
            }

            return bounded.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// The route of one output, built from the frozen 05.4.1-T2 §2 literals so the client and the adapter can
    /// never drift apart. The id is escaped even though <see cref="IsSafeTaskId"/> already refused everything
    /// that is not a plain adapter id.
    /// </summary>
    private static Uri RouteFor(string taskId, RenderOutputKind kind)
    {
        var template = kind == RenderOutputKind.Video
            ? RenderNodeRoutes.RenderVideo
            : RenderNodeRoutes.RenderSubtitles;

        var path = template.Replace(
            "{" + RenderNodeRoutes.TaskIdParameter + "}",
            Uri.EscapeDataString(taskId),
            StringComparison.Ordinal);

        // An absolute path against the node client's base address: the same shape the health poll uses, so a
        // base URL with a path never silently drops it (11.3.3-T1).
        return new Uri(path, UriKind.Relative);
    }

    /// <summary>Whether the id can be a path segment: non-empty, bounded and made of unreserved characters only.</summary>
    private static bool IsSafeTaskId(string taskId)
    {
        if (string.IsNullOrEmpty(taskId) || taskId.Length > TaskIdMaxLength)
        {
            return false;
        }

        foreach (var character in taskId)
        {
            // No dot: ".." in a path segment is exactly the traversal this check exists to refuse.
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The cap of this download: the stricter of the caller's <paramref name="maxBytes"/> and the configured
    /// cap of the kind, so a caller can tighten a cap but never widen one (BD6).
    /// </summary>
    private long CapFor(RenderOutputKind kind, long maxBytes)
    {
        var configured = kind == RenderOutputKind.Video
            ? options.Value.MaxVideoBytes
            : options.Value.MaxSubtitleBytes;

        return Math.Min(maxBytes, configured);
    }

    /// <summary>Whether the media type the node announced is the one this kind must be served as (parameters tolerated).</summary>
    private static bool IsExpected(string? mediaType, RenderOutputKind kind) =>
        string.Equals(
            mediaType,
            kind == RenderOutputKind.Video ? VideoContentType : SubRipContentType,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>The SHA-256 the node declared for the body, or <c>null</c> when it declared none.</summary>
    private static string? DeclaredSha256(HttpResponseMessage response) =>
        response.Headers.TryGetValues(ContentSha256Header, out var values) ? values.FirstOrDefault() : null;

    /// <summary>Compares two hex digests without a data-dependent early exit (as the certificate pin does).</summary>
    private static bool HashMatches(string declared, string computed) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(declared.Trim().ToLowerInvariant()),
            Encoding.ASCII.GetBytes(computed));

    /// <summary>A stalled transfer and an exhausted budget are both a timeout at the node (05 D5).</summary>
    private static RenderClientFailure TimeoutFailure() => AdapterErrorMapper.Map(RenderClientError.Timeout(), taskSubmitted: true);

    /// <summary>A transport that broke before or during the body is a transient node failure (05 D5).</summary>
    private static RenderClientFailure Transport() => AdapterErrorMapper.Map(RenderClientError.Transport(), taskSubmitted: true);

    /// <summary>
    /// Logs what was attempted — node, task id, kind, status, byte count, outcome — and hands the failure on.
    /// The URL, the key and the body never reach the log: the failure object carries the mapped code, never raw
    /// adapter text (BD16).
    /// </summary>
    private Error Failed(
        string node,
        string taskId,
        RenderOutputKind kind,
        int? status,
        long bytes,
        RenderClientFailure failure,
        string? error = null)
    {
        LogFailed(node, taskId, kind, status, bytes, failure.OutcomeCode ?? failure.Kind.ToString(), error);

        return RenderClientErrors.ToError(failure);
    }

    /// <summary>
    /// Cancels the transfer, ignoring a source that has already been disposed: a timer that fires while the
    /// method unwinds must never throw on a thread-pool thread.
    /// </summary>
    private static void CancelQuietly(object? state)
    {
        try
        {
            ((CancellationTokenSource)state!).Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The download finished (or failed) between the timer falling due and this callback running.
        }
    }

    [LoggerMessage(
        EventId = 2500,
        Level = LogLevel.Information,
        Message = "render-node.download.completed node={Node} task={TaskId} kind={Kind} status={Status} bytes={Bytes}")]
    private partial void LogDownloaded(string node, string taskId, RenderOutputKind kind, int status, long bytes);

    [LoggerMessage(
        EventId = 2501,
        Level = LogLevel.Warning,
        Message = "render-node.download.failed node={Node} task={TaskId} kind={Kind} status={Status} bytes={Bytes} outcome={Outcome} error={Error}")]
    private partial void LogFailed(string node, string taskId, RenderOutputKind kind, int? status, long bytes, string outcome, string? error);
}
