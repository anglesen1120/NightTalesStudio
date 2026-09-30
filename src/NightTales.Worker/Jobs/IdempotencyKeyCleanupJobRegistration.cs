using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Features.Platform.Jobs;

namespace NightTales.Worker.Jobs;

/// <summary>
/// Registers <see cref="IdempotencyKeyCleanupJob"/> as the platform's daily idempotency cleanup
/// (09.1.3 plan §7): 04:00 UTC, id <see cref="RecurringId"/>, through the Worker's recurring-job
/// reconciliation (09 README D12).
/// </summary>
public sealed class IdempotencyKeyCleanupJobRegistration : IRecurringJobRegistration
{
    /// <summary>The stable recurring job id across Worker starts.</summary>
    public const string RecurringId = "idempotency-key-cleanup";

    /// <summary>Daily at 04:00 UTC.</summary>
    public const string Cron = "0 4 * * *";

    /// <inheritdoc />
    public string Id => RecurringId;

    /// <inheritdoc />
    public void Register(IJobScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);

        scheduler.AddOrUpdateRecurring<IdempotencyKeyCleanupJob>(
            RecurringId,
            Cron,
            (job, cancellationToken) => job.RunAsync(cancellationToken));
    }
}
