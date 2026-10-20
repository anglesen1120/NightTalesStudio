using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Publishing.Dtos;
using NightTales.Application.Publishing;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Features.Publishing.Commands.ConsentPublication;

/// <summary>
/// Records the owner's consent on a publication (06.2.2-T1 task §Design steps 1-5, 06 README D7/D12/D13/D17):
/// loads the row through the tenant filter, answers the idempotent repeat with the same DTO without writing,
/// compares <c>If-Match</c>, records the consent on the aggregate, moves the episode to <c>scheduled</c> in the
/// same unit of work and lets the outbox interceptor raise <c>publication_updated.v1</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order is the contract.</b> The idempotent repeat is answered <em>before</em> the ETag comparison, because a
/// retried request after a lost response still carries the pre-consent tag: comparing first would answer 412 to
/// the very request the first attempt already applied (06 README D7).
/// </para>
/// <para>
/// <b>No scheduler here (YR3).</b> Consenting leaves the row <c>scheduled</c>; the Worker's subscriber to
/// <c>publication_updated.v1</c> is what derives the queue time and enqueues the upload (06.2.3-T2). This handler
/// therefore takes no scheduling port at all, and the Api host's throwing registration of the scheduler can
/// never be reached from this path.
/// </para>
/// <para>
/// <b>What is never logged.</b> Ids only: never the title, the description or the channel display name
/// (06 README D17). The outbox payload is the aggregate's own event, which carries ids, the status and the error
/// code for the same reason.
/// </para>
/// <para>
/// <b>Round trips.</b> Three on the success path: the publication with its connection's display name (one
/// statement), the episode, and the save that writes the row, the episode and the outbox message. Only the
/// lost-update catch adds a fourth, a single existence read that separates a deleted row (404) from a moved one
/// (409). No external call is made.
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the tenant filter is what turns "another owner's row" into a 404.</param>
/// <param name="options">
/// The bound <c>YouTube</c> section, handed over as the scoped snapshot of the host's
/// <c>IOptionsMonitor&lt;YouTubeOptions&gt;.CurrentValue</c> (the composition root registers it; Application may not
/// reference <c>Microsoft.Extensions.Options</c>). The current consent-text version is read once per command (D11).
/// </param>
/// <param name="clock">The scope's clock; one instant is read for the whole command.</param>
/// <param name="logger">The id-only log lines of this path.</param>
public sealed partial class ConsentPublicationHandler(
    IAppDbContext db,
    YouTubeOptions options,
    IClock clock,
    ILogger<ConsentPublicationHandler> logger)
    : ICommandHandler<ConsentPublicationCommand, ErrorOr<PublicationDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<PublicationDto>> Handle(
        ConsentPublicationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var now = clock.UtcNow;

        // D11/WT27: the host hands over the monitor's CurrentValue per scope, so flipping
        // `YouTube:ConsentTextVersion` is live and the version is still read once per command.
        var currentTextVersion = options.ConsentTextVersion;

        // Step 1: the row and the display name of its connection in one statement. The DTO carries the name
        // (06.2.1-T1's mapper takes it), and the tenant filter is what turns another owner's row into the same
        // 404 an unknown id gets (09 README D10). The connection's FK is RESTRICT, so the join never drops a row.
        var row = await db.Publications
            .Where(publication => publication.Id == command.PublicationId)
            .Join(
                db.ChannelConnections,
                publication => publication.ChannelConnectionId,
                connection => connection.Id,
                (publication, connection) => new { Publication = publication, ChannelDisplayName = connection.DisplayName })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return Errors.NotFound();
        }

        var publication = row.Publication;

        // Step 2: the idempotent repeat, answered before the ETag comparison (D7): a lost-response retry carries
        // the pre-consent tag, and the row already holds exactly the consent this request would write. No save,
        // no audit projection and no episode read: the command changed nothing.
        if (publication.IsUserConsentedAs(command.ConsentTextVersion))
        {
            LogAlreadyConsented(publication.Id, publication.EpisodeId);

            return PublicationDtoMapper.ToDto(publication, row.ChannelDisplayName);
        }

        // Step 3: the If-Match comparison, after the load so the 412 carries the current tag and no row was
        // written on a stale value (09 README D15; the route's RequireIfMatch answered 428 for an absent header).
        if (!ETags.Matches(command.IfMatch, ETags.Publication(publication.Version)))
        {
            return PublicationErrors.Conflict();
        }

        // Step 4: the aggregate records the consent and raises publication_updated.v1 (D13). Its two refusals are
        // the 400 of a text version the owner can no longer be shown and the 409 of a row that is no longer
        // awaiting_consent — including an autopilot-consented row, whose consent is never silently merged (D7).
        var consented = publication.Consent(command.ConsentTextVersion, currentTextVersion, now);

        if (consented.IsError)
        {
            return consented.Errors;
        }

        // Step 5: the episode, owner-scoped like the row above, moves in the same unit of work: a refusal here
        // fails the whole command, so no `scheduled` publication ever exists without its episode transition.
        var episode = await db.Episodes
            .SingleOrDefaultAsync(candidate => candidate.Id == publication.EpisodeId, cancellationToken)
            .ConfigureAwait(false);

        if (episode is null)
        {
            // The publication is gone with its episode (the key cascades): the 404 the row read would answer.
            return Errors.NotFound();
        }

        var scheduled = episode.MarkScheduled(publication.Id, now);

        if (scheduled.IsError)
        {
            return scheduled.Errors;
        }

        // ONE transaction: the publication row, the episode row and the outbox message commit together. The flush
        // is the handler's own (the DTO must carry the post-save xmin), so the pipeline's commit mapping never sees
        // a lost update: mapping it here keeps the loser of a concurrent consent/cancel on the documented 409
        // `concurrency_conflict` of 09.2.4-T2 instead of an unhandled 500 (task §Failure handling).
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The xmin token reports "0 rows" for two different events, so the catch alone cannot tell them apart.
            // One cheap tenant-filtered read separates them: a row that vanished between the load and the save
            // (account deletion cascade) is the same 404 the load path answers — never a 500, never a bogus 409 —
            // while a row that is still there but moved is the documented 409 of a lost update.
            var stillExists = await db.Publications
                .AsNoTracking()
                .AnyAsync(candidate => candidate.Id == command.PublicationId, cancellationToken)
                .ConfigureAwait(false);

            if (!stillExists)
            {
                return Errors.NotFound();
            }

            return Error.Conflict(
                ErrorCodes.ConcurrencyConflict,
                "The publication changed while the consent was being recorded; reload it and try again.");
        }

        // The audit row's ids (the pipeline's AuditBehavior writes the row on the success path only).
        command.Outcome = new PublicationConsentedOutcome(
            publication.Id,
            publication.EpisodeId,
            command.ConsentTextVersion);

        LogConsented(publication.Id, publication.EpisodeId);

        return PublicationDtoMapper.ToDto(publication, row.ChannelDisplayName);
    }

    [LoggerMessage(
        EventId = 6428,
        Level = LogLevel.Information,
        Message = "Publication {PublicationId} of episode {EpisodeId} was consented and is scheduled.")]
    private partial void LogConsented(Guid publicationId, Guid episodeId);

    [LoggerMessage(
        EventId = 6429,
        Level = LogLevel.Information,
        Message = "Publication {PublicationId} of episode {EpisodeId} was already consented; the repeat wrote nothing.")]
    private partial void LogAlreadyConsented(Guid publicationId, Guid episodeId);
}
