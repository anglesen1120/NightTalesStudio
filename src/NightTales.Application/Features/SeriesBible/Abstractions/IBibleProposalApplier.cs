using NightTales.Domain.Series.Bible;

// The feature namespace `…Features.SeriesBible` shadows the Domain entity's name inside this folder (the trap
// 09.4.1-T1 documents for the series aggregate), and `…Features.Series` shadows the aggregate's, so both are
// aliased — the shape `GetBibleHandler` uses.
using SeriesAggregate = NightTales.Domain.Series.Series;
using SeriesBibleEntity = NightTales.Domain.Series.Bible.SeriesBible;

namespace NightTales.Application.Features.SeriesBible.Abstractions;

/// <summary>
/// Applies the pending bible proposal of one series (02.6.3-T2, plan §4 U4/U5, F27): the single write path the
/// manual accept command and the autopilot auto-accept of the proposal job both take, so neither can grow a
/// second way of turning ops into content.
/// </summary>
/// <remarks>
/// <para>
/// The Domain's <c>SeriesBible.ApplyProposal</c> stays the only writer of the content: this port sanitises the
/// stored proposal (the accept path must run the proposal's text through the same character rules a
/// <c>PUT /series/{id}/bible</c> applies, or a later save would refuse the document the accept stored) and then
/// hands the ops to that method, under the caller's row lock.
/// </para>
/// <para>
/// It also announces the write (<c>series_bible_updated.v1</c>, plan §5): accepting a proposal is a content
/// version like a manual save is, so the same outbox row is raised, with <paramref name="changedBy"/> naming
/// the actor — the accepting user, or the owner for the autopilot auto-accept.
/// </para>
/// <para>
/// An op that cannot be applied is skipped, never fatal: the character rules and a summary whose episode is no
/// longer approved are the two such cases, and both are counted in the result's <c>Skipped</c>, so an accept
/// never fails for its content (plan §4 U4).
/// </para>
/// </remarks>
public interface IBibleProposalApplier
{
    /// <summary>
    /// Applies the ops of <paramref name="bible"/>'s pending proposal onto the content as it stands now and
    /// announces the new version.
    /// </summary>
    /// <remarks>
    /// The caller holds <c>IAppDbContext.LockSeriesBibleForUpdateAsync</c> for the series and passes both
    /// entities tracked, exactly as <c>UpdateBibleCommandHandler</c> does: the lock is what makes the
    /// append-only apply safe against a concurrent manual save (plan §4 U4).
    /// </remarks>
    /// <param name="series">The aggregate that announces the write; tracked.</param>
    /// <param name="bible">The bible row that carries the proposal; tracked.</param>
    /// <param name="source">
    /// The episode an <c>add_summary</c> op belongs to, with its derived structure and approval instant, or
    /// <see langword="null"/> when that episode is no longer approved (or is gone): a summary may only be stored
    /// for an approved episode of the series, so the pending <c>add_summary</c> ops are then skipped and the rest
    /// of the proposal applies.
    /// </param>
    /// <param name="changedBy">The actor of the write: the accepting user, or the owner for an autopilot auto-accept.</param>
    /// <returns>The ops that changed the content and how many were skipped, sanitisation included; ids and counts only (D13/D19).</returns>
    /// <exception cref="InvalidOperationException">There is no pending proposal, or the announcement the Domain refuses.</exception>
    /// <exception cref="ArgumentException"><paramref name="changedBy"/> is empty.</exception>
    BibleApplyResult Apply(
        SeriesAggregate series,
        SeriesBibleEntity bible,
        BibleSummarySource? source,
        Guid changedBy);
}
