using System.Text.Json.Nodes;

namespace NightTales.Domain.Stories.Similarity;

/// <summary>A story-level score and its highest overlapping segment addresses, without prose.</summary>
public sealed record SimilarityResult(double Score, int N, string Corpus, IReadOnlyList<SegmentOverlap> Segments)
{
    /// <summary>Projects precisely D22's ordered audit shape, rounded to four decimal places.</summary>
    public object ToAuditSection()
    {
        var segments = new JsonArray();
        foreach (var segment in Segments)
        {
            segments.Add(new JsonObject
            {
                ["segment"] = segment.Segment,
                ["share"] = Math.Round(segment.Share, 4, MidpointRounding.AwayFromZero),
            });
        }

        return new JsonObject
        {
            ["score"] = Math.Round(Score, 4, MidpointRounding.AwayFromZero),
            ["n"] = N,
            ["corpus"] = Corpus,
            ["segments"] = segments,
        };
    }
}
