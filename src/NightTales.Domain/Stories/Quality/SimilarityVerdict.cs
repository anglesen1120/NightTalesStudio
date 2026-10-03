namespace NightTales.Domain.Stories.Quality;

/// <summary>
/// The verdict of the one similarity boundary (02.5.6-T1 <c>SimilarityApprovalGuard.Classify</c>: <c>Pass</c>,
/// <c>Warn</c>, <c>Block</c>). The checker never classifies a score itself: the caller passes the verdict the
/// approve gate acts on, so a hint and a 409 always explain the same number (02.3.4-T1 Q-SIM-01/Q-SIM-02).
/// A null score is <see cref="Pass"/>.
/// </summary>
public enum SimilarityVerdict
{
    /// <summary>Below the warn boundary, or unscored.</summary>
    Pass,

    /// <summary>At or above the warn boundary.</summary>
    Warn,

    /// <summary>At or above the block boundary.</summary>
    Block,
}
