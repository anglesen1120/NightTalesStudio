using System.Linq.Expressions;
using NightTales.Application.Abstractions.Jobs;

namespace NightTales.Infrastructure.Jobs;

/// <summary>
/// The <see cref="IJobScheduler"/> the Api registers (09 README D12/H4): the Api never enqueues a
/// job directly — a command handler writes an <c>outbox_messages</c> row and the Worker's outbox
/// dispatcher schedules the job — so an accidental use fails fast instead of silently enqueueing
/// work from the web host.
/// </summary>
public sealed class ThrowingJobScheduler : IJobScheduler
{
    /// <summary>The message every member throws; the failure is a programming error, not a runtime state.</summary>
    public const string Message =
        "The Api never schedules background jobs (09 README D12): command handlers write outbox_messages "
        + "and the Worker's outbox dispatcher enqueues the job.";

    /// <inheritdoc />
    public string Enqueue<TJob>(Expression<Func<TJob, CancellationToken, Task>> methodCall)
        where TJob : class, IBackgroundJob => throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public string Schedule<TJob>(Expression<Func<TJob, CancellationToken, Task>> methodCall, TimeSpan delay)
        where TJob : class, IBackgroundJob => throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public void AddOrUpdateRecurring<TJob>(
        string id,
        string cron,
        Expression<Func<TJob, CancellationToken, Task>> methodCall)
        where TJob : class, IBackgroundJob => throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public void RemoveRecurringIfExists(string id) => throw new InvalidOperationException(Message);
}
