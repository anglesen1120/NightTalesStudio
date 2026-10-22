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

    /// <summary>
    /// The queues the main Hangfire server listens on: every queue except <see cref="JobQueue.Publish"/>
    /// (06.2.3-T2 review PERF-1).
    /// </summary>
    /// <remarks>
    /// The Worker runs a second server for <see cref="JobQueue.Publish"/> alone, so this list never
    /// contains the publish queue: a queue name on both servers would leave the uploads unbounded again.
    /// </remarks>
    public static IReadOnlyList<string> MainServerQueues { get; } =
        [.. Enum.GetValues<JobQueue>().Where(queue => queue != JobQueue.Publish).Select(JobQueues.Name)];

    /// <summary>The queues the dedicated publish server listens on (the publish queue alone).</summary>
    public static IReadOnlyList<string> PublishServerQueues { get; } = [JobQueues.Name(JobQueue.Publish)];

    /// <summary>
    /// The main server's worker count, i.e. the sum over <see cref="MainServerQueues"/>: the publish
    /// queue's workers belong to the dedicated publish server and are not part of this pool.
    /// </summary>
    public int MainServerWorkerCount =>
        Enum.GetValues<JobQueue>().Where(queue => queue != JobQueue.Publish).Sum(WorkerCount);

    /// <summary>
    /// The publish server's worker count, i.e. <c>Jobs:Workers:publish</c>: the real bound on concurrent
    /// uploads, because no other queue's job can occupy one of these threads (06.2.3-T2 review PERF-1).
    /// </summary>
    public int PublishServerWorkerCount => WorkerCount(JobQueue.Publish);

    /// <summary>The queue names of <see cref="Workers"/> that are not a <see cref="JobQueue"/> member.</summary>
    internal IReadOnlyList<string> UnknownQueueNames() =>
        [.. Workers.Keys.Where(key => !JobQueues.All.Contains(key, StringComparer.OrdinalIgnoreCase))];
}
