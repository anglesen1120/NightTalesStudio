using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.SeriesBible.Abstractions;
using NightTales.Application.Features.SeriesBible.Commands.UpdateBible;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Application.Features.SeriesBible.Commands.AcceptBibleProposal;

/// <summary>
/// <c>POST /api/v1/series/{id}/bible/proposal/accept</c> (02.6.3-T2, plan §4 U4): applies the pending proposal to
/// the bible as it stands now and answers the new version with its <c>"sb-&lt;version&gt;"</c> tag.
/// </summary>
/// <remarks>
/// <para>
/// The check order is the PUT's, minus its <c>If-Match</c>: the row lock first
/// (<c>LockSeriesBibleForUpdateAsync</c> — <c>Version</c> is not an EF concurrency token, STATUS BIBLE-4), the
/// tenant-filtered loads (an unknown series and another user's are both 404), the drift 500 for a series without a
/// bible row, then the 409 <c>bible_proposal_not_pending</c> when the row carries no proposal. Nothing reads a page
/// of the proposal from the client: the ops, the version and the counts are all the stored row's.
/// </para>
/// <para>
/// The ops are applied by <see cref="IBibleProposalApplier"/>, the one write path this route shares with the
/// autopilot auto-accept of the job: the Domain's <c>ApplyProposal</c> appends onto the current content, bumps the
/// version once and clears the proposal, and the applier announces the new version as
/// <c>series_bible_updated.v1</c> with the accepting user as <c>changed_by</c>.
/// </para>
/// <para>
/// A summary op is bound to the episode the proposal came from, and its two server-derived members come from that
/// episode's approved story version, exactly as the PUT derives them for a client-added summary
/// (<see cref="UpdateBibleCommandHandler"/>). The op is bound to the approval of <em>the moment of the accept</em>:
/// an episode that was reopened in the meantime has no approval left to derive them from, and since a stored
/// summary for an unapproved episode would make the next PUT refuse the whole document
/// (<c>not_approved_episode</c>), the applier skips those ops — an accept never fails for its content (plan §4 U4).
/// </para>
/// <para>
/// The command's own effects are saved and committed by the pipeline's transaction, which also maps a lost update
/// or a transient database refusal to <c>concurrency_conflict</c> / <c>service_unavailable</c> (D4/D5). The audit
/// row of the accept joins that same transaction (BD15).
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the pipeline commits it.</param>
/// <param name="currentUser">The authenticated actor: the <c>changed_by</c> of the announced version.</param>
/// <param name="applier">The one writer of an accepted proposal.</param>
/// <param name="metrics">The feature's instruments, counted when the row is missing.</param>
public sealed class AcceptBibleProposalCommandHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    IBibleProposalApplier applier,
    SeriesBibleMetrics metrics) : ICommandHandler<AcceptBibleProposalCommand, ErrorOr<UpdateBibleResult>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<UpdateBibleResult>> Handle(
        AcceptBibleProposalCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Step 1: the row lock first, then the tenant-filtered loads — the order the PUT handler documents, so two
        // writers of one bible serialise instead of racing.
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

        // Step 2: nothing pending means the proposal was already accepted or dismissed (a repeat of this very
        // call), which is the route's 409 — never a silent no-op that would bump a version for nothing. A proposal
        // whose episode column was lost is answered the same way: the ops were computed from an episode the row no
        // longer names, so there is nothing to bind their summaries to.
        if (bible.ProposedContent is null || bible.ProposedFromEpisodeId is null)
        {
            return BibleErrors.ProposalNotPending();
        }

        // Step 3: the summary sources, one per episode a summary op of the proposal belongs to. An op carries its own
        // episode; a legacy op (stored before that) belongs to the episode the row records. An episode that cannot be
        // resolved is left out, and the Domain method skips its summary; the rest of the proposal still applies.
        var proposedFromEpisodeId = bible.ProposedFromEpisodeId.Value;
        var summaryEpisodeIds = bible.ProposedContent.Ops
            .Where(static op => op.Kind == BibleOpKind.AddSummary)
            .Select(op => op.EpisodeId ?? proposedFromEpisodeId)
            .Distinct();

        var summarySources = new List<BibleSummarySource>();
        foreach (var summaryEpisodeId in summaryEpisodeIds)
        {
            var source = await ResolveSummarySourceAsync(
                command.SeriesId,
                summaryEpisodeId,
                cancellationToken).ConfigureAwait(false);
            if (source is not null)
            {
                summarySources.Add(source);
            }
        }

        var fromVersion = bible.Version;
        var result = applier.Apply(series, bible, summarySources, currentUser.Id!.Value);

        command.Outcome = new AcceptBibleProposalOutcome(
            fromVersion,
            bible.Version,
            result.Applied.Count,
            result.Skipped,
            proposedFromEpisodeId);

        return new UpdateBibleResult(bible.Version, BibleEtags.For(bible.Version));
    }

    /// <summary>
    /// The episode an <c>add_summary</c> op belongs to, with the values only the server owns: the structure tags of
    /// its approved story version and the instant it was approved (plan §4 U2/U4).
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> is not a failure but the one case the accept tolerates: the episode is unknown, was
    /// reopened, or its approved version cannot be read, so no summary op can be bound to anything and the applier
    /// skips them. The episode is looked up inside the series and under the caller's tenant filter, so a foreign id
    /// in the column resolves to that same <see langword="null"/> rather than to another user's episode.
    /// </remarks>
    /// <param name="seriesId">The series the bible belongs to.</param>
    /// <param name="episodeId">The row's <c>proposed_from_episode_id</c>; never empty (the handler answers that as 409).</param>
    /// <param name="cancellationToken">Cancels the two reads.</param>
    /// <returns>The source, or <see langword="null"/> when the proposal's summaries cannot be bound.</returns>
    private async Task<BibleSummarySource?> ResolveSummarySourceAsync(
        Guid seriesId,
        Guid episodeId,
        CancellationToken cancellationToken)
    {
        if (episodeId == Guid.Empty)
        {
            return null;
        }

        var episode = await db.Episodes
            .AsNoTracking()
            .Where(candidate => candidate.Id == episodeId && candidate.SeriesId == seriesId)
            .Select(candidate => new ApprovedEpisode(
                candidate.Id,
                candidate.ApprovedStoryVersionId,
                candidate.ApprovedAt))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (episode?.StoryVersionId is null)
        {
            return null;
        }

        var version = await db.StoryVersions
            .AsNoTracking()
            .Where(candidate => candidate.Id == episode.StoryVersionId.Value)
            .Select(candidate => new ApprovedStoryVersion(candidate.Content, candidate.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (version is null || !StoryContentJson.TryReadStored(version.Content, out var story))
        {
            return null;
        }

        // approved_at is the approval transaction's; a row that lost the denormalized column falls back to the
        // approved version's own creation time rather than to the host clock (the PUT's derivation does the same).
        return new BibleSummarySource(
            episode.Id,
            StoryStructureTags.FromStoryContent(story),
            episode.ApprovedAt ?? version.CreatedAt);
    }

    /// <summary>An episode of the series, as the summary source reads it: its approval, when it has one.</summary>
    private sealed record ApprovedEpisode(Guid Id, Guid? StoryVersionId, DateTimeOffset? ApprovedAt);

    /// <summary>The approved version of that episode, as the derivation reads it.</summary>
    private sealed record ApprovedStoryVersion(string Content, DateTimeOffset CreatedAt);
}
