using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.Commands.ApproveFactSheet;

/// <summary>
/// Approves the draft the client read (02.5.4-T1, plan §4.3, decisions 5/6/8/10). The episode's row lock is the
/// first statement, so an approve and an edit of the same episode serialise: the second caller reads the first
/// one's new version and its <c>fs-</c> ETag no longer matches (412).
/// </summary>
/// <remarks>
/// <para>
/// The check order is the contract's: the tenant-filtered loads (404 <c>fact_sheet_not_found</c>, never 403, for
/// a foreign episode, an unknown id and an episode without a sheet), the ETag (428/412), the F26 state gate (409
/// <c>episode_invalid_state</c>), the target's own status — only a <c>draft</c> is approvable, a re-approve is 412
/// <c>fact_sheet_conflict</c> because the tag names the draft — and then <c>F13SourceDiversityCheck</c> (409
/// <c>fact_sheet_sources_insufficient</c> or 409 <c>story_sources_required</c>).
/// </para>
/// <para>
/// One transaction holds both halves of the approval (invariant 4): the episode's previous <c>Approved</c> row is
/// demoted to <c>Superseded</c> and flushed <em>before</em> the draft becomes <c>Approved</c>, because EF may
/// reorder the two statements of one save and <c>ux_fact_sheets_episode_approved</c> is checked per statement.
/// Each flush also maps a refused unique constraint to 412 instead of letting it become a 500 (decision 6).
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="currentUser">The acting user, recorded as the approver.</param>
/// <param name="clock">The command's clock reading.</param>
public sealed class ApproveFactSheetCommandHandler(IAppDbContext db, ICurrentUser currentUser, IClock clock)
    : ICommandHandler<ApproveFactSheetCommand, ErrorOr<FactSheetSummaryDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<FactSheetSummaryDto>> Handle(
        ApproveFactSheetCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Step 1: the row lock first, then the tenant-filtered loads (another user's id, an unknown id and an
        // episode without a sheet all answer the same 404).
        await db.LockEpisodeForUpdateAsync(command.EpisodeId, cancellationToken).ConfigureAwait(false);

        var episode = await db.Episodes
            .SingleOrDefaultAsync(candidate => candidate.Id == command.EpisodeId, cancellationToken)
            .ConfigureAwait(false);
        if (episode is null)
        {
            return SheetNotFound();
        }

        var current = await db.FactSheets
            .Where(sheet => sheet.EpisodeId == command.EpisodeId)
            .OrderByDescending(sheet => sheet.Version)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            return SheetNotFound();
        }

        // Step 2: 428 without the header, 412 for a stale, malformed or foreign tag. Only the current version is
        // addressable (decision 4).
        if (string.IsNullOrWhiteSpace(command.IfMatch))
        {
            return IfMatchRequired();
        }

        if (!ETags.Matches(command.IfMatch, ETags.FactSheet(current.Id)))
        {
            return StaleTag();
        }

        // Step 3: draft and story_review only (F26); every other state gets the same code. The state gate is
        // answered before the addressed row's own status (task Notes decision 2): when no approval can happen in
        // the episode's state, that is the reason the client is told, whatever the row's status would have said.
        if (!episode.CanEditStory)
        {
            return InvalidState();
        }

        // Step 4: only a draft is approvable — a re-approve names the row the tag addresses, so it is a conflict
        // (decision 5).
        if (current.Status != FactSheetStatus.Draft)
        {
            return StaleTag();
        }

        // Step 5: the source-diversity gate, over the episode's own sources (a cited source that no longer exists
        // counts as ineligible, exactly like a deleted one — F13).
        var rows = await db.StorySources
            .Where(source => source.EpisodeId == command.EpisodeId)
            .Select(source => new
            {
                source.Id,
                source.Kind,
                source.Status,
                source.ContentSha256,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var sources = rows.ToDictionary(
            row => row.Id,
            row => new F13SourceInfo(row.Id, row.Kind, row.Status, row.ContentSha256));

        var gate = F13SourceDiversityCheck.Check(current.Facts, sources);
        if (gate.IsError)
        {
            return gate.Errors;
        }

        // Step 6: within one transaction, demote the episode's previous approved row first — and flush it — so the
        // partial unique index sees at most one approved row at any statement (decision 5).
        var previouslyApproved = await db.FactSheets
            .SingleOrDefaultAsync(
                sheet => sheet.EpisodeId == command.EpisodeId && sheet.Status == FactSheetStatus.Approved,
                cancellationToken)
            .ConfigureAwait(false);
        if (previouslyApproved is not null && previouslyApproved.Id != current.Id)
        {
            previouslyApproved.Supersede();

            var demoted = await FlushAsync(cancellationToken).ConfigureAwait(false);
            if (demoted is not null)
            {
                return demoted.Value;
            }
        }

        // Step 7: the approval itself and the event the outbox turns into the UI's EpisodeUpdated.
        var now = clock.UtcNow;
        var approved = current.Approve(currentUser.Id ?? episode.UserId, now);
        if (approved.IsError)
        {
            return approved.Errors;
        }

        var raised = episode.RaiseFactSheetApproved(current.Id, current.Version, now);
        if (raised.IsError)
        {
            return raised.Errors;
        }

        var conflict = await FlushAsync(cancellationToken).ConfigureAwait(false);
        if (conflict is not null)
        {
            return conflict.Value;
        }

        command.Outcome = new FactSheetApprovedOutcome(current.Id, current.Version);
        return Summary(current);
    }

    /// <summary>Flushes the pending changes inside the command's transaction; a unique refusal becomes 412.</summary>
    private async Task<Error?> FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (DbUpdateException exception) when (FactSheetFlushConflict.IsFactSheetUniqueViolation(exception))
        {
            return StaleTag();
        }
    }

    private static FactSheetSummaryDto Summary(FactSheet sheet) =>
        new(
            sheet.Id,
            sheet.Version,
            FactSheetWire.Status(sheet.Status),
            ETags.FactSheet(sheet.Id),
            sheet.Facts.Count,
            sheet.CreatedAt);

    private static Error SheetNotFound() =>
        Error.NotFound(ErrorCodes.FactSheetNotFound, "No fact sheet exists for this episode.");

    private static Error IfMatchRequired() =>
        Error.Failure(
            ErrorCodes.IfMatchRequired,
            "An If-Match header with the fact-sheet ETag you read is required.");

    private static Error StaleTag() =>
        Error.Failure(
            ErrorCodes.FactSheetConflict,
            "The fact sheet changed since it was read; read it again and retry.");

    private static Error InvalidState() =>
        Error.Conflict(ErrorCodes.EpisodeInvalidState, "A fact sheet cannot be approved in the episode's current state.");
}
