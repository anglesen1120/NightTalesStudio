namespace NightTales.Domain.Stories.Keywords;

/// <summary>
/// The outcome of <see cref="KeywordRules.Check"/> for one keyword: it either passes, or the first 03
/// FR-03-3 rule it breaks. The validator turns any failure into the blocking <c>keyword_invalid</c> issue
/// (02 README D3); 03.2.2 reuses this type and its rule ids (CR-5).
/// </summary>
/// <param name="IsValid">True when no rule fired.</param>
/// <param name="RuleId">The 03 FR-03-3 rule that fired; empty when the keyword is valid.</param>
/// <param name="Evidence">The offending fragment, for the corrective note.</param>
public sealed record KeywordViolation(bool IsValid, string RuleId, string Evidence)
{
    /// <summary>The keyword passes every machine check.</summary>
    public static KeywordViolation Valid { get; } = new(true, string.Empty, string.Empty);

    /// <summary>The keyword breaks <paramref name="ruleId"/>.</summary>
    /// <param name="ruleId">The 03 FR-03-3 rule that fired.</param>
    /// <param name="evidence">The offending fragment.</param>
    /// <returns>The violation.</returns>
    public static KeywordViolation Invalid(string ruleId, string evidence) => new(false, ruleId, evidence);
}
