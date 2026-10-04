namespace NightTales.Domain.Stories.Review;

/// <summary>
/// The closed list of issue types a <c>review.v1</c> may report (02 README D21, 02.6.1-T1): thirteen
/// objective types and four taste types. The wire spelling is the snake_case of the member name; the
/// model never sets the issue's <c>kind</c>, it follows from the type (<see cref="ReviewIssueTypes"/>).
/// </summary>
public enum ReviewIssueType
{
    /// <summary>Objective: a <c>fact_refs</c> entry names a fact that is not in the sheet's id list.</summary>
    FactRefInvalid,

    /// <summary>Objective: a checkable claim that no supplied fact supports.</summary>
    FactNotInSheet,

    /// <summary>Objective: the twist arrives without a set-up.</summary>
    MissingSetupBeforeTwist,

    /// <summary>Objective: the tone breaks the channel's frame (explicit gore, sexual content, a minor in danger).</summary>
    ToneViolation,

    /// <summary>Objective: the story is outside the tier's token or segment band.</summary>
    LengthOutOfRange,

    /// <summary>Objective: the draft contradicts the series bible.</summary>
    BibleContradiction,

    /// <summary>Objective: a narration-for-the-ear rule (E1–E10) is broken.</summary>
    EarRuleViolation,

    /// <summary>Objective: a segment's image keywords break the keyword rules.</summary>
    KeywordRuleViolation,

    /// <summary>Objective: there is no usable hook.</summary>
    HookMissing,

    /// <summary>Objective: there is no usable ending.</summary>
    EndingMissing,

    /// <summary>Objective: a required beat is absent.</summary>
    BeatMissing,

    /// <summary>Objective: markdown, a list marker or a URL leaked into narration text.</summary>
    MarkdownOrUrlInText,

    /// <summary>Objective: a content warning the story needs is not declared.</summary>
    ContentWarningMissing,

    /// <summary>Taste: the hook is weak.</summary>
    WeakHook,

    /// <summary>Taste: the ending is flat.</summary>
    FlatEnding,

    /// <summary>Taste: the pacing is off.</summary>
    PacingIssue,

    /// <summary>Taste: phrasing repeats.</summary>
    RepetitivePhrasing,
}
