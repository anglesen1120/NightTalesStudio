using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Features.Assets.Jobs;

namespace NightTales.Worker.Jobs;

/// <summary>
/// Registers <see cref="RetentionSweepJob"/> as the platform's daily retention sweep (09.7.3-T1): 03:00 UTC,
/// id <see cref="RecurringId"/>, through the Worker's recurring-job reconciliation (09 README D12).
/// <c>RetentionSweep:Enabled = false</c> removes the stored recurring job instead — the rollback switch,
/// applied on the next Worker start.
/// </summary>
/// <param name="options">The sweep options; only <see cref="RetentionSweepOptions.Enabled"/> is read here.</param>
public sealed class RetentionSweepRegistration(IOptions<RetentionSweepOptions> options) : IRecurringJobRegistration
{
    /// <summary>The stable recurring job id across Worker starts.</summary>
    public const string RecurringId = "retention_sweep";

    /// <summary>Daily at 03:00 UTC.</summary>
    public const string Cron = "0 3 * * *";

    /// <inheritdoc />
    public string Id => RecurringId;

    /// <inheritdoc />
    public void Register(IJobScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);

        if (!options.Value.Enabled)
        {
            scheduler.RemoveRecurringIfExists(RecurringId);

            return;
        }

        scheduler.AddOrUpdateRecurring<RetentionSweepJob>(
            RecurringId,
            Cron,
            (job, cancellationToken) => job.RunAsync(cancellationToken));
    }
}
