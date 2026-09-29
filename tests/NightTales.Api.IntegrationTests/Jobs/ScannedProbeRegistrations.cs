using NightTales.Application.Abstractions.Jobs;

namespace NightTales.Api.IntegrationTests.Jobs;

/// <summary>
/// The first recurring registration of the probe assembly (09.2.6-T1): the start-time scan must reach
/// <b>every</b> implementation an assembly declares, so this assembly declares two. Both are top-level
/// classes because the scan skips nested types, which is what keeps a test double such as the
/// registrar's own stub out of it.
/// </summary>
public sealed class FirstScannedProbeRegistration : IRecurringJobRegistration
{
    /// <summary>The recurring id this scan fixture registers.</summary>
    public const string RecurringId = "probe:scanned-first";

    private const string Cron = "0 4 * * *";

    /// <inheritdoc />
    public string Id => RecurringId;

    /// <inheritdoc />
    public void Register(IJobScheduler scheduler) =>
        scheduler.AddOrUpdateRecurring<RecordingProbeJob>(RecurringId, Cron, (job, ct) => job.RunAsync(Guid.Empty, ct));
}

/// <summary>
/// The second recurring registration of the probe assembly; see
/// <see cref="FirstScannedProbeRegistration"/> for why the assembly carries two.
/// </summary>
public sealed class SecondScannedProbeRegistration : IRecurringJobRegistration
{
    /// <summary>The recurring id this scan fixture registers.</summary>
    public const string RecurringId = "probe:scanned-second";

    private const string Cron = "0 4 * * *";

    /// <inheritdoc />
    public string Id => RecurringId;

    /// <inheritdoc />
    public void Register(IJobScheduler scheduler) =>
        scheduler.AddOrUpdateRecurring<RecordingProbeJob>(RecurringId, Cron, (job, ct) => job.RunAsync(Guid.Empty, ct));
}

/// <summary>The ids every host that scans this assembly registers.</summary>
internal static class ScannedProbeRegistrations
{
    private static readonly string[] All =
        [FirstScannedProbeRegistration.RecurringId, SecondScannedProbeRegistration.RecurringId];

    /// <summary>The scanned ids of this assembly, in scan order.</summary>
    internal static IReadOnlyList<string> Ids => All;
}
