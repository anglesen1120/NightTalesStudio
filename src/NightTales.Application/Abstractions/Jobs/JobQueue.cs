using System.Diagnostics.CodeAnalysis;

namespace NightTales.Application.Abstractions.Jobs;

/// <summary>The Hangfire queues of the platform (09 README D12, 09.2.6 plan §7).</summary>
/// <remarks>
/// A member name is the queue name in lower case (<c>default</c>, <c>render</c>, <c>publish</c>,
/// <c>email</c>); <see cref="JobQueues"/> is the single place that spells it for Hangfire, so a
/// member added here is served and dashboard-visible without a second list to update.
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "'JobQueue' is the type name the 09 README D12 contract fixes for this enum.")]
public enum JobQueue
{
    Default,
    Render,
    Publish,
    Email,
}

/// <summary>The queue names Hangfire sees, derived from <see cref="JobQueue"/> (09 README D12).</summary>
public static class JobQueues
{
    /// <summary>Every queue, in declaration order — the server's queue list.</summary>
    public static IReadOnlyList<string> All { get; } = [.. Enum.GetValues<JobQueue>().Select(Name)];

    /// <summary>The Hangfire queue name of <paramref name="queue"/> (lower case member name).</summary>
    public static string Name(JobQueue queue) =>
        Enum.GetName(queue)?.ToLowerInvariant()
        ?? throw new ArgumentOutOfRangeException(nameof(queue), queue, "Unknown job queue.");
}
