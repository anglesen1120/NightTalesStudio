namespace NightTales.Domain.Stories.Quality;

/// <summary>The derived values the checker measured; they equal <c>DerivedStoryFields</c> where both exist (02.3.4-T1).</summary>
/// <param name="Tokens">The D4 token count of the body (hook, paragraphs, ending).</param>
/// <param name="Segments">Hook + paragraphs + ending.</param>
/// <param name="Keywords">The total number of keywords over all segments.</param>
/// <param name="Paragraphs">The number of body paragraphs.</param>
/// <param name="SimilarityScore">The context's similarity score, passed through; null when unscored.</param>
/// <param name="InternalShingleShare">The share of 8-token shingles that repeat inside the draft; null when the draft has no shingle.</param>
public sealed record QualityCounters(
    int Tokens,
    int Segments,
    int Keywords,
    int Paragraphs,
    double? SimilarityScore,
    double? InternalShingleShare);
