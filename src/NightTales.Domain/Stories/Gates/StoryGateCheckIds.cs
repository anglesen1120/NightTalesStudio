namespace NightTales.Domain.Stories.Gates;

/// <summary>
/// The story gate's check ids (02.8.2 plan §3.1), in evaluation order. One source for <c>gate_results</c>,
/// <c>audit_logs.data</c>, the run-log copy keys (<c>autopilot.gate.failed.&lt;id&gt;</c>), the tests and the
/// story-gate evaluator, so the ids cannot drift apart.
/// </summary>
public static class StoryGateCheckIds
{
    /// <summary>1: the audit report carries a current (<c>stale = false</c>) <c>model_review</c>.</summary>
    public const string SelfReviewPresent = "self_review_present";

    /// <summary>2: the mean of the non-null D16 review dimensions.</summary>
    public const string SelfReviewMeanScore = "self_review_mean_score";

    /// <summary>3: the lowest of the non-null D16 review dimensions.</summary>
    public const string SelfReviewMinDimension = "self_review_min_dimension";

    /// <summary>4: the count of blocking objective issues in <c>local.issues ∪</c> the selected review.</summary>
    public const string BlockingObjectiveIssues = "blocking_objective_issues";

    /// <summary>5: how many revisions were applied (D21 allows one).</summary>
    public const string RevisionCount = "revision_count";

    /// <summary>6: the draft-time moderation verdict (<c>audit_report.moderation.flagged</c>).</summary>
    public const string ModerationDraft = "moderation_draft";

    /// <summary>7: the 02.8.2-T2 content gate over narration, metadata and scene prompts.</summary>
    public const string ModerationGate = "moderation_gate";

    /// <summary>8: the 02.8.2-T2 real-world signal (framing lexicon, dated incident, model claim).</summary>
    public const string RealWorldSignal = "real_world_signal";

    /// <summary>9: <c>story_versions.similarity_score</c> against the source corpus.</summary>
    public const string SimilaritySources = "similarity_sources";

    /// <summary>10: the similarity against the series' own past episodes.</summary>
    public const string SimilarityPastEpisodes = "similarity_past_episodes";

    /// <summary>11: <c>word_count</c> inside the D4 band of the tier × language cell (±15 % advisory).</summary>
    public const string LengthInRange = "length_in_range";

    /// <summary>12: the <c>KeywordRules</c> violations and the keyword total against <c>ceil(D × K)</c>.</summary>
    public const string KeywordRules = "keyword_rules";

    /// <summary>13: <c>content_warnings</c> are a subset of the profile's allow-list.</summary>
    public const string ContentWarningsAllowed = "content_warnings_allowed";

    /// <summary>14 (<c>true_crime</c> only): the version was written from the current approved fact sheet (F12).</summary>
    public const string FactSheetCurrent = "fact_sheet_current";

    /// <summary>15 (<c>true_crime</c> only, evaluated last): the hold. It always fails.</summary>
    public const string TrueCrimeHold = "true_crime_hold";

    /// <summary>The fifteen ids in plan §3.1 order; 14 and 15 are the <c>true_crime</c> ones.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        SelfReviewPresent,
        SelfReviewMeanScore,
        SelfReviewMinDimension,
        BlockingObjectiveIssues,
        RevisionCount,
        ModerationDraft,
        ModerationGate,
        RealWorldSignal,
        SimilaritySources,
        SimilarityPastEpisodes,
        LengthInRange,
        KeywordRules,
        ContentWarningsAllowed,
        FactSheetCurrent,
        TrueCrimeHold,
    ];
}
