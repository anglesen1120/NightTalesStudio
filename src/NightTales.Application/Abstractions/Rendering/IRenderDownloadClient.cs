using ErrorOr;
using NightTales.Domain.Rendering;

namespace NightTales.Application.Abstractions.Rendering;

/// <summary>
/// The streaming download of one render output (09 README BD6, WF08): the bytes go straight into the
/// caller's <see cref="Stream"/> (the ingest job's temp file), hashed incrementally, with the byte cap
/// enforced before and during the read. The client never buffers the file and never allocates it.
/// </summary>
/// <remarks>
/// <para>
/// Failures come back as an <see cref="Error"/> whose metadata carries a
/// <see cref="RenderClientFailure"/> (see <see cref="RenderClientErrors"/>): a size or content-type
/// violation (and a text or number anomaly) is terminal with outcome code <c>render_output_invalid</c>, a
/// stall is transient, a redirect is terminated with <c>render_node_error</c>, and a missing subtitle
/// track is <see cref="RenderClientFailureKind.OutputAbsent"/> — which the caller reads as "write nothing",
/// not as a failed job (05 D10).
/// </para>
/// <para>
/// <b>The destination keeps whatever the client already wrote on <em>every</em> error</b> (09.6.2-T1 Notes
/// H1): the client never truncates and never rewinds it, so a failed attempt — a cap trip, an unexpected
/// content type, a stall, a terminated redirect, an <c>IOException</c> raised by the destination itself or the
/// caller's cancellation — leaves partial bytes in it. The caller therefore owns two obligations: it downloads
/// into a temp file and commits that file only after this call succeeded, and it compares the returned
/// <see cref="DownloadResult.Bytes"/> and <see cref="DownloadResult.Sha256"/> with the adapter's own report
/// itself. Both the destination's <c>IOException</c> and the <c>OperationCanceledException</c> of a cancelled
/// attempt surface from this call and leave the same partial file to discard; the ingest retries from scratch,
/// idempotent by the SHA-256 it compares.
/// </para>
/// </remarks>
public interface IRenderDownloadClient
{
    /// <summary>Downloads one output of <paramref name="taskId"/> into <paramref name="destination"/>.</summary>
    /// <param name="node">The node the task was submitted to.</param>
    /// <param name="taskId">The adapter's task id.</param>
    /// <param name="kind">Which route to read; each kind has its own expected content type.</param>
    /// <param name="destination">The stream the bytes are written to; never rewinded, never closed, and left
    /// holding whatever was written when the call fails (H1).</param>
    /// <param name="maxBytes">The cap of this kind (<c>Render:MaxVideoBytes</c> or <c>Render:MaxSubtitleBytes</c>).</param>
    /// <param name="ct">The caller's cancellation.</param>
    /// <returns>The bytes written and their SHA-256, or a mapped failure.</returns>
    Task<ErrorOr<DownloadResult>> DownloadAsync(
        WorkerNode node, string taskId, RenderOutputKind kind, Stream destination, long maxBytes, CancellationToken ct);
}
