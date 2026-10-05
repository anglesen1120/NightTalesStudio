using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Similarity;
using NightTales.Application.Abstractions.Storage;
using NightTales.Domain.Common;
using NightTales.Domain.Research;
using NightTales.Domain.Stories.Similarity;

namespace NightTales.Infrastructure.Persistence.Queries;

/// <summary>Loads bounded corpora without bypassing tenant filters or retaining source metadata.</summary>
public sealed class SimilarityCorpusProvider(
    AppDbContext db,
    IExtractedTextStore textStore,
    IOptions<SimilaritySettings> settings) : ISimilarityCorpusProvider
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CorpusText>> LoadAsync(Guid episodeId, Language lang, CancellationToken ct)
    {
        // The frozen port reserves language; raw source text is language-independent today.
        _ = lang;
        var limits = settings.Value.Corpus;
        // The Worker's ISystemContext.For(owner) sets CurrentUserId before constructing this context.
        // Keep all tenant filters, plus an explicit owner equality to reject inconsistent source rows.
        var rows = await (
            from source in db.StorySources.AsNoTracking()
            join episode in db.Episodes.AsNoTracking() on source.EpisodeId equals episode.Id
            where episode.Id == episodeId && source.UserId == episode.UserId
                && source.Kind == StorySourceKind.Url && source.Status == StorySourceStatus.Fetched
                && source.ExtractedTextKey != null
            orderby source.CreatedAt, source.Id
            select new { source.Id, source.ExtractedTextKey })
            .Take(limits.MaxSourceTexts)
            .ToListAsync(ct).ConfigureAwait(false);

        var corpus = new List<CorpusText>(rows.Count);
        foreach (var row in rows)
        {
            var read = await textStore.ReadAsync(row.ExtractedTextKey!, ct).ConfigureAwait(false);
            if (read.IsError)
            {
                throw new SimilarityCorpusUnavailableException(SimilarityUnavailableReason.StorageError);
            }

            var text = read.Value;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (text.Length > limits.MaxSourceTextChars)
            {
                var length = limits.MaxSourceTextChars;
                if (char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
                {
                    length--;
                }

                text = text[..length];
            }

            corpus.Add(new CorpusText(row.Id, text));
        }

        return corpus;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CorpusText>> GetPastEpisodesAsync(
        Guid seriesId, Guid excludeEpisodeId, int max, CancellationToken ct)
    {
        var limit = Math.Clamp(max, 0, settings.Value.Corpus.MaxPastEpisodes);
        if (limit == 0)
        {
            return [];
        }

        return await (
            from episode in db.Episodes.AsNoTracking()
            join series in db.Series.AsNoTracking() on episode.SeriesId equals series.Id
            join version in db.StoryVersions.AsNoTracking() on episode.CurrentStoryVersionId equals (Guid?)version.Id
            where series.Id == seriesId && episode.Id != excludeEpisodeId
                && episode.UserId == series.UserId && version.UserId == episode.UserId
                && version.EpisodeId == episode.Id && version.Body != null
            orderby episode.CreatedAt descending, episode.Id descending
            select new CorpusText(episode.Id, version.Body))
            .Take(limit).ToListAsync(ct).ConfigureAwait(false);
    }
}
