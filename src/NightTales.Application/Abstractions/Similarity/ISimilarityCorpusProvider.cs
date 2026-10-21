using NightTales.Domain.Common;
using NightTales.Domain.Stories.Similarity;

namespace NightTales.Application.Abstractions.Similarity;

/// <summary>Loads the bounded, owner-isolated corpora used by the lexical similarity scorer.</summary>
public interface ISimilarityCorpusProvider
{
    /// <summary>Loads fetched URL source text; language is reserved by the scoring contract.</summary>
    ValueTask<IReadOnlyList<CorpusText>> LoadAsync(Guid episodeId, Language lang, CancellationToken ct);

    /// <summary>Loads current story bodies of other episodes in the same series.</summary>
    ValueTask<IReadOnlyList<CorpusText>> GetPastEpisodesAsync(Guid seriesId, Guid excludeEpisodeId, int max, CancellationToken ct);
}
