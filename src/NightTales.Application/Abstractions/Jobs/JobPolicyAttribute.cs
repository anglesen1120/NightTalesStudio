namespace NightTales.Application.Abstractions.Jobs;

/// <summary>
/// The queue and retry policy of one background job class (09 README D12). The Infrastructure
/// adapter reads it once per enqueue (the queue) and once per processing attempt (the retries); a
/// job class without it runs on <see cref="JobQueue.Default"/> with <b>0</b> retries.
/// </summary>
/// <remarks>
/// Only <see cref="IBackgroundJob"/> classes are annotated: the attribute is Application metadata,
/// so a job class still carries no Hangfire type.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class JobPolicyAttribute : Attribute
{
    /// <summary>The queue the job runs on; <see cref="JobQueue.Default"/> when not set.</summary>
    public JobQueue Queue { get; init; } = JobQueue.Default;

    /// <summary>Retry attempts after the first failure; <c>0</c> (no retry) when not set.</summary>
    public int Retries { get; init; }
}
