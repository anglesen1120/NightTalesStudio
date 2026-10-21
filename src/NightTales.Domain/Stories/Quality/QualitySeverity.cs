namespace NightTales.Domain.Stories.Quality;

/// <summary>How a <see cref="QualityIssue"/> is presented (02.3.4-T1; D22 <c>advisory</c>/<c>blocking</c>). The checker itself never gates approve.</summary>
public enum QualitySeverity
{
    /// <summary>A hint the author may ignore.</summary>
    Advisory,

    /// <summary>A rule that is enforced elsewhere (validator, D12 gate, true-crime gate); the editor shows it as a warning.</summary>
    Blocking,
}
