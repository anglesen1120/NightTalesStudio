using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Realtime;

namespace NightTales.Application.Features.Realtime.Jobs;

/// <summary>
/// Deletes the <c>hub_tickets</c> rows that outlived the configured retention (09 README BD11, 09.8.1
/// plan §7): the recurring hourly sweep that cleans up after the users whose connections never came
/// back.
/// </summary>
/// <remarks>
/// <para>
/// The batching and the retention are <see cref="IHubTicketStore.DeleteExpiredAsync"/>'s, so this job
/// carries no policy of its own: one run removes at most one configured batch, and the next hour's
/// run continues.
/// </para>
/// <para>
/// Idempotent and safe to re-run: it deletes by <c>expires_at</c>, never by id, and a re-run after a
/// failure finds the remaining rows. Only the Worker schedules it (D12/R7); the Api never does.
/// </para>
/// </remarks>
/// <param name="store">The <c>hub_tickets</c> sweep.</param>
[JobPolicy(Queue = JobQueue.Default, Retries = 2)]
public sealed class HubTicketCleanupJob(IHubTicketStore store) : IBackgroundJob
{
    /// <summary>Removes one batch of rows that expired before the configured retention.</summary>
    /// <param name="cancellationToken">The scheduler's shutdown token.</param>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // The count is the sweep's own evidence; nothing consumes it here (a job writes no log row of
        // its own — Hangfire's history is the Worker's log surface, D12).
        _ = await store.DeleteExpiredAsync(cancellationToken).ConfigureAwait(false);
    }
}
