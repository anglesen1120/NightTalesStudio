using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Features.Compliance.Deletion;
using NightTales.Application.Features.Compliance.Deletion.Jobs;

namespace NightTales.Worker.Jobs;

/// <summary>
/// Registers <see cref="DataDeletionJob"/> as the hourly deletion run (13.3.1-T1, 13 README D4): id
/// <see cref="RecurringId"/>, every hour on the hour. <c>DataDeletion:Enabled=false</c> removes the stored recurring
/// job instead of upserting it; pending rows wait and keep accruing (documented, never auto-mitigated).
/// </summary>
/// <param name="options">The <c>DataDeletion</c> section.</param>
public sealed class DataDeletionRegistration(IOptions<DataDeletionOptions> options) : IRecurringJobRegistration
{
    /// <summary>The stable recurring job id across Worker starts.</summary>
    public const string RecurringId = "data_deletion";

    /// <summary>Hourly, on the hour (UTC).</summary>
    public const string Cron = "0 * * * *";

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

        scheduler.AddOrUpdateRecurring<DataDeletionJob>(
            RecurringId,
            Cron,
            (job, cancellationToken) => job.RunAsync(cancellationToken));
    }
}
