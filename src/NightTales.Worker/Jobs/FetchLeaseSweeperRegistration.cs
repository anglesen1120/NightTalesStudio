using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Features.Research.Jobs;

namespace NightTales.Worker.Jobs;

/// <summary>
/// Registers <see cref="FetchLeaseSweeperJob"/> (02.5.2-T2, 02 D10): every minute, id <see cref="RecurringId"/>, through the
/// Worker's recurring-job reconciliation (09 README D12). The cron is fixed; <c>Fetcher:SweepIntervalSeconds</c> only
/// documents the contract. It is not switchable: a lease nobody sweeps blocks the platform cap and the per-domain slot.
/// </summary>
public sealed class FetchLeaseSweeperRegistration : IRecurringJobRegistration
{
    /// <summary>The recurring job id.</summary>
    public const string RecurringId = "fetch-lease-sweeper";

    /// <summary>Every minute (Hangfire's <c>Cron.Minutely()</c>).</summary>
    public const string Cron = "* * * * *";

    /// <inheritdoc />
    public string Id => RecurringId;

    /// <inheritdoc />
    public void Register(IJobScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);

        scheduler.AddOrUpdateRecurring<FetchLeaseSweeperJob>(
            RecurringId,
            Cron,
            (job, cancellationToken) => job.RunAsync(cancellationToken));
    }
}
