using System.Linq.Expressions;
using Hangfire;
using NightTales.Application.Abstractions.Jobs;

namespace NightTales.Infrastructure.Jobs;

/// <summary>
/// The Hangfire adapter of <see cref="IJobScheduler"/> (09 README D12). Every scheduled call is
/// first rewritten from <c>(job, ct) =&gt; job.RunAsync(id, ct)</c> into Hangfire's
/// <c>job =&gt; job.RunAsync(id, CancellationToken.None)</c>: Hangfire stores the placeholder and
/// substitutes the server's shutdown token when the job is performed (H3), and the queue comes from
/// the job's <see cref="JobPolicyAttribute"/>.
/// </summary>
/// <param name="client">Hangfire client used by <see cref="Enqueue{TJob}"/> and <see cref="Schedule{TJob}"/>.</param>
/// <param name="recurring">Hangfire recurring-job manager used by the recurring half of the port.</param>
public sealed class HangfireJobScheduler(IBackgroundJobClient client, IRecurringJobManager recurring) : IJobScheduler
{
    /// <inheritdoc />
    public string Enqueue<TJob>(Expression<Func<TJob, CancellationToken, Task>> methodCall)
        where TJob : class, IBackgroundJob
    {
        ArgumentNullException.ThrowIfNull(methodCall);

        return client.Enqueue<TJob>(JobQueues.Name(JobPolicy.QueueOf(typeof(TJob))), Placeholder(methodCall));
    }

    /// <inheritdoc />
    public string Schedule<TJob>(Expression<Func<TJob, CancellationToken, Task>> methodCall, TimeSpan delay)
        where TJob : class, IBackgroundJob
    {
        ArgumentNullException.ThrowIfNull(methodCall);

        return client.Schedule<TJob>(JobQueues.Name(JobPolicy.QueueOf(typeof(TJob))), Placeholder(methodCall), delay);
    }

    /// <inheritdoc />
    public void AddOrUpdateRecurring<TJob>(
        string id,
        string cron,
        Expression<Func<TJob, CancellationToken, Task>> methodCall)
        where TJob : class, IBackgroundJob
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(cron);
        ArgumentNullException.ThrowIfNull(methodCall);

        recurring.AddOrUpdate<TJob>(
            id,
            JobQueues.Name(JobPolicy.QueueOf(typeof(TJob))),
            Placeholder(methodCall),
            cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    /// <inheritdoc />
    public void RemoveRecurringIfExists(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        recurring.RemoveIfExists(id);
    }

    /// <summary>
    /// Drops the <see cref="CancellationToken"/> parameter of <paramref name="methodCall"/> by replacing it
    /// with <see cref="CancellationToken.None"/> — the value Hangfire serializes for the parameter it
    /// substitutes at performance time.
    /// </summary>
    private static Expression<Func<TJob, Task>> Placeholder<TJob>(Expression<Func<TJob, CancellationToken, Task>> methodCall)
        where TJob : class, IBackgroundJob
    {
        var body = new CancellationTokenPlaceholder(methodCall.Parameters[1]).Visit(methodCall.Body)
            ?? throw new InvalidOperationException($"The job expression for '{typeof(TJob).FullName}' has no body.");

        return Expression.Lambda<Func<TJob, Task>>(body, methodCall.Parameters[0]);
    }

    /// <summary>Replaces the placeholder parameter with <see cref="CancellationToken.None"/>.</summary>
    private sealed class CancellationTokenPlaceholder(ParameterExpression parameter) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == parameter
                ? Expression.Constant(CancellationToken.None, typeof(CancellationToken))
                : base.VisitParameter(node);
    }
}
