using NightTales.Domain.Stories.Quality;

namespace NightTales.Domain.Stories.Similarity;

/// <summary>Classifies stored scores with strict thresholds; absent scores do not block approval.</summary>
public static class SimilarityApprovalGuard
{
    /// <summary>Returns block above BlockAbove, warn above WarnAbove, otherwise pass.</summary>
    public static SimilarityVerdict Classify(double? similarityScore, SimilarityOptions opt) =>
        similarityScore > opt.BlockAbove ? SimilarityVerdict.Block
        : similarityScore > opt.WarnAbove ? SimilarityVerdict.Warn : SimilarityVerdict.Pass;
}
