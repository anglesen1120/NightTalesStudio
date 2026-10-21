using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Features.Stories.Jobs;

namespace NightTales.Worker.Jobs;

/// <summary>
/// Registers <see cref="StaleGenerationSweeperJob"/> (02.2.2-T3, plan §7.3): every 5 minutes, id
/// <see cref="RecurringId"/>, through the Worker's recurring-job reconciliation (09 README D12). It covers every generation
/// kind; it is not switchable, because a crashed run would otherwise keep its reservation and its episode locked.
/// </summary>
public sealed class StaleGenerationSweeperRegistration : IRecurringJobRegistration
{
    /// <summary>The recurring job id.</summary>
    public const string RecurringId = "stale-generation-sweeper";

    /// <summary>Every 5 minutes.</summary>
    public const string Cron = "*/5 * * * *";

    /// <inheritdoc />
    public string Id => RecurringId;

    /// <inheritdoc />
    public void Register(IJobScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);

        scheduler.AddOrUpdateRecurring<StaleGenerationSweeperJob>(
            RecurringId,
            Cron,
            (job, cancellationToken) => job.RunAsync(cancellationToken));
    }
}
