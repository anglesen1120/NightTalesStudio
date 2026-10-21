namespace NightTales.Domain.Stories.Gates;

/// <summary>
/// One verdict line of the story or the video gate (02.8.2 plan §3.4, the <c>checks[]</c> of
/// <c>gate_results</c>). Together with <see cref="StoryGateResult.Failures"/> it makes a verdict reproducible
/// from the stored run row alone, which is what T3's calibration reads without re-evaluating (plan §3.3).
/// </summary>
/// <param name="Id">One of <see cref="StoryGateCheckIds"/> or <see cref="VideoGateCheckIds"/>.</param>
/// <param name="Kind">One of <see cref="GateCheckKinds"/>.</param>
/// <param name="Value">
/// The measured value. A boolean answer is 1 (<c>true</c>) or 0 (<c>false</c>); it is <see langword="null"/>
/// when the field the check reads is absent, never zeroed.
/// </param>
/// <param name="Threshold">
/// The bound the verdict turned on: the bound itself for a one-sided check, for <see cref="GateCheckKinds.Range"/>
/// the bound the value was outside of (the band's upper bound when it was inside), and for
/// <see cref="GateCheckKinds.Flag"/> the value required to pass. It is <see langword="null"/> only for the
/// always-failing <c>true_crime_hold</c>, exactly as plan §3.4's example shows.
/// </param>
/// <param name="Pass">Whether the check passed.</param>
/// <param name="Detail">
/// A code from <see cref="GateDetailCodes"/>, a count or nothing at all. It never carries story text, a brief,
/// a fact, an evidence excerpt or a source URL (D19/D22).
/// </param>
public sealed record GateCheck(string Id, string Kind, decimal? Value, decimal? Threshold, bool Pass, string? Detail);

/// <summary>How a <see cref="GateCheck"/> compares its value with its threshold.</summary>
public static class GateCheckKinds
{
    /// <summary>Pass when <c>value ≥ threshold</c>: a floor, such as the mean review score.</summary>
    public const string Minimum = "min";

    /// <summary>Pass when <c>value ≤ threshold</c>: a ceiling, such as a count or a drift share.</summary>
    public const string Maximum = "max";

    /// <summary>
    /// Pass when the value is inside a two-sided band; <see cref="GateCheck.Threshold"/> is the deciding bound
    /// (the length band and the loudness band).
    /// </summary>
    public const string Range = "range";

    /// <summary>Pass when <c>value == threshold</c>; 1 is true and 0 is false.</summary>
    public const string Flag = "flag";
}

/// <summary>
/// The closed set of <see cref="GateCheck.Detail"/> codes: ids, counts and codes only (D19). The rules, the
/// tests, the run log and <c>audit_logs.data</c> share them instead of restating the strings.
/// </summary>
public static class GateDetailCodes
{
    /// <summary>A field the check needs is absent from the stored document: that check fails closed.</summary>
    public const string FieldAbsent = "field_absent";

    /// <summary>The audit report carries no readable <c>model_review</c> block.</summary>
    public const string ReviewAbsent = "review_absent";

    /// <summary>The audit report's <c>model_review.stale</c> flag is set, so the review is not current.</summary>
    public const string ReviewStale = "review_stale";

    /// <summary>The gate had no moderation result to read, so <c>moderation_gate</c> fails closed.</summary>
    public const string NotChecked = "not_checked";

    /// <summary>The moderator was unavailable after the D7 retry budget (plan §7); fail closed.</summary>
    public const string ModeratorUnavailable = "moderator_unavailable";

    /// <summary>The keyword total is above <c>ceil(D × K)</c> (P5/F09, K = 6 images per minute).</summary>
    public const string KeywordCapExceeded = "keyword_cap_exceeded";

    /// <summary>The stored asset's SHA-256 differs from the quality report's.</summary>
    public const string ShaMismatch = "sha_mismatch";

    /// <summary>The stored asset's duration is not above zero.</summary>
    public const string DurationNotPositive = "duration_not_positive";

    /// <summary>The measured frame size does not match the series aspect (C14).</summary>
    public const string ResolutionMismatch = "resolution_mismatch";

    /// <summary>The series is <c>true_crime</c>: the story is held for the owner by design (02.8.2 ¶2).</summary>
    public const string TrueCrime = "true_crime";

    /// <summary>The real-world detector reported a hit (plan §3.1 check 8).</summary>
    public const string RealWorldHit = "hit";

    /// <summary>
    /// Every code, in the order the gates can emit them: the run log, the copy review and the tests enumerate
    /// the closed set instead of restating the strings, and a new code has one place to land.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        FieldAbsent,
        ReviewAbsent,
        ReviewStale,
        NotChecked,
        ModeratorUnavailable,
        KeywordCapExceeded,
        ShaMismatch,
        DurationNotPositive,
        ResolutionMismatch,
        TrueCrime,
        RealWorldHit,
    ];
}
