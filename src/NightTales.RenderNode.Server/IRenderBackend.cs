using ErrorOr;
using NightTales.RenderNode.Contract;

namespace NightTales.RenderNode.Server;

/// <summary>
/// The node-specific seam (11 README D5, F02): everything that touches MPT, the filesystem or the relay's
/// upstream lives behind this interface, which is why <c>NightTales.MockMpt</c> can reuse the whole core.
/// </summary>
public interface IRenderBackend
{
    /// <summary>
    /// Submits the validated body to MPT. <paramref name="mpt"/> is exactly what the backend sent (no
    /// <c>n_threads</c>, no <c>image_plan</c>); the adapter adds its own threads and <c>x-task-id</c>
    /// (05 D3, 05 D15 P6, 05 C3).
    /// </summary>
    Task<ErrorOr<BackendAccepted>> SubmitAsync(RenderTaskRecord record, MptSubmitFields mpt, CancellationToken cancellationToken);

    /// <summary>
    /// Notices the cancel. MPT has no cancel route, so the adapter marks the task and discards the outputs
    /// when MPT reaches a terminal state (05 D4, F08); this call must not throw for an already-cancelled task.
    /// </summary>
    Task CancelAsync(RenderTaskRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Removes MPT's task, its task dir and the relay's JSONL (03 D12). An error with code
    /// <see cref="AdapterErrorCodes.TaskBusy"/> means MPT still runs the task (C11, F08).
    /// </summary>
    Task<ErrorOr<Deleted>> DeleteAsync(RenderTaskRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the stored artefact for streaming. The backend takes <c>Length</c> and <c>Sha256</c> from the
    /// measured <c>render-check.v1</c> output, so the core never re-hashes a served file (D15 P2/P3). The
    /// caller owns the returned <see cref="OutputFile.Content"/> and MUST dispose it after the response.
    /// </summary>
    Task<ErrorOr<OutputFile>> OpenOutputAsync(Guid taskId, OutputKind kind, CancellationToken cancellationToken);
}

/// <summary>The two artefacts a finished task can serve (§2).</summary>
public enum OutputKind
{
    /// <summary><c>final-1.norm.mp4</c> as <c>video/mp4</c>.</summary>
    Video,

    /// <summary>The subtitles as <c>application/x-subrip; charset=utf-8</c>.</summary>
    Subtitles,
}

/// <summary>What the backend accepted: MPT's id plus its start time, both stored on the row.</summary>
public readonly record struct BackendAccepted(string BackendTaskId, DateTimeOffset? StartedAt);

/// <summary>An open artefact stream; <see cref="Stream"/> is streamed to the client, never buffered whole (Q3).</summary>
public sealed record OutputFile(Stream Content, long Length, string Sha256, string ContentType);
