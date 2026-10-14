using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Application.Features.SeriesBible;

/// <summary>
/// What the server owns inside one episode summary of a bible (02.6.3-T1, plan §3.1, §4 U2): the
/// structure tags of the episode's approved story version and the moment it was approved. The client owns
/// the title and the summary text only.
/// </summary>
/// <param name="Structure">The D3 structure tags of the approved story version.</param>
/// <param name="ApprovedAt">When the episode was approved.</param>
public sealed record BibleSummaryServerValues(StoryStructureTags Structure, DateTimeOffset ApprovedAt);

/// <summary>
/// Completes the episode summaries of a <c>PUT /api/v1/series/{id}/bible</c> body (02.6.3-T1, plan §4 U2):
/// the client's summaries carry the title and the text, and this is where the two server-derived members
/// are filled in — the stored values for an episode the bible already describes (a manual edit of a title
/// or a text never rewrites what the approval recorded), the approved story version's for an episode the
/// client newly adds. A summary naming an episode that is unknown or not approved keeps the placeholder
/// the DTO built, and the Domain validator is what refuses it, with the field path.
/// </summary>
/// <remarks>
/// The merge is order-preserving and additive in the client's direction: the returned list holds exactly
/// the requested summaries, in the requested order, so a removed summary stays removed (a wrong summary
/// must be fixable) and the field paths of a 400 keep naming the elements the client sent. It is public
/// because the unit tests of this feature exercise it and this solution has no
/// <c>InternalsVisibleTo</c>.
/// </remarks>
public static class BibleSummaryDerivation
{
    /// <summary>
    /// Merges the requested summaries with the stored ones and the derived server values.
    /// </summary>
    /// <param name="requested">The summaries of the request body, in body order.</param>
    /// <param name="stored">The summaries the bible currently stores, for the episodes it already describes.</param>
    /// <param name="derived">The server values of the newly added episodes, keyed by episode id.</param>
    /// <returns>The summaries to validate and store.</returns>
    public static IReadOnlyList<BibleEpisodeSummary> Merge(
        IReadOnlyList<BibleEpisodeSummary> requested,
        IReadOnlyList<BibleEpisodeSummary> stored,
        IReadOnlyDictionary<Guid, BibleSummaryServerValues> derived)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(derived);

        if (requested.Count == 0)
        {
            return [];
        }

        var storedByEpisode = new Dictionary<Guid, BibleEpisodeSummary>(stored.Count);
        foreach (var summary in stored)
        {
            storedByEpisode[summary.EpisodeId] = summary;
        }

        var merged = new List<BibleEpisodeSummary>(requested.Count);
        foreach (var summary in requested)
        {
            if (storedByEpisode.TryGetValue(summary.EpisodeId, out var existing))
            {
                // Known episode: the title and the text are the client's, the approval record is not.
                merged.Add(existing with { Title = summary.Title, Summary = summary.Summary });
                continue;
            }

            if (derived.TryGetValue(summary.EpisodeId, out var serverValues))
            {
                merged.Add(summary with
                {
                    Structure = serverValues.Structure,
                    ApprovedAt = serverValues.ApprovedAt,
                });
                continue;
            }

            // Unknown or unapproved episode: the placeholder stays, and the validator refuses it with
            // episode_summaries[i].episode_id — a body error the client can see and fix.
            merged.Add(summary);
        }

        return merged;
    }
}
