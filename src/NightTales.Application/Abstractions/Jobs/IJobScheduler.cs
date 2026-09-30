using System.Linq.Expressions;

namespace NightTales.Application.Abstractions.Jobs;

/// <summary>
/// The only way application code schedules background work (09 README D12/H4). The Worker resolves
/// the Hangfire adapter; the Api registers one that throws, because it never enqueues directly —
/// side effects go through the outbox, whose handlers run in the Worker.
/// </summary>
/// <remarks>
/// The <c>CancellationToken</c> parameter of the expression is a placeholder: it is stored as
/// <c>CancellationToken.None</c> and replaced by Hangfire with the server's shutdown token when the
/// job is performed (H3).
/// </remarks>
public interface IJobScheduler
{
    /// <summary>Enqueues <paramref name="methodCall"/> on the queue of the job's <see cref="JobPolicyAttribute"/>.</summary>
    /// <returns>The Hangfire job id.</returns>
    string Enqueue<TJob>(Expression<Func<TJob, CancellationToken, Task>> methodCall)
        where TJob : class, IBackgroundJob;

    /// <summary>Enqueues <paramref name="methodCall"/> to run after <paramref name="delay"/>.</summary>
    /// <returns>The Hangfire job id.</returns>
    string Schedule<TJob>(Expression<Func<TJob, CancellationToken, Task>> methodCall, TimeSpan delay)
        where TJob : class, IBackgroundJob;

    /// <summary>
    /// Upserts the recurring job <paramref name="id"/> with the UTC cron expression
    /// <paramref name="cron"/>; the same <paramref name="id"/> in every Worker start keeps it stable.
    /// </summary>
    void AddOrUpdateRecurring<TJob>(string id, string cron, Expression<Func<TJob, CancellationToken, Task>> methodCall)
        where TJob : class, IBackgroundJob;

    /// <summary>Removes the recurring job <paramref name="id"/> if it exists; a missing id is not an error.</summary>
    void RemoveRecurringIfExists(string id);
}
