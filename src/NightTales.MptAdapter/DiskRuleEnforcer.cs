using Microsoft.Extensions.Logging;

namespace NightTales.MptAdapter;

/// <summary>
/// 05 D15 P4 as three rules over <see cref="TaskDirCleaner"/>: a cancelled task is cleaned at once, a deleted
/// one after the Worker ingested it (the DELETE), a succeeded one is never cleaned on its own, and a failed one
/// is kept <see cref="FailedRetention"/> for diagnosis and then cleaned. Every removal is idempotent, so a
/// repeated DELETE or a cleanup after the 11.3.4-T1 backstop already ran is a no-op.
/// </summary>
internal sealed class DiskRuleEnforcer
{
    /// <summary>05 D15 P4: how long a failed task's files are kept.</summary>
    internal static readonly TimeSpan FailedRetention = TimeSpan.FromHours(24);

    private readonly TaskDirCleaner _cleaner;
    private readonly ILogger<DiskRuleEnforcer> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public DiskRuleEnforcer(TaskDirCleaner cleaner, ILogger<DiskRuleEnforcer> log)
        : this(cleaner, log, static (span, token) => Task.Delay(span, token))
    {
    }

    internal DiskRuleEnforcer(
        TaskDirCleaner cleaner,
        ILogger<DiskRuleEnforcer> log,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        ArgumentNullException.ThrowIfNull(cleaner);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(delay);

        _cleaner = cleaner;
        _log = log;
        _delay = delay;
    }

    /// <summary>Cancel: the task dir (and with it <c>final-1.norm.mp4</c>) and the relay files go now.</summary>
    internal async Task OnCancelledAsync(Guid taskId, string? backendTaskId, CancellationToken cancellationToken)
    {
        await _cleaner.CleanAsync(taskId, backendTaskId, cancellationToken).ConfigureAwait(false);
        _log.TaskCancelledCleaned(taskId);
    }

    /// <summary>Delete after a terminal state: the same removal, repeatable.</summary>
    internal Task OnDeletedAsync(Guid taskId, string? backendTaskId, CancellationToken cancellationToken) =>
        _cleaner.CleanAsync(taskId, backendTaskId, cancellationToken);

    /// <summary>
    /// Failed: files stay for <see cref="FailedRetention"/>, then <see cref="TaskDirCleaner"/> removes them.
    /// The wait is in-process; after a restart the hourly backstop of 11.3.4-T1 owns the same 24 h rule.
    /// </summary>
    internal async Task OnFailedAsync(Guid taskId, string? backendTaskId, CancellationToken cancellationToken)
    {
        _log.FailedTaskKept(taskId, (int)FailedRetention.TotalHours);
        try
        {
            await _delay(FailedRetention, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await _cleaner.CleanAsync(taskId, backendTaskId, cancellationToken).ConfigureAwait(false);
    }
}
