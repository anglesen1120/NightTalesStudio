using System.Collections.Concurrent;
using Hangfire.Common;
using Hangfire.Server;
using NightTales.Application.Abstractions.Jobs;

namespace NightTales.Infrastructure.Jobs;

/// <summary>
/// Publishes the attempt of the job that is about to run to
/// <see cref="JobAttemptScope"/> (09.6.3-T2 step 5, WT52), so a job can tell a retryable failure from
/// its last chance without the Application layer knowing a scheduler (D1/D12).
/// </summary>
/// <remarks>
/// <para>
/// Hangfire counts attempts 0-based in the <c>RetryCount</c> job parameter its retry filter writes;
/// <see cref="IJobAttempt"/> counts from 1, so the attempt is <c>RetryCount + 1</c> and the maximum is
/// the job class's <see cref="JobPolicyAttribute.Retries"/> plus that first run. Both numbers are read
/// exactly as <see cref="JobTelemetryFilter"/> reads the counter it tags its span with, and the retries
/// come from <see cref="JobPolicy"/>, the one place that knows a class without the attribute retries
/// zero times.
/// </para>
/// <para>
/// <b>The value never outlives one attempt.</b> The scope is
/// <c>AsyncLocal</c>-based and a Worker process performs several attempts in parallel on pooled
/// threads, so the handle <see cref="JobAttemptScope.Set"/> returns travels in
/// <see cref="PerformingContext.Items"/> — a dictionary Hangfire keeps for the single
/// <c>PerformContext</c> it hands to both callbacks — and <see cref="OnPerformed"/> disposes it on that
/// same context, restoring whatever the context had before (nothing, for a top-level job). A job body
/// therefore reads its own attempt and a later job on the same thread reads the default "one attempt,
/// and it is the last" instead of the attempt before it.
/// </para>
/// <para>
/// It is applied to every job by <see cref="JobFilterPipeline.Compose"/>, which every server host
/// builds its <see cref="IJobFilterProvider"/> from; a host that runs no server (the Api's dashboard)
/// never performs a job and needs no attempt.
/// </para>
/// </remarks>
public sealed class JobAttemptFilter : IServerFilter
{
    /// <summary>The job parameter Hangfire's retry filter records the attempt counter in (0-based).</summary>
    private const string RetryCountParameter = "RetryCount";

    /// <summary>The <see cref="PerformingContext.Items"/> key carrying the scope handle between the callbacks.</summary>
    private const string HandleItemKey = "nts.job.attempt";

    /// <summary>
    /// The retries each job class declares, read once per class: <see cref="JobPolicy.RetriesOf"/> walks the
    /// type's attributes on every call, and this global filter runs on every execution of every job.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, int> RetriesByJobType = new();

    /// <summary>
    /// The 1-based attempt and the total the policy allows for a job whose retry counter is
    /// <paramref name="retryCount"/> and whose class declares <paramref name="retries"/> retries.
    /// </summary>
    /// <param name="retryCount">The scheduler's 0-based retry counter.</param>
    /// <param name="retries">The retry attempts the job's <see cref="JobPolicyAttribute"/> allows.</param>
    /// <returns>The attempt now running and how many attempts the class is allowed in total.</returns>
    /// <remarks>
    /// The scheduler's counter is authoritative and is not clamped: <see cref="IJobAttempt.IsLastAttempt"/>
    /// compares the two numbers, so an attempt past the declared retries still reads as the last one — a
    /// job with no attempt left must end its own row instead of throwing into a scheduler that will not
    /// run it again.
    /// </remarks>
    public static (int Attempt, int MaxAttempts) Resolve(int retryCount, int retries) =>
        (retryCount + 1, retries + 1);

    /// <inheritdoc />
    public void OnPerforming(PerformingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var retries = RetriesOf(context.BackgroundJob.Job.Type);

        // A class with no retries has exactly one attempt, so the answer is (1, 1) whatever the
        // scheduler would report: skip the storage round trip that reads the RetryCount parameter.
        if (retries == 0)
        {
            context.Items[HandleItemKey] = JobAttemptScope.Set(attempt: 1, maxAttempts: 1);
            return;
        }

        var (attempt, maxAttempts) = Resolve(
            context.GetJobParameter<int>(RetryCountParameter),
            retries);

        context.Items[HandleItemKey] = JobAttemptScope.Set(attempt, maxAttempts);
    }

    /// <summary>The retries of <paramref name="jobType"/>, read once per class and cached.</summary>
    /// <param name="jobType">The declaring type of the job about to run.</param>
    /// <returns>The retry attempts the class declares; 0 for a class without the policy.</returns>
    private static int RetriesOf(Type jobType) =>
        RetriesByJobType.GetOrAdd(jobType, static type => JobPolicy.RetriesOf(type));

    /// <inheritdoc />
    public void OnPerformed(PerformedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(HandleItemKey, out var handle) && handle is IDisposable scope)
        {
            context.Items.Remove(HandleItemKey);
            scope.Dispose();
        }
    }
}

/// <summary>Hands <see cref="JobAttemptFilter"/> to Hangfire's filter pipeline for every job.</summary>
public sealed class JobAttemptFilterProvider : IJobFilterProvider
{
    private static readonly JobFilter[] Filters =
        [new JobFilter(new JobAttemptFilter(), JobFilterScope.Global, order: null)];

    /// <inheritdoc />
    public IEnumerable<JobFilter> GetFilters(Job job) => Filters;
}
