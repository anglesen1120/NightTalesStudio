using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Features.Publishing.Jobs;
using NightTales.Application.Publishing;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Features.Publishing.Events;

/// <summary>
/// Delivers <c>publication_updated.v1</c> by scheduling the upload of a publication that just reached
/// <see cref="PublicationStatus.Scheduled"/> (06 README YR3, 06.2.3 plan §4 UC4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an outbox subscriber and not a synchronous seam.</b> The Api registers an <c>IJobScheduler</c> that throws
/// by design (09 README D12): the owner's consent, the autopilot consent and the incident clear all only commit the
/// row and let the interceptor write the event, so the Worker is the one host that may enqueue a job. Every
/// transition to <c>scheduled</c> — a fresh consent (<see cref="Publication.Consent"/>,
/// <see cref="Publication.ConsentFromAuthorization"/>), the quota reschedule (<see cref="Publication.RescheduleUpload"/>),
/// the incident hold (<see cref="Publication.HoldForIncident"/>) and its clear (<see cref="Publication.ReleaseHold"/>)
/// — raises the same event, so this one member covers them all (D13's payload is unchanged: no <c>queueAt</c> field).
/// </para>
/// <para>
/// <b>What decides the delay.</b> The event carries ids and the status only, so the row is re-read here — as
/// <c>AsNoTracking</c>, because this handler changes nothing — and its own <c>next_attempt_at</c> wins: a quota
/// reschedule, an incident hold and an incident clear already computed the instant the next run is due. Only a fresh
/// consent, whose <c>next_attempt_at</c> is <c>null</c>, falls back to
/// <see cref="PublicationQueueTime.For"/> (06 README D12, declared by 06.2.2-T1) — never a second copy of that rule
/// (STATUS S69-7). A held row therefore schedules at its deferred instant and not "now": with YR24's non-null
/// deferred <c>next_attempt_at</c> the hold is a bounded lazy poll, never a hold → event → enqueue → hold loop.
/// </para>
/// <para>
/// <b>Re-checked state, not the event's snapshot.</b> The row may have moved (or gone) between the commit that raised
/// the event and this delivery, so a row that is no longer <c>scheduled</c>, or one that already carries
/// <c>external_post_id</c>, schedules nothing; the job's own reconcile owns that case. Delivery is at least once
/// (D11), so a redelivered message schedules a second job, and UC1's row lock plus status guard and the
/// <c>external_post_id</c> reconcile make that second run a no-op — the pair is what makes the scheduling idempotent
/// in effect without an explicit job id.
/// </para>
/// <para>
/// <b>Tenant.</b> The dispatcher runs every handler in a scope already pinned to the event's user
/// (<c>ISystemContext.For(user_id)</c>), so the filter answers the re-read without an owner argument: this handler
/// takes ids only and can never read another owner's publication.
/// </para>
/// </remarks>
/// <param name="db">The read model the row is re-read from; never written here.</param>
/// <param name="jobs">The Worker's job port: the only way this folder queues <see cref="PublishYouTubeJob"/>.</param>
/// <param name="options">The <c>YouTube</c> section, for the publish lead time of D12's fallback.</param>
/// <param name="clock">The scope's clock; one instant is read for the whole delivery.</param>
public sealed class PublicationScheduledForUploadHandler(
    IAppDbContext db,
    IJobScheduler jobs,
    YouTubeOptions options,
    IClock clock) : IOutboxHandler<PublicationUpdated>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(
        PublicationUpdated domainEvent,
        OutboxContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        // Every other status has a consumer of its own (the realtime forwarder, 02.8.3-T4's pause reasons); this
        // subscriber is only about "the row is due for upload".
        if (domainEvent.Status != PublicationStatus.Scheduled)
        {
            return;
        }

        var row = await db.Publications
            .AsNoTracking()
            .SingleOrDefaultAsync(publication => publication.Id == domainEvent.PublicationId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            // Gone by the time this ran: there is nothing to schedule.
            return;
        }

        if (row.Status != PublicationStatus.Scheduled)
        {
            // Re-check the current state, never the event's snapshot: a cancel or a run that already started won.
            return;
        }

        if (row.ExternalPostId is not null)
        {
            // A video already exists; the job's own reconcile adopts it, so a second upload must not be queued.
            return;
        }

        var now = clock.UtcNow;

        // Prefer the row's own instant (quota reschedule, incident hold, incident clear); the freshly consented row
        // has none and takes D12's formula — the one Domain function, never a second boundary rule (STATUS S69-7).
        var queueAt = row.NextAttemptAt
            ?? PublicationQueueTime.For(row.PublishAt, options.PublishLeadMinutes, now);

        var delay = queueAt - now;

        if (delay <= TimeSpan.Zero)
        {
            jobs.Enqueue<PublishYouTubeJob>((job, jobCancellation) =>
                job.RunAsync(domainEvent.PublicationId, jobCancellation));
        }
        else
        {
            jobs.Schedule<PublishYouTubeJob>(
                (job, jobCancellation) => job.RunAsync(domainEvent.PublicationId, jobCancellation),
                delay);
        }
    }
}
