namespace NightTales.Domain.Stories.Similarity;

/// <summary>The share of a segment's distinct shingles found in the corpus.</summary>
public sealed record SegmentOverlap(string Segment, double Share);
