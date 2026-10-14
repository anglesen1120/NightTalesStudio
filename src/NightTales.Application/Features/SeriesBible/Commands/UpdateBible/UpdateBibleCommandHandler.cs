using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Application.Features.SeriesBible.Commands.UpdateBible;

/// <summary>
/// <c>PUT /api/v1/series/{id}/bible</c> (02.6.3-T1, plan §4 U2): the client's <c>bible.v1</c> document
/// replaces the stored one under the <c>"sb-&lt;version&gt;"</c> ETag it read.
/// </summary>
/// <remarks>
/// <para>
/// The check order is the contract's: the tenant-filtered load (an unknown series and another user's are
/// both 404), the required <c>If-Match</c> (428 <c>if_match_required</c> without it, 412
/// <c>bible_conflict</c> for a stale, foreign or malformed tag — <c>W/…</c> included, and <c>*</c> matching
/// the current version), the server-derived summary members, then the body (400
/// <c>bible_invalid_content</c>), so an invalid body never hides the reason a save is impossible.
/// </para>
/// <para>
/// A <c>recurring_characters</c>/<c>places</c>/<c>rules</c> entry the client sends with a blank or absent
/// <c>id</c> is a new entry: its id is allocated by <see cref="SeriesBible.AssignMissingIds"/> (the feature's
/// one <c>max + 1</c> rule, over the request's ids and the stored content's) <em>before</em> the validator
/// runs, so a client can add an entry without knowing the next id and the validator only ever checks the
/// shape, the prefix and the uniqueness of an id that exists.
/// </para>
/// <para>
/// The row lock is the first statement, as the fact-sheet edit of 02.5.4-T1 takes it: two saves that read
/// the same version serialise, the second one reads the first one's version and answers 412 rather than
/// writing a lost update. The version column is the token (D9) — never <c>xmin</c>, and no second
/// concurrency mechanism.
/// </para>
/// <para>
/// Nothing is trusted from the request beyond the members the client owns: <c>schema_version</c> is written
/// by the backend, and a summary's <c>structure</c> and <c>approved_at</c> are re-derived from the
/// episode's approved story version (<see cref="BibleSummaryDerivation"/>). The content limits and the
/// episode rules are the Domain validator's, so its field list survives into the ProblemDetails
/// <c>errors</c> map.
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="limits">The configured content limits (plan §3.2), from the <c>Bible:Limits</c> section.</param>
/// <param name="clock">The command's clock reading; the version timestamp is never read from the host clock.</param>
/// <param name="metrics">The feature's instruments, counted when the row is missing.</param>
public sealed class UpdateBibleCommandHandler(
    IAppDbContext db,
    BibleLimits limits,
    IClock clock,
    SeriesBibleMetrics metrics) : ICommandHandler<UpdateBibleCommand, ErrorOr<UpdateBibleResult>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<UpdateBibleResult>> Handle(
        UpdateBibleCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Step 1: the row lock first, then the tenant-filtered load.
        await db.LockSeriesBibleForUpdateAsync(command.SeriesId, cancellationToken).ConfigureAwait(false);

        var bible = await db.SeriesBibles
            .SingleOrDefaultAsync(candidate => candidate.SeriesId == command.SeriesId, cancellationToken)
            .ConfigureAwait(false);
        if (bible is null)
        {
            // The row is created with its series (§3.3): absent means an unknown or another user's series
            // (404), or data drift on one of the caller's own series (500 + alert, never an implicit create).
            var known = await db.Series
                .AnyAsync(series => series.Id == command.SeriesId, cancellationToken)
                .ConfigureAwait(false);
            if (!known)
            {
                return Errors.NotFound();
            }

            metrics.Missing();
            return BibleErrors.Missing();
        }

        // Step 2: 428 without the header, 412 for a tag that does not name this version.
        var precondition = BibleEtags.RequireAndMatch(command.IfMatch, bible.Version);
        if (precondition.IsError)
        {
            return precondition.Errors;
        }

        // Step 3: the body. A body that carried no content object at all answers the same 400 as a content
        // that breaks a rule, so the client reads one error shape.
        if (command.Content is null)
        {
            return BibleErrors.InvalidContent(
                [new BibleFieldError("content", "empty", "The body must carry the bible.v1 content object.")]);
        }

        // Step 4: the approved episodes of this series — the set a summary's episode_id may name, and the
        // source of the structure and the approval time of a summary the client newly adds (§3.2, §4 U2).
        var approved = await db.Episodes
            .AsNoTracking()
            .Where(episode => episode.SeriesId == command.SeriesId && episode.ApprovedStoryVersionId != null)
            .Select(episode => new ApprovedEpisode(
                episode.Id,
                episode.ApprovedStoryVersionId!.Value,
                episode.ApprovedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var approvedEpisodeIds = new HashSet<Guid>(approved.Count);
        foreach (var episode in approved)
        {
            approvedEpisodeIds.Add(episode.Id);
        }

        var storedSummaries = bible.Content.EpisodeSummaries;
        var derivation = await DeriveSummaryValuesAsync(
            approved,
            command.Content.EpisodeSummaries,
            storedSummaries,
            cancellationToken).ConfigureAwait(false);
        if (derivation.IsError)
        {
            return derivation.Errors;
        }

        var derived = derivation.Value;

        var requested = command.Content with
        {
            EpisodeSummaries = BibleSummaryDerivation.Merge(
                command.Content.EpisodeSummaries,
                storedSummaries,
                derived),
        };

        // Step 5: the ids. A blank or absent id means the client is adding an entry and does not know its id, so
        // it is allocated here — before validation — by the feature's one rule, over the request's own ids and the
        // stored content's (plan §4 U2, U4). An id the client did send stays verbatim: it is the validator that
        // refuses one that does not name an entry of its array or repeats another.
        var content = SeriesBible.AssignMissingIds(requested, bible.Content);

        var fieldErrors = BibleContentValidator.Validate(content, limits, approvedEpisodeIds);
        if (fieldErrors.Count > 0)
        {
            return BibleErrors.InvalidContent(fieldErrors);
        }

        // Step 6: the write. ReplaceContent bumps the version (D9) and keeps a pending proposal: its ops
        // stay append-only, so a manual edit can never be undone by accepting one (§4 U2, U4).
        bible.ReplaceContent(content, limits, clock);

        var conflict = await FlushAsync(cancellationToken).ConfigureAwait(false);
        if (conflict is not null)
        {
            return conflict.Value;
        }

        command.Outcome = new UpdateBibleOutcome(
            bible.Version,
            content.RecurringCharacters.Count,
            content.Places.Count,
            content.Rules.Count,
            content.EpisodeSummaries.Count);

        return new UpdateBibleResult(bible.Version, BibleEtags.For(bible.Version));
    }

    /// <summary>
    /// The server values of the summaries the client newly adds: the structure tags of the episode's
    /// approved story version and the moment it was approved. An episode the bible already describes keeps
    /// its stored values; an episode that is not approved, or unknown, gets no entry here and keeps the
    /// placeholder the DTO built, which the validator refuses with the field path the client can act on.
    /// </summary>
    /// <remarks>
    /// An <em>approved</em> episode whose version cannot be read is the one case this cannot answer with a
    /// placeholder: the row is drift the client cannot fix, and storing the summary with an empty structure
    /// would be silently wrong data, so it is refused as <c>invalid_structure</c> on that entry.
    /// </remarks>
    /// <param name="approved">The series' approved episodes, with the version each approval points at.</param>
    /// <param name="requested">The summaries of the request body, in body order.</param>
    /// <param name="stored">The summaries the bible currently stores.</param>
    /// <param name="cancellationToken">Cancels the version read.</param>
    /// <returns>The derived values, or the refusal above.</returns>
    private async Task<ErrorOr<Dictionary<Guid, BibleSummaryServerValues>>> DeriveSummaryValuesAsync(
        IReadOnlyList<ApprovedEpisode> approved,
        IReadOnlyList<BibleEpisodeSummary> requested,
        IReadOnlyList<BibleEpisodeSummary> stored,
        CancellationToken cancellationToken)
    {
        var derived = new Dictionary<Guid, BibleSummaryServerValues>();
        if (requested.Count == 0 || approved.Count == 0)
        {
            return derived;
        }

        var storedEpisodes = new HashSet<Guid>(stored.Count);
        foreach (var summary in stored)
        {
            storedEpisodes.Add(summary.EpisodeId);
        }

        // The field path of a refusal names the element the client sent, so the index of the first summary
        // per episode is kept alongside the set of the requested ids.
        var requestedIndex = new Dictionary<Guid, int>(requested.Count);
        for (var index = 0; index < requested.Count; index++)
        {
            requestedIndex.TryAdd(requested[index].EpisodeId, index);
        }

        var needed = new List<ApprovedEpisode>(approved.Count);
        foreach (var episode in approved)
        {
            if (requestedIndex.ContainsKey(episode.Id) && !storedEpisodes.Contains(episode.Id))
            {
                needed.Add(episode);
            }
        }

        if (needed.Count == 0)
        {
            return derived;
        }

        var versionIds = new List<Guid>(needed.Count);
        foreach (var episode in needed)
        {
            versionIds.Add(episode.StoryVersionId);
        }

        var versions = await db.StoryVersions
            .AsNoTracking()
            .Where(version => versionIds.Contains(version.Id))
            .Select(version => new ApprovedStoryVersion(version.Id, version.Content, version.CreatedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byId = new Dictionary<Guid, ApprovedStoryVersion>(versions.Count);
        foreach (var version in versions)
        {
            byId[version.Id] = version;
        }

        foreach (var episode in needed)
        {
            if (!byId.TryGetValue(episode.StoryVersionId, out var version)
                || !StoryContentJson.TryReadStored(version.Content, out var story))
            {
                return BibleErrors.InvalidContent(
                [
                    new BibleFieldError(
                        $"episode_summaries[{requestedIndex[episode.Id]}].structure",
                        "invalid_structure",
                        "The approved story version of this episode could not be read, so the summary's structure cannot be derived."),
                ]);
            }

            // approved_at is the approval transaction's; a row that lost the denormalized column falls back
            // to the approved version's own creation time rather than to the host clock.
            derived[episode.Id] = new BibleSummaryServerValues(
                StoryStructureTags.FromStoryContent(story),
                episode.ApprovedAt ?? version.CreatedAt);
        }

        return derived;
    }

    /// <summary>
    /// Flushes the pending change inside the command's transaction. The row lock of step 1 normally makes
    /// this unreachable; a version that moved anyway (a writer that bypassed the lock, a serialisation
    /// failure) answers the same 412 a stale tag does, because the client must re-read either way.
    /// </summary>
    private async Task<Error?> FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return BibleErrors.Conflict();
        }
        catch (DbUpdateException exception) when (BibleFlushConflict.IsSerialisationFailure(exception))
        {
            return BibleErrors.Conflict();
        }
    }

    /// <summary>An approved episode of the series: the id and the story version its approval points at.</summary>
    private sealed record ApprovedEpisode(Guid Id, Guid StoryVersionId, DateTimeOffset? ApprovedAt);

    /// <summary>The approved version of one of those episodes, as the derivation reads it.</summary>
    private sealed record ApprovedStoryVersion(Guid Id, string Content, DateTimeOffset CreatedAt);
}
