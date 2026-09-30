using System.Collections.Concurrent;
using Hangfire;
using Hangfire.Common;

namespace NightTales.Infrastructure.Jobs;

/// <summary>
/// Translates a job's <see cref="JobPolicyAttribute"/> retries into Hangfire's
/// <see cref="AutomaticRetryAttribute"/> (09 README D12). It is the only retry filter the platform
/// installs: <see cref="JobFilterPipeline.RemoveGlobalAutomaticRetryDefault"/> removes Hangfire's
/// global 10-attempt default, so a job without the attribute is attempted exactly once.
/// </summary>
/// <remarks>
/// Confirmed against Hangfire.Core 1.8.25 (source of the package): the filter API is
/// <c>Hangfire.Common.IJobFilterProvider.GetFilters(Job job)</c>, providers are chained through
/// <c>Hangfire.Common.JobFilterProviderCollection</c>, and the global defaults live in
/// <c>Hangfire.GlobalJobFilters.Filters</c> — a <c>JobFilterCollection</c> seeded with
/// <c>CaptureCultureAttribute</c>, <c>AutomaticRetryAttribute</c> (10 attempts),
/// <c>StatisticsHistoryAttribute</c> and <c>ContinuationsSupportAttribute</c>. <see cref="Job.Type"/>
/// is a <see cref="Type"/>, so the policy is read straight off it. Filters are evaluated in
/// <c>Order</c> order and the first state-election filter that moves the candidate state wins, which
/// is why the 10-attempt default is removed rather than shadowed.
/// </remarks>
public sealed class JobPolicyFilterProvider : IJobFilterProvider
{
    private static readonly ConcurrentDictionary<Type, JobFilter[]> Cache = new();

    /// <inheritdoc />
    public IEnumerable<JobFilter> GetFilters(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return Cache.GetOrAdd(job.Type, CreateFilters);
    }

    private static JobFilter[] CreateFilters(Type jobType)
    {
        var retry = new AutomaticRetryAttribute
        {
            Attempts = JobPolicy.RetriesOf(jobType),
            OnAttemptsExceeded = AttemptsExceededAction.Fail,
        };

        // Scope Type: the policy belongs to the class, not to one of its methods.
        return [new JobFilter(retry, JobFilterScope.Type, order: retry.Order)];
    }
}
