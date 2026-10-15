using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Bible;

namespace NightTales.Application.Features.SeriesBible.Commands.DismissBibleProposal;

/// <summary>
/// <c>POST /api/v1/series/{id}/bible/proposal/dismiss</c> (02.6.3-T2, plan §4 U3): clears the pending proposal and
/// answers 204, without a version bump.
/// </summary>
/// <remarks>
/// <para>
/// The check order is the accept command's: the row lock first, then the tenant-filtered loads — the series (404
/// for an unknown or another user's series, before the row is looked for at all) and the bible (500
/// <c>series_bible_missing</c> for the caller's own series without one, the PUT's drift answer) — and finally the
/// 409 <c>bible_proposal_not_pending</c> when there is nothing to dismiss.
/// </para>
/// <para>
/// The proposal was never content, so the write is the Domain's own <c>ClearProposal</c>: no version moves, no
/// event is raised (a dismissal is not a bible change, plan §5 lists <c>series_bible_updated.v1</c> for the PUT and
/// the accept only) and the audit row the pipeline writes is the only trace — ids and counts, never an op's text.
/// The row lock is taken even though the clear cannot conflict: it is what keeps this write serialised against an
/// accept of the same proposal, so exactly one of the two wins and the loser reads the row the winner left behind.
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="clock">The command's clock reading; the row's <c>updated_at</c> is never taken from the host clock.</param>
/// <param name="metrics">The feature's instruments, counted when the row is missing.</param>
public sealed class DismissBibleProposalCommandHandler(
    IAppDbContext db,
    IClock clock,
    SeriesBibleMetrics metrics) : ICommandHandler<DismissBibleProposalCommand, ErrorOr<Success>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> Handle(
        DismissBibleProposalCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await db.LockSeriesBibleForUpdateAsync(command.SeriesId, cancellationToken).ConfigureAwait(false);

        var series = await db.Series
            .SingleOrDefaultAsync(candidate => candidate.Id == command.SeriesId, cancellationToken)
            .ConfigureAwait(false);
        if (series is null)
        {
            return Errors.NotFound();
        }

        var bible = await db.SeriesBibles
            .SingleOrDefaultAsync(candidate => candidate.SeriesId == command.SeriesId, cancellationToken)
            .ConfigureAwait(false);
        if (bible is null)
        {
            metrics.Missing();
            return BibleErrors.Missing();
        }

        var proposal = bible.ProposedContent;
        if (proposal is null)
        {
            return BibleErrors.ProposalNotPending();
        }

        // The counts are read before the clear, because the projection of the audit row is built from the row this
        // command emptied.
        command.Outcome = new DismissBibleProposalOutcome(proposal.Ops.Count, bible.ProposedFromEpisodeId);

        bible.ClearProposal(clock);

        return Result.Success;
    }
}
