using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Features.Realtime.Jobs;

namespace NightTales.Worker.Jobs;

/// <summary>
/// Registers <see cref="HubTicketCleanupJob"/> as the platform's hourly hub-ticket sweep (09 README
/// BD11, D12): id <see cref="RecurringId"/>, every hour on the hour, through the Worker's recurring-job
/// reconciliation.
/// </summary>
/// <remarks>
/// The Worker is the only host that schedules it (D12/R7) and the Api never does. The job's retention
/// and batch size come from the <c>Realtime</c> section, so the cron says only <em>when</em> the sweep
/// runs.
/// </remarks>
public sealed class HubTicketCleanupJobRegistration : IRecurringJobRegistration
{
    /// <summary>The stable recurring job id across Worker starts.</summary>
    public const string RecurringId = "hub-ticket-cleanup";

    /// <summary>Hourly, on the hour (UTC — the registrar pins the time zone to UTC).</summary>
    public const string Cron = "0 * * * *";

    /// <inheritdoc />
    public string Id => RecurringId;

    /// <inheritdoc />
    public void Register(IJobScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);

        scheduler.AddOrUpdateRecurring<HubTicketCleanupJob>(
            RecurringId,
            Cron,
            (job, cancellationToken) => job.RunAsync(cancellationToken));
    }
}
