using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Features.Platform.Jobs;

namespace NightTales.Worker.Jobs;

/// <summary>
/// Registers <see cref="OutboxCleanupJob"/> as the platform's daily outbox cleanup (09.2.4 plan §7):
/// 04:30 UTC, id <see cref="RecurringId"/>, through the Worker's recurring-job reconciliation
/// (09 README D12).
/// </summary>
public sealed class OutboxCleanupJobRegistration : IRecurringJobRegistration
{
    /// <summary>The stable recurring job id across Worker starts.</summary>
    public const string RecurringId = "outbox-cleanup";

    /// <summary>Daily at 04:30 UTC — after the idempotency sweep of 04:00.</summary>
    public const string Cron = "30 4 * * *";

    /// <inheritdoc />
    public string Id => RecurringId;

    /// <inheritdoc />
    public void Register(IJobScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);

        scheduler.AddOrUpdateRecurring<OutboxCleanupJob>(
            RecurringId,
            Cron,
            (job, cancellationToken) => job.RunAsync(cancellationToken));
    }
}
