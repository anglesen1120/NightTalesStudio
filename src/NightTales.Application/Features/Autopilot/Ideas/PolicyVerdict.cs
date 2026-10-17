namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// One candidate's content-policy verdict (02.8.1-T2, task Design "Content policy"): the rejection the policy
/// pre-check found, the research flag it raised and the banned themes it matched.
/// </summary>
/// <remarks>
/// <para>
/// The policy pre-check is a <b>gate, not a score</b>: a banned-theme hit or a moderator flag rejects the
/// candidate before anything is generated from it, and a candidate it cleared may still carry
/// <see cref="RequiresResearch"/>, which is a signal for the story stage and the owner's fact-sheet approval,
/// never a rejection here (F33, plan §4 U2).
/// </para>
/// <para>
/// The moderator is asked about the candidate's <c>logline</c> and <c>hook</c> together, as the two texts of
/// one brief-epoch check, so the verdict is one for both.
/// </para>
/// </remarks>
/// <param name="RejectReason">The rejection reason code (an <c>IdeaRejectionReason</c> value) when the policy refused the candidate, or <see langword="null"/>.</param>
/// <param name="RequiresResearch">Whether the candidate reads as real-world framing and needs the owner's fact sheet before it can be written (true crime only).</param>
/// <param name="MatchedBannedThemes">The banned themes the candidate's text matched, in their configured form; empty when it matched none.</param>
public sealed record PolicyVerdict(
    string? RejectReason,
    bool RequiresResearch,
    IReadOnlyList<string> MatchedBannedThemes)
{
    /// <summary>A candidate the policy cleared, matching no banned theme and needing no research.</summary>
    public static PolicyVerdict Clear { get; } = new(null, false, []);
}

/// <summary>
/// One batch's content-policy result (02.8.1-T2, task Design "Content policy"): a verdict for <b>every</b>
/// candidate of the batch.
/// </summary>
/// <remarks>
/// The pre-check carries no error path: a policy refusal is a per-candidate rejection, and an unavailable
/// moderator is either a fail-closed rejection or a cleared batch, decided inside the checker against
/// <c>Autopilot:Ideas:Policy:FailClosed</c> (the shipped default clears and counts). The counter
/// <c>moderation_unavailable_total</c> is what makes the event visible either way, because the configured flag
/// alone cannot say what happened.
/// </remarks>
/// <param name="ByIdeaId">The verdicts keyed by the batch's idea ids.</param>
public sealed record PolicyBatchResult(IReadOnlyDictionary<Guid, PolicyVerdict> ByIdeaId);

/// <summary>
/// The series-level policy inputs one batch is checked against (02.8.1-T2, task Design "Content policy"):
/// the topic the framing check reads and the ban lists the banned-theme check matches.
/// </summary>
/// <remarks>
/// Built by the caller from the series' settings and the endpoint's topic filter, so the checker reads no
/// database and its tests are pure. <see cref="BannedThemes"/> holds the configured forms as they arrive (the
/// match is whole-word, case- and diacritic-insensitive and reports the matched original form) and both lists
/// are owner text, never logged (D19).
/// </remarks>
/// <param name="Topic">The series' narrative topic, snake_case; <c>true_crime</c> is what arms the framing check.</param>
/// <param name="BannedThemes">The theme forms the candidate text must not contain; empty when none are configured.</param>
public sealed record IdeaPolicyContext(string Topic, IReadOnlyList<string> BannedThemes)
{
    /// <summary>The topic of a series whose ideas are real-world angles and need research (F31/F33), snake_case.</summary>
    public const string TrueCrimeTopic = "true_crime";
}
