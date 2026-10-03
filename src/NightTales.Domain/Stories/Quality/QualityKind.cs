namespace NightTales.Domain.Stories.Quality;

/// <summary>Whether an issue is a machine-checkable fact or a matter of taste (02 README D22, <c>kind</c>).</summary>
public enum QualityKind
{
    /// <summary>Reproducible from the draft and its context alone.</summary>
    Objective,

    /// <summary>A heuristic or a judgement call.</summary>
    Taste,
}
