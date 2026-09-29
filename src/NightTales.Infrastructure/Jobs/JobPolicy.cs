using System.Reflection;
using NightTales.Application.Abstractions.Jobs;

namespace NightTales.Infrastructure.Jobs;

/// <summary>
/// Reads the <see cref="JobPolicyAttribute"/> of a job class: the one place that knows a job without
/// the attribute runs on <see cref="JobQueue.Default"/> with <b>0</b> retries (09 README D12).
/// </summary>
internal static class JobPolicy
{
    /// <summary>The queue of <paramref name="jobType"/>.</summary>
    internal static JobQueue QueueOf(Type jobType) => PolicyOf(jobType)?.Queue ?? JobQueue.Default;

    /// <summary>The retry attempts of <paramref name="jobType"/>; 0 when no attribute is present.</summary>
    internal static int RetriesOf(Type jobType) => PolicyOf(jobType)?.Retries ?? 0;

    private static JobPolicyAttribute? PolicyOf(Type jobType) =>
        jobType.GetCustomAttribute<JobPolicyAttribute>(inherit: false);
}
