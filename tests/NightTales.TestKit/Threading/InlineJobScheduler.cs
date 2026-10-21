using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions.Jobs;

namespace NightTales.TestKit.Threading;

/// <summary>
/// The <see cref="IJobScheduler"/> of <see cref="WorkerHost"/>'s default (inline) mode (09 README
/// D13): it records every scheduling call and runs enqueued and scheduled jobs synchronously when the
/// test asks for it, so a test never waits for a Hangfire server.
/// </summary>
/// <remarks>
/// A job is resolved from a fresh DI scope and invoked exactly as the Worker would invoke it, with
/// <see cref="CancellationToken.None"/> in place of the server's shutdown token. Recurring calls are
/// recorded only: a recurring job is not executed inline.
/// </remarks>
/// <param name="services">Creates the per-job scope the job class is resolved from.</param>
public sealed class InlineJobScheduler(IServiceScopeFactory services) : IJobScheduler
{
    private readonly List<RecordedEnqueue> _enqueued = [];
    private readonly List<RecordedRecurringCall> _recurringUpserts = [];
    private readonly List<string> _recurringRemovals = [];
    private readonly List<Func<IServiceProvider, Task>> _pending = [];
    private readonly List<Exception> _failures = [];

    /// <summary>Every <see cref="IJobScheduler.Enqueue{TJob}"/> and <see cref="IJobScheduler.Schedule{TJob}"/> call, in order.</summary>
    public IReadOnlyList<RecordedEnqueue> Enqueued => _enqueued;

    /// <summary>Every <see cref="IJobScheduler.AddOrUpdateRecurring{TJob}"/> call, in order.</summary>
    public IReadOnlyList<RecordedRecurringCall> RecurringUpserts => _recurringUpserts;

    /// <summary>Every <see cref="IJobScheduler.RemoveRecurringIfExists"/> call, in order.</summary>
    public IReadOnlyList<string> RecurringRemovals => _recurringRemovals;

    /// <summary>The exceptions the pending jobs threw during the last <see cref="RunPendingAsync"/>.</summary>
    public IReadOnlyList<Exception> Failures => _failures;

    /// <inheritdoc />
    public string Enqueue<TJob>(Expression<Func<TJob, CancellationToken, Task>> methodCall)
        where TJob : class, IBackgroundJob
    {
        ArgumentNullException.ThrowIfNull(methodCall);

        var id = NewJobId();
        _enqueued.Add(new RecordedEnqueue(typeof(TJob).Name, QueueOf<TJob>(), id, Delay: null));
        _pending.Add(provider => RunAsync(provider, methodCall));

        return id;
    }

    /// <inheritdoc />
    public string Schedule<TJob>(Expression<Func<TJob, CancellationToken, Task>> methodCall, TimeSpan delay)
        where TJob : class, IBackgroundJob
    {
        ArgumentNullException.ThrowIfNull(methodCall);

        var id = NewJobId();
        _enqueued.Add(new RecordedEnqueue(typeof(TJob).Name, QueueOf<TJob>(), id, delay));
        _pending.Add(provider => RunAsync(provider, methodCall));

        return id;
    }

    /// <inheritdoc />
    public void AddOrUpdateRecurring<TJob>(
        string id,
        string cron,
        Expression<Func<TJob, CancellationToken, Task>> methodCall)
        where TJob : class, IBackgroundJob
    {
        ArgumentNullException.ThrowIfNull(methodCall);

        _recurringUpserts.Add(new RecordedRecurringCall(id, typeof(TJob).Name, QueueOf<TJob>(), cron));
    }

    /// <inheritdoc />
    public void RemoveRecurringIfExists(string id) => _recurringRemovals.Add(id);

    /// <summary>
    /// Runs every pending job once, in scheduling order, each in its own DI scope — the way Hangfire
    /// activates a job. Returns how many ran; a job that threw is recorded in <see cref="Failures"/>
    /// and does not stop the others.
    /// </summary>
    public async Task<int> RunPendingAsync(CancellationToken cancellationToken = default)
    {
        var pending = _pending.ToArray();
        _pending.Clear();
        _failures.Clear();

        var executed = 0;
        foreach (var job in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var scope = services.CreateScope();
            try
            {
                await job(scope.ServiceProvider);
                executed++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _failures.Add(exception);
            }
        }

        return executed;
    }

    private static Task RunAsync<TJob>(
        IServiceProvider provider,
        Expression<Func<TJob, CancellationToken, Task>> methodCall)
        where TJob : class, IBackgroundJob
    {
        var job = provider.GetRequiredService<TJob>();

        return methodCall.Compile()(job, CancellationToken.None);
    }

    private static string QueueOf<TJob>()
        where TJob : class, IBackgroundJob =>
        JobQueues.Name(typeof(TJob).GetCustomAttribute<JobPolicyAttribute>()?.Queue ?? JobQueue.Default);

    private static string NewJobId() => Guid.CreateVersion7().ToString("N");
}

/// <summary>One recorded enqueue or schedule call (09 README D12).</summary>
/// <param name="JobType">Simple name of the scheduled job class.</param>
/// <param name="Queue">The queue the job's <see cref="JobPolicyAttribute"/> selected.</param>
/// <param name="JobId">The id the scheduler returned.</param>
/// <param name="Delay">The delay of a <c>Schedule</c> call; <c>null</c> for <c>Enqueue</c>.</param>
public sealed record RecordedEnqueue(string JobType, string Queue, string JobId, TimeSpan? Delay);

/// <summary>One recorded recurring upsert (09 README D12).</summary>
/// <param name="RecurringId">The recurring job id.</param>
/// <param name="JobType">Simple name of the job class.</param>
/// <param name="Queue">The queue the job's <see cref="JobPolicyAttribute"/> selected.</param>
/// <param name="Cron">The cron expression, which is UTC.</param>
public sealed record RecordedRecurringCall(string RecurringId, string JobType, string Queue, string Cron);
