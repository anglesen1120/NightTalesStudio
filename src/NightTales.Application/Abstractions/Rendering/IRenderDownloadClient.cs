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
/// A transport failure mid-stream leaves a partial file in <paramref name="destination"/>: the caller owns
/// that stream and discards it (the ingest retries from scratch, idempotent by the SHA-256 it compares).
/// </para>
/// </remarks>
public interface IRenderDownloadClient
{
    /// <summary>Downloads one output of <paramref name="taskId"/> into <paramref name="destination"/>.</summary>
    /// <param name="node">The node the task was submitted to.</param>
    /// <param name="taskId">The adapter's task id.</param>
    /// <param name="kind">Which route to read; each kind has its own expected content type.</param>
    /// <param name="destination">The stream the bytes are written to; never rewinded, never closed.</param>
    /// <param name="maxBytes">The cap of this kind (<c>Render:MaxVideoBytes</c> or <c>Render:MaxSubtitleBytes</c>).</param>
    /// <param name="ct">The caller's cancellation.</param>
    /// <returns>The bytes written and their SHA-256, or a mapped failure.</returns>
    Task<ErrorOr<DownloadResult>> DownloadAsync(
        WorkerNode node, string taskId, RenderOutputKind kind, Stream destination, long maxBytes, CancellationToken ct);
}
