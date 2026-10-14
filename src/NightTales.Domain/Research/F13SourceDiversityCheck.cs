using ErrorOr;

namespace NightTales.Domain.Research;

/// <summary>
/// The eligibility facts the F13 diversity gate needs of one source the sheet cites (02.5.4-T1, README F13).
/// </summary>
/// <param name="SourceId">The <c>story_sources.id</c> the fact cites.</param>
/// <param name="Kind">Whether the source is a fetched page or the user's own note; only a page can support a fact.</param>
/// <param name="Status">Where the source is in its life; only <see cref="StorySourceStatus.Fetched"/> counts.</param>
/// <param name="ContentSha256">The hash of the source's stored text; null when the source carries none.</param>
public sealed record F13SourceInfo(
    Guid SourceId,
    StorySourceKind Kind,
    StorySourceStatus Status,
    string? ContentSha256);

/// <summary>
/// The source-diversity gate of approving a fact sheet (README F13, 02.5.4-T1): a version is approvable only
/// when it carries at least <see cref="MinFacts"/> facts and the facts it keeps cite at least two distinct
/// documents — fetched url sources whose stored text differs, because two pages with identical text are one
/// source for this rule. Pure: the facts and the metadata of the episode's sources come in as arguments, and
/// nothing is read or written.
/// </summary>
public static class F13SourceDiversityCheck
{
    /// <summary>The fewest facts an approvable version carries (02.5.4-T1, S31 hand-over).</summary>
    public const int MinFacts = 5;

    /// <summary>The fewest distinct documents the cited sources must resolve to (README F13).</summary>
    public const int MinDistinctSources = 2;

    /// <summary>
    /// The catalogued code of a version with too few facts (409, 02 README D8). The Domain may not reference
    /// <c>NightTales.Application.Common.Errors.ErrorCodes</c>; the unit test
    /// <c>Check_Codes_MatchTheErrorCatalogue</c> keeps the two equal.
    /// </summary>
    public const string SourcesInsufficientCode = "fact_sheet_sources_insufficient";

    /// <summary>
    /// The catalogued code of a version whose cited sources are not two distinct documents (409, 02 README D8),
    /// the same code a true-crime story approve answers with when its approved sheet is too thin.
    /// </summary>
    public const string StorySourcesRequiredCode = "story_sources_required";

    /// <summary>
    /// Checks the F13 gate: the fact count first (an empty or thin sheet can never be approved), then the
    /// diversity of the cited sources.
    /// </summary>
    /// <param name="facts">The facts of the version as they would be stored.</param>
    /// <param name="sources">
    /// The episode's <c>story_sources</c> by id. A cited source that is missing from the map counts as
    /// ineligible, exactly like a deleted one.
    /// </param>
    /// <returns>
    /// <c>Success</c> when the version carries <see cref="MinFacts"/> facts and cites at least
    /// <see cref="MinDistinctSources"/> distinct eligible documents; otherwise the 409 of the rule that failed.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static ErrorOr<Success> Check(
        IReadOnlyList<FactItem> facts,
        IReadOnlyDictionary<Guid, F13SourceInfo> sources)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(sources);

        if (facts.Count < MinFacts)
        {
            return Error.Conflict(
                SourcesInsufficientCode,
                $"A fact sheet needs at least {MinFacts} facts before it can be approved.");
        }

        var contentHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            ArgumentNullException.ThrowIfNull(fact);
            if (fact.SourceIds is null)
            {
                continue;
            }

            // One fact counts a source once, however many times it cites it.
            var citedByThisFact = new HashSet<Guid>();
            foreach (var sourceId in fact.SourceIds)
            {
                if (!citedByThisFact.Add(sourceId))
                {
                    continue;
                }

                if (!sources.TryGetValue(sourceId, out var source)
                    || source is null
                    || source.Kind != StorySourceKind.Url
                    || source.Status != StorySourceStatus.Fetched
                    || source.ContentSha256 is null)
                {
                    continue;
                }

                contentHashes.Add(source.ContentSha256);
            }
        }

        if (contentHashes.Count < MinDistinctSources)
        {
            return Error.Conflict(
                StorySourcesRequiredCode,
                $"An approvable fact sheet must cite at least {MinDistinctSources} distinct fetched sources.");
        }

        return Result.Success;
    }
}
