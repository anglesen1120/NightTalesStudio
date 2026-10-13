using NightTales.Domain.Research;

namespace NightTales.Domain.Stories.Quality;

/// <summary>
/// F13 (02 README): a true-crime story needs at least two distinct fetched url sources that are each cited
/// by a kept fact of the approved fact sheet. Pure and free of EF or framework types, so the rule is
/// unit-testable on its own; the caller (the approval gate) compares the returned count with
/// <c>Story:TrueCrime:MinCitedSources</c>.
/// </summary>
public static class TrueCrimeSourceRule
{
    /// <summary>One of the episode's sources, reduced to the fields the rule reads.</summary>
    /// <param name="Id">The <c>story_sources.id</c> a fact alias resolves to.</param>
    /// <param name="Kind">The source kind; only <see cref="StorySourceKind.Url"/> can qualify.</param>
    /// <param name="Status">The fetch status; only <see cref="StorySourceStatus.Fetched"/> qualifies.</param>
    /// <param name="ContentSha256">
    /// The hash of the fetched text; a null hash never qualifies, and two sources with equal hashes count
    /// once, so mirrored copies of one article cannot satisfy the rule twice (F13).
    /// </param>
    public sealed record Candidate(
        Guid Id,
        StorySourceKind Kind,
        StorySourceStatus Status,
        string? ContentSha256);

    /// <summary>
    /// Counts the qualifying sources of one episode that carry a distinct content hash (F13): a source
    /// qualifies when it is a fetched url source, its hash is present, and at least one kept fact cites it.
    /// </summary>
    /// <param name="sources">The episode's url sources.</param>
    /// <param name="citedSourceIds">
    /// The source ids cited by the approved sheet's kept facts, with their aliases already resolved.
    /// Dropped facts never reach the approved sheet, so they contribute nothing here.
    /// </param>
    /// <returns>The number of distinct qualifying content hashes.</returns>
    public static int QualifyingDistinctHashCount(
        IReadOnlyList<Candidate> sources,
        IReadOnlyCollection<Guid> citedSourceIds)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(citedSourceIds);

        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source.Kind == StorySourceKind.Url
                && source.Status == StorySourceStatus.Fetched
                && !string.IsNullOrEmpty(source.ContentSha256)
                && citedSourceIds.Contains(source.Id))
            {
                hashes.Add(source.ContentSha256);
            }
        }

        return hashes.Count;
    }
}
