using NightTales.Domain.Common;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Structure;

// The feature namespace `…Features.SeriesBible` shadows the Domain entity's name inside this folder (the trap
// 09.4.1-T1 documents for the series aggregate), and `…Features.Series` shadows the aggregate's, so both are
// aliased.
using SeriesAggregate = NightTales.Domain.Series.Series;
using SeriesBibleEntity = NightTales.Domain.Series.Bible.SeriesBible;

namespace NightTales.Application.Features.SeriesBible;

/// <summary>
/// Applies a pending bible proposal through the one Domain writer (02.6.3-T2, <see cref="Abstractions.IBibleProposalApplier"/>):
/// sanitise the stored proposal, apply it onto the current content, announce the version.
/// </summary>
/// <remarks>
/// <para>
/// Sanitising is not optional and not cosmetic: <c>BibleOpNormalizer</c> only strips unpaired surrogates, so a
/// proposal carrying a <c>&lt;</c>, a control, a format, a bidi or a tag character would be stored by an accept
/// and then refused by the very next <c>PUT /series/{id}/bible</c>, whose content validator applies the same
/// character rules to every field (02.6.3-T1 note, plan §4 U4). The removed ops are counted as skipped, so the
/// audit row of the accept still reconciles: applied + skipped = the ops the proposal carried.
/// </para>
/// <para>
/// The sanitised proposal is written back onto the row before the apply, because
/// <c>SeriesBible.ApplyProposal</c> reads <c>ProposedContent</c>: dropping the ops here and letting the Domain
/// method walk the original list would sanitise nothing. Nothing else about the proposal moves — the ops it
/// keeps stay in model order, and the apply clears it in the same step as it bumps the version.
/// </para>
/// <para>
/// The port holds no state and no database: the caller owns the row lock and the transaction, and this type only
/// sequences the Domain calls.
/// </para>
/// </remarks>
/// <param name="limits">The configured content limits (plan §3.2), from the <c>Bible:Limits</c> section; the same instance the PUT handler applies.</param>
/// <param name="clock">The writing clock reading; the version timestamp is never taken from the host clock.</param>
public sealed class BibleProposalApplier(BibleLimits limits, IClock clock) : Abstractions.IBibleProposalApplier
{
    /// <inheritdoc />
    public BibleApplyResult Apply(
        SeriesAggregate series,
        SeriesBibleEntity bible,
        BibleSummarySource? source,
        Guid changedBy)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(bible);

        if (changedBy == Guid.Empty)
        {
            throw new ArgumentException("An accepted proposal needs the user who accepted it.", nameof(changedBy));
        }

        var stored = bible.ProposedContent
            ?? throw new InvalidOperationException("There is no pending proposal to apply.");

        // The episode the ops were computed from: the source a summary op is bound to, and the row's own record
        // of it. A proposal without one (its record was lost) cannot be applied — the summary rule needs it, and
        // a rewrite of the proposal would have nothing to keep in that column.
        var episodeId = source?.EpisodeId ?? bible.ProposedFromEpisodeId;
        if (episodeId is null || episodeId.Value == Guid.Empty)
        {
            throw new InvalidOperationException("A pending proposal must record the episode it came from.");
        }

        var sanitised = BibleProposalValidator.SanitizeStored(stored);
        var removed = stored.Ops.Count - sanitised.Ops.Count;

        // A summary may only be stored for an approved episode of the series (BibleContentValidator's
        // not_approved_episode rule), so ops of that kind whose episode is no longer approved add nothing and are
        // dropped here rather than stored as content a later PUT would refuse.
        if (source is null)
        {
            var bound = new List<BibleOp>(sanitised.Ops.Count);
            foreach (var op in sanitised.Ops)
            {
                if (op.Kind == BibleOpKind.AddSummary)
                {
                    removed++;
                    continue;
                }

                bound.Add(op);
            }

            sanitised = sanitised with { Ops = bound };
        }

        if (!ReferenceEquals(stored, sanitised))
        {
            // The proposal keeps the episode it came from, and the apply below clears it together with the ops it
            // applied. The rewrite is what makes the sanitisation effective at all: the Domain method walks the
            // row's proposal, so leaving the original there would apply the very ops just dropped.
            bible.SetProposal(sanitised, episodeId.Value, clock);
        }

        var applied = bible.ApplyProposal(limits, source ?? Unbound(episodeId.Value), clock);

        // The announcement of the new version (plan §5). It is raised after the write, so the outbox row joins
        // the accept's own transaction; its two refusals are caller bugs, not states (a version below 1 cannot
        // exist here and changedBy was checked above), so they surface as an exception rather than an error the
        // caller has no shape for.
        var raised = series.RaiseSeriesBibleUpdated(bible.Version, changedBy, clock.UtcNow);
        if (raised.IsError)
        {
            throw new InvalidOperationException(
                $"The bible announcement of version {bible.Version} was refused: {raised.FirstError.Code}.");
        }

        return removed == 0
            ? applied
            : new BibleApplyResult(applied.Applied, applied.Skipped + removed);
    }

    /// <summary>
    /// The source an apply runs with when the proposal's episode is no longer approved: every
    /// <c>add_summary</c> op was dropped before this call, so the Domain method reads neither of the two derived
    /// members. The episode id is the real one, so the call still says which episode the ops came from.
    /// </summary>
    /// <param name="episodeId">The episode the proposal came from; never empty.</param>
    /// <returns>The source that is never read.</returns>
    private BibleSummarySource Unbound(Guid episodeId) =>
        new(episodeId, StoryStructureTags.Empty, clock.UtcNow);
}
