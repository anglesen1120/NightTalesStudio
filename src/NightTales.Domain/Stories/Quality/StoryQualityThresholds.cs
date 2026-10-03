namespace NightTales.Domain.Stories.Quality;

/// <summary>
/// The config-driven thresholds of <see cref="StoryQualityChecker"/> (02.3.4-T1 rule table, <c>Story:Quality</c>).
/// A plain record so the Domain stays free of options types; the Application options map onto it.
/// </summary>
/// <param name="LengthTolerance">Q-LEN-01/02: the share outside the D4 token band that turns advisory into blocking.</param>
/// <param name="MinHookTokens">Q-HOOK-01: the fewest tokens a hook may have.</param>
/// <param name="MaxHookSentences">Q-HOOK-02: the most sentences a hook may have.</param>
/// <param name="MinKeywordsPerSegment">Q-KW-01: the fewest keywords per segment.</param>
/// <param name="MaxKeywordsPerSegment">Q-KW-01: the most keywords per segment.</param>
/// <param name="KeywordsPerMinute">Q-KW-02: <c>K</c>; the keyword total may not exceed <c>ceil(D × K)</c> (F09).</param>
/// <param name="RepeatedSentenceMinTokens">Q-REP-01: the fewest tokens a sentence needs to count as a repeat.</param>
/// <param name="MaxInternalShingleShare">Q-REP-02: the largest share of repeated 8-token shingles.</param>
/// <param name="NameHeuristic">Q-NAME-01: whether the true-crime person-name heuristic runs.</param>
public sealed record StoryQualityThresholds(
    double LengthTolerance = 0.15,
    int MinHookTokens = 3,
    int MaxHookSentences = 3,
    int MinKeywordsPerSegment = 1,
    int MaxKeywordsPerSegment = 2,
    int KeywordsPerMinute = 6,
    int RepeatedSentenceMinTokens = 12,
    double MaxInternalShingleShare = 0.25,
    bool NameHeuristic = true)
{
    /// <summary>The rule-table defaults.</summary>
    public static StoryQualityThresholds Defaults { get; } = new();
}
