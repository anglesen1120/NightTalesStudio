using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Similarity;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Commands.ApproveStory;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Quality;
using NightTales.Domain.Stories.Similarity;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.Stories.Approval;

/// <summary>
/// The real approval precondition gate of 02.3.3-T1 (02 README D8, D21, F12, F13 and 02 §5.9): the
/// sub-checks 3a-3e of the 02.3.2-T1 seam, in the fixed order topic, fact sheet, sources, similarity,
/// attestation. It fails closed on every error path and never reads or returns attestation text, a story
/// body or a URL.
/// <para>
/// [DECISION] SEC-1 pinned data (02.3.2-T1 hand-over): a story is treated as true-crime for 3b, 3c and 3e
/// when the series topic is <see cref="Topic.TrueCrime"/> <em>or</em> the version carries a
/// <see cref="StoryVersion.FactSheetId"/>. A story generated from a fact sheet therefore stays true-crime
/// after the series topic is patched to fiction, so that patch cannot bypass the fact-sheet, source or
/// attestation requirements. <c>ApproveStoryCommandHandler</c> applies the same predicate to the forwarded
/// attestation and to the <c>episode.true_crime_attested</c> audit row.
/// </para>
/// <para>
/// [DECISION] guard-error mapping: at approve time the D8 code for "no current approved fact sheet" is
/// <c>story_fact_sheet_outdated</c> (409), so the fact-sheet guard's generate-time
/// <c>fact_sheet_required</c> is mapped to it; any other guard error passes through unchanged.
/// </para>
/// </summary>
public sealed class StoryApprovalGate(
    IFactSheetGuard factSheets,
    IReadDb readDb,
    ISimilarityCorpusProvider similarityCorpus,
    StoryTrueCrimeOptions trueCrime,
    SimilaritySettings similaritySettings) : IStoryApprovalGate
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<StoryApprovalGateResult>> ValidateAsync(
        Episode episode,
        SeriesAggregate series,
        StoryVersion version,
        AttestationInput? attestation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(episode);
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(version);

        // 3a: the topic, plus the SEC-1 pinned-data rule documented on the class.
        var trueCrimeTopic = series.Topic == Topic.TrueCrime || version.FactSheetId is not null;
        IReadOnlyList<StorySource>? urlSources = null;

        if (trueCrimeTopic)
        {
            // 3b (F12): the story must have been written from the currently approved sheet.
            var sheet = await factSheets
                .EnsureApprovedAsync(episode.Id, Topic.TrueCrime, ct)
                .ConfigureAwait(false);
            if (sheet.IsError)
            {
                return sheet.FirstError.Code == ErrorCodes.FactSheetRequired ? Outdated() : sheet.Errors;
            }

            if (sheet.Value.FactSheetId is not { } approvedSheetId || version.FactSheetId != approvedSheetId)
            {
                return Outdated();
            }

            // 3c (F13): at least MinCitedSources distinct qualifying sources, cited by a kept fact.
            urlSources = await LoadUrlSourcesAsync(episode.Id, ct).ConfigureAwait(false);
            var qualifying = TrueCrimeSourceRule.QualifyingDistinctHashCount(
                [.. urlSources.Select(source => new TrueCrimeSourceRule.Candidate(
                    source.Id, source.Kind, source.Status, source.ContentSha256))],
                CitedSourceIds(sheet.Value));

            if (qualifying < trueCrime.MinCitedSources)
            {
                return Error.Conflict(
                    ErrorCodes.StorySourcesRequired,
                    $"A true-crime story needs at least {trueCrime.MinCitedSources} distinct fetched sources cited by a kept fact; this story cites {qualifying}.");
            }
        }

        // 3d (02 §5.9): not topic-scoped — any episode with url sources is scored, fiction included.
        var options = SimilarityOptions.For(series.Language, similaritySettings);
        var score = version.SimilarityScore;
        if (score is null)
        {
            urlSources ??= await LoadUrlSourcesAsync(episode.Id, ct).ConfigureAwait(false);
            if (urlSources.Count > 0)
            {
                var recomputed = await RecomputeScoreAsync(episode, series.Language, version, options, ct)
                    .ConfigureAwait(false);
                if (recomputed.IsError)
                {
                    return recomputed.Errors;
                }

                score = recomputed.Value;
            }
        }

        if (SimilarityApprovalGuard.Classify((double?)score, options) == SimilarityVerdict.Block)
        {
            return Error.Conflict(
                ErrorCodes.StoryTooSimilarToSource,
                "The story overlaps its sources above the block threshold.");
        }

        // 3e: the explicit attestation tick, only for true crime (a fiction tick is ignored).
        if (!trueCrimeTopic)
        {
            return new StoryApprovalGateResult(null);
        }

        if (attestation is not { Accepted: true }
            || !string.Equals(attestation.TextVersion, trueCrime.AttestationVersion, StringComparison.Ordinal))
        {
            return Error.Validation(
                ErrorCodes.StoryAttestationRequired,
                "The current attestation text version must be accepted before a true-crime story can be approved.");
        }

        return new StoryApprovalGateResult(trueCrime.AttestationVersion);
    }

    // The episode's url sources, read once through the tenant-filtered read model (09 README D8/D10).
    private async ValueTask<IReadOnlyList<StorySource>> LoadUrlSourcesAsync(Guid episodeId, CancellationToken ct)
    {
        var sources = await readDb.Query<StorySource>()
            .Where(source => source.EpisodeId == episodeId && source.Kind == StorySourceKind.Url)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return sources;
    }

    // F13: the sources cited by the approved sheet's kept facts, with each fact's aliases resolved to ids.
    // Dropped facts never reach the approved sheet, so they contribute nothing.
    private static HashSet<Guid> CitedSourceIds(ApprovedFactSheet sheet)
    {
        var cited = new HashSet<Guid>();
        foreach (var fact in sheet.Facts)
        {
            foreach (var alias in fact.SourceAliases)
            {
                if (sheet.AliasToSourceId.TryGetValue(alias, out var sourceId))
                {
                    cited.Add(sourceId);
                }
            }
        }

        return cited;
    }

    // 3d: recomputes a missing score from the stored draft exactly as the generation pipeline does. An
    // unreadable or unscorable draft yields no score (Pass); an unreadable corpus fails closed.
    private async ValueTask<ErrorOr<decimal?>> RecomputeScoreAsync(
        Episode episode,
        Language language,
        StoryVersion version,
        SimilarityOptions options,
        CancellationToken ct)
    {
        if (!StoryContentJson.TryReadStored(version.Content, out var content))
        {
            return (decimal?)null;
        }

        IReadOnlyList<CorpusText> corpus;
        try
        {
            corpus = await similarityCorpus.LoadAsync(episode.Id, language, ct).ConfigureAwait(false);
        }
        catch (SimilarityCorpusUnavailableException)
        {
            ct.ThrowIfCancellationRequested();

            // Fail closed: the similarity verdict cannot be decided while the corpus is unreadable, so the
            // approval is retryable rather than silently passing the source-overlap guard.
            return Error.Failure(
                ErrorCodes.ServiceUnavailable,
                "The source corpus could not be read; retry the approval.");
        }

        var story = NightTales.Domain.Stories.Similarity.StoryText.From(content);
        return SimilarityScorer.IsScorable(story, corpus, options)
            ? (decimal)Math.Round(SimilarityScorer.Score(story, corpus, options).Score, 4, MidpointRounding.AwayFromZero)
            : (decimal?)null;
    }

    private static Error Outdated() => Error.Conflict(
        ErrorCodes.StoryFactSheetOutdated,
        "The story was not written from the currently approved fact sheet.");
}
