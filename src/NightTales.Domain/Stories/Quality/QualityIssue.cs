namespace NightTales.Domain.Stories.Quality;

/// <summary>
/// One quality finding of <see cref="StoryQualityChecker"/> (02.3.4-T1, D21/D22). It carries ids and codes only:
/// the localised copy lives in the UI's <c>quality</c> i18n files under <see cref="MessageKey"/>.
/// </summary>
/// <param name="RuleId">The stable rule id (<c>Q-LEN-01</c> …).</param>
/// <param name="Severity">Advisory or blocking (blocking only mirrors a rule enforced elsewhere).</param>
/// <param name="Kind">Objective or taste.</param>
/// <param name="Segment">The D22 address: <c>title</c>, <c>hook</c>, <c>p:&lt;i&gt;</c>, <c>ending</c> or <c>meta</c>.</param>
/// <param name="MessageKey">The i18n key of the message.</param>
/// <param name="Evidence">At most 200 characters of our own draft or a measured value; never source text (D22).</param>
/// <param name="RepairDirection">A short imperative, or null.</param>
/// <param name="Measured">The measured value, when the rule is numeric.</param>
/// <param name="Threshold">The threshold the value was compared with, when the rule is numeric.</param>
public sealed record QualityIssue(
    string RuleId,
    QualitySeverity Severity,
    QualityKind Kind,
    string Segment,
    string MessageKey,
    string Evidence,
    string? RepairDirection,
    decimal? Measured,
    decimal? Threshold);
