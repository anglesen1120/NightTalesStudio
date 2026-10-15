using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Features.Rendering;
using NightTales.Application.Features.Research.Jobs;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Keywords;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Infrastructure.Rendering;

/// <summary>
/// The name corpus of one episode (03 README D11, 03.2.2-T1), over 09 README D8's no-tracking
/// <c>IReadDb</c>: the titles of the episode's fetched <c>url</c> <c>story_sources</c> and the statements
/// of its one approved <c>fact_sheets</c> row.
/// </summary>
/// <remarks>
/// <para>
/// Every read runs under the ambient tenant filter of the scope's read model (09 README D10), so an
/// episode, a source or a sheet of another user is invisible and yields <see cref="NameCorpus.Empty"/>;
/// no read here uses <c>IgnoreQueryFilters</c>.
/// </para>
/// <para>
/// The corpus carries ids, titles and statements only into memory — never into a log line: the class has
/// no logger, so no name can reach the log (02 README D19). The three reads are one query each, never a
/// per-row query. The title count is one per fetched url source of the episode, bounded by the episode's source
/// cap (<c>Sources:MaxPerEpisode</c>, default 10); the fact count is bounded by
/// <see cref="FactSheetJobOptions.MaxMaxFacts"/> (the <c>facts.v1</c> ceiling of one approved version, 40); and
/// the corpus reads at most <see cref="NameCorpus.MaxTexts"/> texts in total, so no read here is bounded by a
/// Take.
/// </para>
/// <para>
/// The corpus is built with <see cref="NameCorpus.From(IEnumerable{string}, IEnumerable{string})"/>, so D11's
/// Title Case guard runs on the source titles only (CR-18d): a sentence-case fact statement is never skipped as
/// a headline, which is what keeps a statement naming two people in the corpus.
/// </para>
/// </remarks>
/// <param name="readDb">The scope's read model; it carries the tenant filter and never tracks.</param>
/// <param name="options">The bound, validated <c>Images</c> section (<c>Images:Linter:NameCorpusEnabled</c>).</param>
public sealed class NameCorpusProvider(IReadDb readDb, ImagesOptions options) : INameCorpusProvider
{
    /// <inheritdoc />
    public async ValueTask<NameCorpus> LoadAsync(Guid episodeId, CancellationToken ct)
    {
        // D11's rollback: the flag stops the name rule before anything is read (KeywordRules stay on), so a
        // deployment can turn the feature off without a corpus query.
        if (!options.Linter.NameCorpusEnabled)
        {
            return NameCorpus.Empty;
        }

        // The series' topic decides: only true crime has real-person names, and fiction is [DECISION] empty.
        // Episodes.series_id is a required FK, and the join carries the same tenant filter, so an episode of
        // another user (or a deleted one) yields no row here.
        var topic = await readDb.Query<Episode>()
            .Where(episode => episode.Id == episodeId)
            .Join(
                readDb.Query<SeriesAggregate>(),
                episode => episode.SeriesId,
                series => series.Id,
                (_, series) => (Topic?)series.Topic)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (topic != Topic.TrueCrime)
        {
            return NameCorpus.Empty;
        }

        // One query for the titles: url sources the fetcher produced text for; a note's text never becomes a
        // name source (its kind is user_note), and a source without a title has nothing to fold.
        var titles = await readDb.Query<StorySource>()
            .Where(source => source.EpisodeId == episodeId
                && source.Kind == StorySourceKind.Url
                && source.Status == StorySourceStatus.Fetched
                && source.Title != null
                && source.Title != string.Empty)
            .Select(source => source.Title!)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // One query for the facts: the single approved sheet of the episode (the partial unique index keeps
        // at most one). Only the `facts` document is projected — the aliases document and the audit columns
        // stay in the database.
        var facts = await readDb.Query<FactSheet>()
            .Where(sheet => sheet.EpisodeId == episodeId && sheet.Status == FactSheetStatus.Approved)
            .Select(sheet => sheet.Facts)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // Titles and statements stay apart: the Title Case guard is for headlines and must not skip a fact
        // statement (CR-18d), so the corpus is built through the two-argument overload.
        var statements = new List<string>(facts?.Count ?? 0);
        if (facts is not null)
        {
            foreach (var fact in facts)
            {
                statements.Add(fact.Statement);
            }
        }

        return titles.Count == 0 && statements.Count == 0
            ? NameCorpus.Empty
            : NameCorpus.From(titles, statements);
    }
}
