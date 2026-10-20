using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Publishing.Dtos;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Features.Publishing.Commands.CancelPublication;

/// <summary>
/// Cancels a publication that has not started uploading (06.2.6-T1 §Design steps 1-6, 06 README D7/D10/D15/D17):
/// loads the row under its own lock through the tenant filter, answers the lost-response repeat without writing,
/// compares <c>If-Match</c>, moves <c>awaiting_consent|scheduled</c> to <c>cancelled</c> on the aggregate, gives
/// back any quota unit the row still holds and lets the outbox interceptor raise <c>publication_updated.v1</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order is the contract.</b> The already-cancelled repeat is answered <em>before</em> the ETag comparison,
/// because a retried request after a lost response still carries the pre-cancel tag: comparing first would answer
/// 412 to the very request the first attempt already applied (06 README D7). Nothing is written on that path — no
/// second audit row, no second outbox row, no second release.
/// </para>
/// <para>
/// <b>The lock is what makes cancel and the upload job exclusive.</b> The upload job re-reads this row under the
/// same lock before it reserves a unit and moves the row to <c>uploading</c>, so whichever takes the lock first
/// decides: if the job wins, the row is <c>uploading</c> and this command answers the aggregate's 409
/// <c>publication_invalid_state</c> (the client points the owner at YouTube Studio — a video is never deleted from
/// the owner's channel in MVP); if the cancel wins, the job reads <c>cancelled</c> and refuses in
/// <see cref="Publication.StartUpload"/>. No lost-response window can therefore produce a second video.
/// </para>
/// <para>
/// <b>No external call and no mail.</b> Nothing here reaches Google or queues an e-mail: the upload job is a
/// delayed background job that cannot be un-scheduled, and the row — not the schedule — is the source of truth, so
/// a cancelled publication is simply skipped when that job runs (06.2.6 task §Design). The episode keeps whatever
/// status it had: its return to a pre-publication state belongs to 02's own flow.
/// </para>
/// <para>
/// <b>Round trips.</b> The lock, the locked read (which joins the episode's liveness into the same statement, so a
/// soft-cancelled episode's publication is the 404 an unknown one gets), the row update on commit, and one optional
/// ledger update when a reserved day is present. The audit row is written by the pipeline's <c>AuditBehavior</c>
/// inside that same transaction, so the state, the outbox message and the audit row commit or roll back together.
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the tenant filter is what turns "another owner's row" into a 404.</param>
/// <param name="quotaLedger">The platform quota ledger the row's reserved unit is given back to (D10).</param>
/// <param name="clock">The scope's clock; one instant is read for the whole command.</param>
/// <param name="logger">The log of the cancel; every line carries ids only (D17).</param>
public sealed partial class CancelPublicationHandler(
    IAppDbContext db,
    IQuotaLedger quotaLedger,
    IClock clock,
    ILogger<CancelPublicationHandler> logger)
    : ICommandHandler<CancelPublicationCommand, ErrorOr<Deleted>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<Deleted>> Handle(
        CancelPublicationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Step 1: the row lock first (the render cancel's convention), then the tenant-filtered load. Another
        // user's id, an unknown id and an anonymous scope lock nothing, and the read below answers the same 404
        // for all three (09 README D10). The episode predicate is a join of the same statement: a publication whose
        // episode was soft-cancelled reads as gone, exactly as the episode's own routes answer it.
        await db.LockPublicationForUpdateAsync(command.PublicationId, cancellationToken).ConfigureAwait(false);

        var publication = await db.Publications
            .Where(candidate => candidate.Id == command.PublicationId)
            .Where(candidate => db.Episodes.Any(episode =>
                episode.Id == candidate.EpisodeId && episode.Status != EpisodeStatus.Cancelled))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (publication is null)
        {
            return Errors.NotFound();
        }

        // Step 2: the lost-response repeat, answered before the ETag comparison (D7): the row already holds the
        // state this request would write, and the retry carries the pre-cancel tag. No save, no audit projection,
        // no ledger call and no outbox row: the command changed nothing.
        if (publication.Status == PublicationStatus.Cancelled)
        {
            LogAlreadyCancelled(publication.Id, publication.EpisodeId);

            return Result.Deleted;
        }

        // Step 3: the If-Match comparison, after the load so the 412 carries the current tag and nothing was
        // written on a stale value (09 README D15; the route's RequireIfMatch answered 428 for an absent header).
        if (!ETags.Matches(command.IfMatch, ETags.Publication(publication.Version)))
        {
            return PublicationErrors.Conflict();
        }

        // The status the row leaves, read before the transition overwrites it: it is the only history the row
        // keeps and the one value the audit row and the log line add to the ids (D17).
        var previousStatus = PublicationWire.Status(publication.Status);

        // Step 4: the aggregate owns the state machine. `uploading`, `processing`, `published` and `failed` are
        // refused with the 409 `publication_invalid_state` — the row keeps its status and nothing is written.
        var cancelled = publication.Cancel(clock.UtcNow);

        if (cancelled.IsError)
        {
            return cancelled.Errors;
        }

        // Step 5: the quota unit the row still holds, if any. An absent or unusable key is "nothing to release"
        // (the row never passed the upload job's reserve step); a ledger refusal fails the whole command, so a
        // committed cancel never strands a reservation.
        var released = await PublicationQuotaRelease
            .ReleaseAsync(publication, quotaLedger, logger, cancellationToken)
            .ConfigureAwait(false);

        if (released.IsError)
        {
            return released.Errors;
        }

        // Step 6: the audit row's values (the pipeline's AuditBehavior writes the row on the success path only) and
        // the id-only log line. The save and the commit are the unit of work's, which is where the outbox
        // interceptor stores `publication_updated.v1` with status `cancelled` beside the row (D13).
        command.Outcome = new PublicationCancelledOutcome(publication.Id, publication.EpisodeId, previousStatus);

        LogCancelled(publication.Id, publication.EpisodeId, previousStatus);

        return Result.Deleted;
    }

    [LoggerMessage(
        EventId = 6503,
        Level = LogLevel.Information,
        Message = "Publication {PublicationId} of episode {EpisodeId} was cancelled from {PreviousStatus}; no upload is sent.")]
    private partial void LogCancelled(Guid publicationId, Guid episodeId, string previousStatus);

    [LoggerMessage(
        EventId = 6504,
        Level = LogLevel.Information,
        Message = "Publication {PublicationId} of episode {EpisodeId} was already cancelled; the repeat wrote nothing.")]
    private partial void LogAlreadyCancelled(Guid publicationId, Guid episodeId);
}
