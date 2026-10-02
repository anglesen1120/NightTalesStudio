using System.Net;
using Microsoft.Extensions.Logging;
using NightTales.RenderNode.Server;

namespace NightTales.MptAdapter;

/// <summary>
/// The adapter's log messages as <c>[LoggerMessage]</c> delegates (CA1848): one place for every event id, and
/// every argument is a value the generator formats lazily. The level and the id are part of the node's journal
/// contract, so the ids are never reused for another meaning.
/// </summary>
/// <remarks>
/// S7: a message carries ids, codes, counts and paths of the node's own state dirs — never a script, a term, a
/// prompt or a request body. MPT's own <c>error</c> text is the one exception, and it is logged only here
/// (S4: "the raw error text stays in the journal").
/// </remarks>
internal static partial class AdapterLog
{
    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "task store schema ready at version {Version}")]
    internal static partial void TaskStoreReady(this ILogger logger, string version);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Error,
        Message = "{Variable} is not set: every MPT call will be rejected (the value is never logged).")]
    internal static partial void MptApiKeyMissing(this ILogger logger, string variable);

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Information,
        Message = "cancel noted for {TaskId}; MPT has no cancel route, the poller finalises it on the terminal state")]
    internal static partial void CancelNoted(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Debug, Message = "output of {TaskId} ({Kind}) is not published yet")]
    internal static partial void OutputNotPublished(this ILogger logger, Guid taskId, OutputKind kind);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Warning, Message = "MPT did not answer {Method} {Path} within {Seconds}s")]
    internal static partial void MptTimeout(this ILogger logger, string method, string? path, double seconds);

    [LoggerMessage(EventId = 3006, Level = LogLevel.Warning, Message = "MPT is unreachable for {Method} {Path}: {Reason}")]
    internal static partial void MptUnreachable(this ILogger logger, string method, string? path, HttpRequestError reason);

    [LoggerMessage(EventId = 3007, Level = LogLevel.Warning, Message = "MPT answered an unreadable body: {Reason}")]
    internal static partial void MptUnreadableBody(this ILogger logger, string reason);

    [LoggerMessage(
        EventId = 3008,
        Level = LogLevel.Warning,
        Message = "could not read {Property} of {Unit}: the MPT restart rule stays disabled and only the 404 rule applies")]
    internal static partial void ProcessStartUnavailable(this ILogger logger, string property, string unit);

    [LoggerMessage(EventId = 3009, Level = LogLevel.Debug, Message = "systemctl probe failed: {Reason}")]
    internal static partial void SystemctlProbeFailed(this ILogger logger, string reason);

    [LoggerMessage(EventId = 3010, Level = LogLevel.Information, Message = "MPT poller every {Seconds}s per non-terminal task")]
    internal static partial void PollerStarted(this ILogger logger, double seconds);

    [LoggerMessage(EventId = 3011, Level = LogLevel.Error, Message = "MPT poll cycle failed")]
    internal static partial void PollCycleFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3012, Level = LogLevel.Error, Message = "polling {TaskId} failed")]
    internal static partial void PollTaskFailed(this ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(
        EventId = 3013,
        Level = LogLevel.Information,
        Message = "task {TaskId} completed in MPT and is handed to the post-processor (stage checking)")]
    internal static partial void CompletedHandedOver(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 3014, Level = LogLevel.Error, Message = "MPT reported {TaskId} failed with: {MptError}")]
    internal static partial void MptReportedFailure(this ILogger logger, Guid taskId, string mptError);

    [LoggerMessage(EventId = 3015, Level = LogLevel.Information, Message = "task {TaskId} failed: {Code}/{Detail}")]
    internal static partial void TaskFailed(this ILogger logger, Guid taskId, string? code, string? detail);

    [LoggerMessage(EventId = 3016, Level = LogLevel.Warning, Message = "task {TaskId} is lost: {Reason}")]
    internal static partial void TaskLost(this ILogger logger, Guid taskId, string reason);

    [LoggerMessage(EventId = 3017, Level = LogLevel.Information, Message = "task {TaskId} is cancelled and its task dir was removed")]
    internal static partial void TaskCancelledCleaned(this ILogger logger, Guid taskId);

    [LoggerMessage(
        EventId = 3018,
        Level = LogLevel.Error,
        Message = "MPT rejected the node's key; no task state changes until it is fixed (D7 reports mpt_unauthorized)")]
    internal static partial void MptUnauthorizedOnce(this ILogger logger);

    [LoggerMessage(EventId = 3019, Level = LogLevel.Warning, Message = "no MPT answer for {TaskId} ({Code}, attempt {Attempts})")]
    internal static partial void PollNoAnswer(this ILogger logger, Guid taskId, string code, int attempts);

    [LoggerMessage(
        EventId = 3020,
        Level = LogLevel.Error,
        Message = "ops alert: {TaskId} has had no MPT answer for {Attempts} polls ({Minutes} min at {Seconds}s)")]
    internal static partial void PollOpsAlert(this ILogger logger, Guid taskId, int attempts, int minutes, int seconds);

    [LoggerMessage(EventId = 3021, Level = LogLevel.Information, Message = "purged {Count} cleaned task rows")]
    internal static partial void PurgedCleanedRows(this ILogger logger, int count);

    [LoggerMessage(EventId = 3022, Level = LogLevel.Information, Message = "removed {Path}")]
    internal static partial void PathRemoved(this ILogger logger, string path);

    [LoggerMessage(EventId = 3023, Level = LogLevel.Warning, Message = "could not remove {Path}: {Reason}")]
    internal static partial void PathRemoveFailed(this ILogger logger, string path, string reason);

    [LoggerMessage(EventId = 3024, Level = LogLevel.Warning, Message = "could not scan {Path}: {Reason}")]
    internal static partial void DirectoryScanFailed(this ILogger logger, string path, string reason);

    [LoggerMessage(
        EventId = 3025,
        Level = LogLevel.Warning,
        Message = "MPT delete for {TaskId} failed ({Code}); the Worker is answered adapter_node_unhealthy")]
    internal static partial void DeleteBackendFailed(this ILogger logger, Guid taskId, string code);

    [LoggerMessage(
        EventId = 3026,
        Level = LogLevel.Warning,
        Message = "MPT {Method} {Path} answered a body over the {Limit}-byte cap; it was dropped unread")]
    internal static partial void MptResponseTooLarge(this ILogger logger, string method, string? path, int limit);

    [LoggerMessage(
        EventId = 3027,
        Level = LogLevel.Information,
        Message = "check for {TaskId} exited {ExitCode} after {Seconds}s; report {ReportPath}")]
    internal static partial void CheckFinished(this ILogger logger, Guid taskId, int exitCode, double seconds, string reportPath);

    [LoggerMessage(
        EventId = 3028,
        Level = LogLevel.Error,
        Message = "ops alert: the check environment of {TaskId} is broken (render_check.py exit 2: input unreadable or tool environment); see the node journal")]
    internal static partial void CheckOpsAlert(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 3029, Level = LogLevel.Information, Message = "task {TaskId} succeeded; output published from the check")]
    internal static partial void TaskSucceeded(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 3030, Level = LogLevel.Error, Message = "post-processing {TaskId} failed unexpectedly")]
    internal static partial void PostProcessFailed(this ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 3031, Level = LogLevel.Information, Message = "check output of {TaskId}: stdout {Stdout}; stderr {Stderr}")]
    internal static partial void CheckStreams(this ILogger logger, Guid taskId, string stdout, string stderr);

    [LoggerMessage(EventId = 3032, Level = LogLevel.Information, Message = "task {TaskId} failed rows kept; foreign cleanup runs after {Hours} h")]
    internal static partial void FailedTaskKept(this ILogger logger, Guid taskId, int hours);

    [LoggerMessage(EventId = 3033, Level = LogLevel.Warning, Message = "output {Kind} of {TaskId} is not servable: {Reason}")]
    internal static partial void OutputUnavailable(this ILogger logger, Guid taskId, OutputKind kind, string reason);
}
