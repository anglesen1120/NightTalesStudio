namespace NightTales.Domain.Autopilot.Ideas;

/// <summary>
/// Why an idea did not become the pick (02.8.1 plan §3.1, <c>episode_ideas.rejected_reason</c>): the one
/// source of truth for the stored values of 02.8.1-T2 and for the copy keys the run log (10.4.3) maps.
/// </summary>
/// <remarks>
/// The column is <c>varchar(40)</c>, so <see cref="MaxLength"/> is the bound every constant must satisfy —
/// kept here rather than in the EF configuration so the table mapping can reference one number.
/// </remarks>
public static class IdeaRejectionReason
{
    /// <summary>The length of <c>rejected_reason</c>: every constant below is at most this long.</summary>
    public const int MaxLength = 40;

    /// <summary>The candidate lost the ranking to the winner without breaking a rule.</summary>
    public const string NotBest = "not_best";

    /// <summary>Its lexical similarity to a corpus entry is above <c>Autopilot:Ideas:Novelty:LexicalMax</c> (02.8.1-T2).</summary>
    public const string NoveltyLexical = "novelty_lexical";

    /// <summary>The novelty judge scored it above <c>Autopilot:Ideas:Novelty:JudgeMax</c> (02.8.1-T2).</summary>
    public const string NoveltyJudge = "novelty_judge";

    /// <summary>Its text contains a banned theme of the series (02.8.1-T2, the policy backstop).</summary>
    public const string PolicyBannedTheme = "policy_banned_theme";

    /// <summary>The content moderator flagged its logline or hook (02.8.1-T2).</summary>
    public const string PolicyModerated = "policy_moderated";

    /// <summary>The variety guard blocked it as a triple repeat (02.8.4-T1).</summary>
    public const string VarietyTripleRepeat = "variety_triple_repeat";

    /// <summary>The variety guard blocked it as a template repeat of a recent episode (02.8.4-T1).</summary>
    public const string VarietyTemplateRepeat = "variety_template_repeat";

    /// <summary>The variety guard hit the tag-share cap; a penalty, never a block, so the candidate is still scored (02.8.4-T1).</summary>
    public const string VarietyTagCap = "variety_tag_cap";

    /// <summary>It failed <see cref="IdeaValidator"/> and was dropped before scoring.</summary>
    public const string Invalid = "invalid";

    /// <summary>Its normalised title repeats an earlier candidate of the same batch.</summary>
    public const string Duplicate = "duplicate";
}
