using NightTales.Application.Abstractions.Jobs;

namespace NightTales.Infrastructure.Jobs;

/// <summary>
/// The Hangfire configuration of the platform, bound from the <c>Jobs</c> section (09 README D12,
/// 09.2.6 plan §7, 09 W3 CR-42).
/// </summary>
public sealed class JobsOptions
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string SectionName = "Jobs";

    /// <summary>The Hangfire queue poll interval when <see cref="QueuePollSeconds"/> is not set.</summary>
    public const int DefaultQueuePollSeconds = 5;

    /// <summary>The scheduler poll interval when <see cref="SchedulePollSeconds"/> is not set.</summary>
    public const int DefaultSchedulePollSeconds = 5;

    /// <summary>The PostgreSQL schema Hangfire owns when <see cref="SchemaName"/> is not set.</summary>
    public const string DefaultSchemaName = "hangfire";

    private static readonly Dictionary<JobQueue, int> DefaultWorkerCounts =
        new()
        {
            [JobQueue.Default] = 4,
            [JobQueue.Render] = 2,
            [JobQueue.Publish] = 1,
            [JobQueue.Email] = 1,
        };

    /// <summary>
    /// Worker counts per queue, keyed by the queue name (<c>Jobs:Workers:&lt;queue&gt;</c>); a queue
    /// that is absent uses its default (4 / 2 / 1 / 1).
    /// </summary>
    public Dictionary<string, int> Workers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hangfire's queue poll interval in seconds (<c>Jobs:QueuePollSeconds</c>).</summary>
    public int QueuePollSeconds { get; set; } = DefaultQueuePollSeconds;

    /// <summary>The scheduler's poll interval in seconds, which delays use (<c>Jobs:SchedulePollSeconds</c>).</summary>
    public int SchedulePollSeconds { get; set; } = DefaultSchedulePollSeconds;

    /// <summary>The PostgreSQL schema Hangfire installs and reads (<c>Jobs:SchemaName</c>).</summary>
    public string SchemaName { get; set; } = DefaultSchemaName;

    /// <summary>The Hangfire worker count of <paramref name="queue"/>.</summary>
    public int WorkerCount(JobQueue queue) =>
        Workers.TryGetValue(JobQueues.Name(queue), out var count) ? count : DefaultWorkerCounts[queue];

    /// <summary>The worker count of the single server, i.e. the sum over every queue.</summary>
    public int TotalWorkerCount => Enum.GetValues<JobQueue>().Sum(WorkerCount);

    /// <summary>The queue names of <see cref="Workers"/> that are not a <see cref="JobQueue"/> member.</summary>
    internal IReadOnlyList<string> UnknownQueueNames() =>
        [.. Workers.Keys.Where(key => !JobQueues.All.Contains(key, StringComparer.OrdinalIgnoreCase))];
}
