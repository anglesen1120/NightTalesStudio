using NightTales.Domain.Series;

namespace NightTales.Domain.Stories.Gates;

/// <summary>
/// The story half of the auto-approval gate (02.8.2 plan §3.1): the fifteen checks over data the caller has
/// already read, evaluated in plan order, with no I/O, no clock and no randomness.
/// </summary>
/// <remarks>
/// <para>
/// Checks 1–13 run for every topic; checks 14 and 15 run for <c>true_crime</c> only, and check 15 is evaluated
/// last and always fails, so a true-crime story returns <c>Pass = false</c> with <c>Hold = true</c> — the only
/// case that sets the hold (plan §3.1, §3.4).
/// </para>
/// <para>
/// It fails closed: every absent or unreadable field fails its own check with a detail code, and nothing here
/// throws on data. Every check records the threshold it faced, so a stored verdict reproduces itself from the
/// run row alone (plan §3.3). The caller resolves the profile first
/// (<see cref="AutopilotGateProfile.Resolve"/>); a <see cref="StoryGateThresholds"/> member the caller left
/// unset is unaffected because a resolved profile carries every member.
/// </para>
/// </remarks>
public static class StoryGateRules
{
    private const int ScoreDecimals = 2;

    /// <summary>Evaluates the fifteen story checks over one assembled input.</summary>
    /// <param name="input">The stored facts; see <see cref="StoryGateInput"/>.</param>
    /// <param name="profile">The resolved thresholds, normally <see cref="AutopilotGateProfile.Resolve"/>'s result.</param>
    /// <returns>The verdict, in plan §3.1 order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="profile"/> is null.</exception>
    public static StoryGateResult Evaluate(StoryGateInput input, AutopilotGateProfile profile)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(profile);

        var thresholds = profile.Story;
        var version = input.Version;
        var audit = input.Audit;
        var checks = new List<GateCheck>(StoryGateCheckIds.All.Count);

        // 1 — a present, current self review. A missing block and a set stale flag both fail, so an edited
        // story (whose review is not the current state) can never open the gate.
        var reviewCurrent = audit.ModelReviewPresent && !audit.ModelReviewStale;
        checks.Add(Flag(
            StoryGateCheckIds.SelfReviewPresent,
            reviewCurrent,
            required: true,
            reviewCurrent
                ? null
                : audit.ModelReviewPresent ? GateDetailCodes.ReviewStale : GateDetailCodes.ReviewAbsent));

        // 2/3 — the D16 dimensions, over the non-null ones only (factual_care is null outside true crime).
        var dimensions = audit.Scores?.Scored().Select(dimension => (decimal)dimension.Value).ToList();
        if (dimensions is null || dimensions.Count == 0)
        {
            checks.Add(Missing(StoryGateCheckIds.SelfReviewMeanScore, GateCheckKinds.Minimum, thresholds.MinMeanReviewScore));
            checks.Add(Missing(StoryGateCheckIds.SelfReviewMinDimension, GateCheckKinds.Minimum, thresholds.MinDimensionScore));
        }
        else
        {
            // The mean is rounded before the comparison, like every stored value, so the recorded row always
            // reproduces the verdict; the detail sentence is D16 provenance for T3's calibration.
            var mean = Math.Round(dimensions.Sum() / dimensions.Count, ScoreDecimals, MidpointRounding.AwayFromZero);
            checks.Add(new GateCheck(
                StoryGateCheckIds.SelfReviewMeanScore,
                GateCheckKinds.Minimum,
                mean,
                thresholds.MinMeanReviewScore,
                mean >= thresholds.MinMeanReviewScore,
                FormattableString.Invariant($"mean of {dimensions.Count} non-null dimensions")));

            var lowest = dimensions.Min();
            checks.Add(new GateCheck(
                StoryGateCheckIds.SelfReviewMinDimension,
                GateCheckKinds.Minimum,
                lowest,
                thresholds.MinDimensionScore,
                lowest >= thresholds.MinDimensionScore,
                null));
        }

        // 4 — blocking objective issues across local.issues and the selected review (02.6.2-T1: review #2 when
        // a revision was applied, review #1 otherwise; the caller selects, the rule counts).
        if (audit.Issues is not { } issues)
        {
            checks.Add(Missing(StoryGateCheckIds.BlockingObjectiveIssues, GateCheckKinds.Maximum, 0));
        }
        else
        {
            var blocking = issues.Count(IsBlockingObjective);
            checks.Add(new GateCheck(
                StoryGateCheckIds.BlockingObjectiveIssues,
                GateCheckKinds.Maximum,
                blocking,
                0,
                blocking == 0,
                null));
        }

        // 5 — at most MaxRevisions revisions (D21 allows one).
        var revisions = audit.RevisionApplied is { } applied ? (applied ? 1 : 0) : (int?)null;
        checks.Add(new GateCheck(
            StoryGateCheckIds.RevisionCount,
            GateCheckKinds.Maximum,
            revisions,
            thresholds.MaxRevisions,
            revisions is { } count && count <= thresholds.MaxRevisions,
            revisions is null ? GateDetailCodes.FieldAbsent : null));

        // 6 — the draft-time moderation verdict must not be flagged.
        var draftFlagged = audit.ModerationFlagged;
        checks.Add(new GateCheck(
            StoryGateCheckIds.ModerationDraft,
            GateCheckKinds.Flag,
            draftFlagged is { } flagged ? (flagged ? 1 : 0) : null,
            0,
            draftFlagged == false,
            draftFlagged is null ? GateDetailCodes.FieldAbsent : null));

        // 7 — the 02.8.2-T2 content gate. No result at all, a flag and an unavailable moderator all fail closed.
        checks.Add(input.Moderation switch
        {
            null => new GateCheck(StoryGateCheckIds.ModerationGate, GateCheckKinds.Flag, null, 0, false, GateDetailCodes.NotChecked),
            { Flagged: true } flaggedOutcome => new GateCheck(StoryGateCheckIds.ModerationGate, GateCheckKinds.Flag, 1, 0, false, flaggedOutcome.UnavailableDetail),
            { UnavailableDetail: { } unavailable } => new GateCheck(StoryGateCheckIds.ModerationGate, GateCheckKinds.Flag, 0, 0, false, unavailable),
            _ => new GateCheck(StoryGateCheckIds.ModerationGate, GateCheckKinds.Flag, 0, 0, true, null),
        });

        // 8 — the real-world signal. T1 reads persisted model claims only, so no hit means no hit; a signal that
        // could not be read is null and fails closed.
        checks.Add(input.RealWorld switch
        {
            null => new GateCheck(StoryGateCheckIds.RealWorldSignal, GateCheckKinds.Flag, null, 0, false, GateDetailCodes.FieldAbsent),
            { Hit: true } => new GateCheck(StoryGateCheckIds.RealWorldSignal, GateCheckKinds.Flag, 1, 0, false, GateDetailCodes.RealWorldHit),
            _ => new GateCheck(StoryGateCheckIds.RealWorldSignal, GateCheckKinds.Flag, 0, 0, true, null),
        });

        // 9/10 — similarity. A score that was never computed is not a pass.
        checks.Add(Maximum(StoryGateCheckIds.SimilaritySources, input.Similarity.Sources, thresholds.MaxSourceSimilarity));
        checks.Add(Maximum(
            StoryGateCheckIds.SimilarityPastEpisodes,
            input.Similarity.PastEpisodes,
            thresholds.MaxPastEpisodeSimilarity));

        // 11 — the D4 band; the ±15 % advisory band counts as a pass (F07/D4), which is the same rule as
        // StoryQualityChecker's Q-LEN-02. The recorded threshold is the deciding bound.
        var band = input.Length;
        var low = band.Min * (1m - band.AdvisoryTolerance);
        var high = band.Max * (1m + band.AdvisoryTolerance);
        var wordCount = version.WordCount;
        checks.Add(new GateCheck(
            StoryGateCheckIds.LengthInRange,
            GateCheckKinds.Range,
            wordCount,
            wordCount < band.Min ? low : high,
            wordCount >= low && wordCount <= high,
            null));

        // 12 — the keyword rules and the ceil(D × K) cap (P5/F09). The value is the violation count; a total
        // above the cap is reported by its code.
        var overCap = version.Keywords.Count > version.KeywordLimit;
        checks.Add(new GateCheck(
            StoryGateCheckIds.KeywordRules,
            GateCheckKinds.Maximum,
            version.KeywordViolationCount,
            0,
            version.KeywordViolationCount == 0 && !overCap,
            overCap ? GateDetailCodes.KeywordCapExceeded : null));

        // 13 — content_warnings must be a subset of the profile's allow-list (case-insensitive, so a
        // capitalised warning is the same code).
        var allowed = thresholds.AllowedContentWarnings;
        var disallowed = version.ContentWarnings.Count(warning => !allowed.Contains(warning, StringComparer.OrdinalIgnoreCase));
        checks.Add(new GateCheck(
            StoryGateCheckIds.ContentWarningsAllowed,
            GateCheckKinds.Maximum,
            disallowed,
            0,
            disallowed == 0,
            null));

        // 14/15 — true crime only. Check 15 runs last and always fails: the hold is the designed outcome, and the
        // run log still shows the full picture for the owner (plan §3.1).
        var trueCrime = input.Series.Topic == Topic.TrueCrime;
        if (trueCrime)
        {
            var sheetKnown = version.FactSheetId.HasValue && version.ApprovedFactSheetId.HasValue;
            checks.Add(Flag(
                StoryGateCheckIds.FactSheetCurrent,
                sheetKnown && version.FactSheetId == version.ApprovedFactSheetId,
                required: true,
                sheetKnown ? null : GateDetailCodes.FieldAbsent));

            checks.Add(new GateCheck(StoryGateCheckIds.TrueCrimeHold, GateCheckKinds.Flag, 1, null, false, GateDetailCodes.TrueCrime));
        }

        var failures = checks.Where(check => !check.Pass).Select(check => check.Id).ToList();

        return new StoryGateResult(
            Pass: failures.Count == 0,
            Hold: trueCrime,
            Attempt: input.Attempt,
            StoryVersionId: version.Id,
            Checks: checks,
            Failures: failures);
    }

    /// <summary>
    /// A blocking issue, per plan §3.1 check 4: severity <c>blocking</c> and a kind that is not clearly
    /// <c>taste</c>. A malformed kind therefore counts and cannot open the gate.
    /// </summary>
    /// <param name="issue">The issue as the audit stored it.</param>
    /// <returns>True when the issue counts against the gate.</returns>
    public static bool IsBlockingObjective(GateIssueFacts issue) =>
        string.Equals(issue.Severity, "blocking", StringComparison.Ordinal)
        && !string.Equals(issue.Kind, "taste", StringComparison.Ordinal);

    private static GateCheck Flag(string id, bool value, bool required, string? detail) =>
        new(id, GateCheckKinds.Flag, value ? 1 : 0, required ? 1 : 0, value == required, detail);

    private static GateCheck Maximum(string id, decimal? value, decimal threshold) =>
        new(
            id,
            GateCheckKinds.Maximum,
            value,
            threshold,
            value is { } measured && measured <= threshold,
            value is null ? GateDetailCodes.FieldAbsent : null);

    private static GateCheck Missing(string id, string kind, decimal threshold) =>
        new(id, kind, null, threshold, false, GateDetailCodes.FieldAbsent);
}
