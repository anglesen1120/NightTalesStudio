namespace NightTales.Application.Abstractions.Rendering;

/// <summary>
/// The five shapes a failed render-node call can have (09 README BD6, 05 README D5). A caller switches on
/// the kind instead of on an HTTP status, so the adapter's status-to-outcome mapping stays in one place.
/// </summary>
public enum RenderClientFailureKind
{
    /// <summary>
    /// The node cannot take the work now but will later (<c>adapter_capacity_full</c>,
    /// <c>adapter_node_unhealthy</c>, <c>adapter_task_busy</c>): the job is rescheduled after
    /// <see cref="RenderClientFailure.RetryAfter"/> and spends no retry budget (11 T6).
    /// </summary>
    Deferred,

    /// <summary>
    /// The call may succeed on another attempt or another node (5xx after the pipeline, transport error,
    /// timeout): outcome code <c>render_node_error</c>, retryable by the job's retry policy (05 D5).
    /// </summary>
    TransientFailure,

    /// <summary>
    /// The call must not be retried as-is: a contract, key or idempotency fault carries
    /// <see cref="RenderClientFailure.Alert"/> so an operator sees it, and a lost task carries
    /// <c>render_lost</c>, which the job's retry policy may retry as a fresh attempt (05 D5).
    /// </summary>
    TerminalFailure,

    /// <summary>
    /// <c>adapter_output_not_ready</c> (409): the task finished but the output is not readable yet; the
    /// ingest job retries the download (05 D5).
    /// </summary>
    OutputNotReady,

    /// <summary>
    /// <c>adapter_output_absent</c> (404) for <see cref="RenderOutputKind.Subtitles"/>: the node has no such
    /// output. This is <em>not</em> a job failure — the ingest writes the video asset only and leaves the
    /// <c>subtitles_present</c> check to the quality evaluator (05 D10).
    /// </summary>
    OutputAbsent,
}

/// <summary>
/// One mapped render-node failure (09 README BD6): what a caller needs in order to decide between
/// rescheduling, retrying, failing the job and alerting, without reading an HTTP status or the adapter's
/// envelope itself.
/// </summary>
/// <remarks>
/// <para>
/// The mapper never copies an adapter message: <see cref="Detail"/> carries the <em>adapter code</em> of a
/// code the mapping does not know (it is the only field that can hold a code), and nothing here can hold raw
/// MPT text, so a fixed sentence is the only text that reaches <c>render_jobs.error_message</c>
/// (05 D4 S4, D8/BD16).
/// </para>
/// <para>
/// This failure travels inside an <see cref="ErrorOr.Error"/>: <see cref="RenderClientErrors.ToError"/> puts
/// it in the error's metadata and <see cref="RenderClientErrors.TryGetFailure"/> reads it back. That round
/// trip is also how a caller detects <see cref="RenderClientFailureKind.OutputAbsent"/> — a download of a
/// kind the task has none of fails the call with <see cref="Kind"/> =
/// <see cref="RenderClientFailureKind.OutputAbsent"/>, and the caller writes no asset instead of failing the
/// job (05 D10).
/// </para>
/// </remarks>
/// <param name="Kind">Which of the five shapes this is; the caller's first switch.</param>
/// <param name="OutcomeCode">The 05 D5 outcome code for <c>render_jobs.error_code</c>, or <c>null</c> when the call is retried instead of recorded.</param>
/// <param name="Detail">The adapter's own error code, for a code the mapping does not know; <c>null</c> otherwise. Never raw adapter text.</param>
/// <param name="Alert">Whether an operator must be told (key rotation, contract drift); <c>false</c> for an expected fault.</param>
/// <param name="RetryAfter">How long to wait before the work is offered again, for the deferred kinds; <c>null</c> otherwise.</param>
public sealed record RenderClientFailure(
    RenderClientFailureKind Kind,
    string? OutcomeCode,
    string? Detail,
    bool Alert,
    TimeSpan? RetryAfter);
