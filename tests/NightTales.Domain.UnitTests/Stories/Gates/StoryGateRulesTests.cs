using NightTales.Domain.Stories.Gates;
using NightTales.Domain.Stories.Review;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Gates;

/// <summary>
/// The story gate (02.8.2 plan §3.1, task 02.8.2-T1): the pass path, each check's own failure, the true-crime
/// hold, the fail-closed field reads and the strict profile overlay.
/// </summary>
/// <remarks>
/// Pure unit tests: the input is assembled by <see cref="GateTestData"/> and every rule reads only that. The
/// caller-side selection of <c>revision.review_after</c> (02.6.2-T1) is exercised through the facts it maps,
/// because the rules never choose the review.
/// </remarks>
public sealed class StoryGateRulesTests
{
    [Fact]
    public void StoryGate_AllChecksPass_ReturnsPass()
    {
        var result = StoryGateRules.Evaluate(GateTestData.StoryInput(), AutopilotGateProfile.Defaults);

        result.Pass.ShouldBeTrue();
        result.Hold.ShouldBeFalse();
        result.Attempt.ShouldBe(1);
        result.StoryVersionId.ShouldBe(GateTestData.StoryVersionId);
        result.Failures.ShouldBeEmpty();

        // Thirteen checks for a non-true-crime story, in plan order, and no more.
        result.Checks.Count.ShouldBe(13);
        result.Checks.Select(check => check.Id).ShouldBe(StoryGateCheckIds.All.Take(13));
    }

    [Fact]
    public void StoryGate_CheckIds_AreThePlanOrder()
    {
        StoryGateCheckIds.All.ShouldBe(
        [
            "self_review_present",
            "self_review_mean_score",
            "self_review_min_dimension",
            "blocking_objective_issues",
            "revision_count",
            "moderation_draft",
            "moderation_gate",
            "real_world_signal",
            "similarity_sources",
            "similarity_past_episodes",
            "length_in_range",
            "keyword_rules",
            "content_warnings_allowed",
            "fact_sheet_current",
            "true_crime_hold",
        ]);
    }

    [Fact]
    public void StoryGate_StaleModelReview_Fails()
    {
        var audit = GateTestData.Audit(modelReviewStale: true);

        var result = StoryGateRules.Evaluate(GateTestData.StoryInput(audit: audit), AutopilotGateProfile.Defaults);

        result.Pass.ShouldBeFalse();
        result.Hold.ShouldBeFalse();
        result.Failures.ShouldBe([StoryGateCheckIds.SelfReviewPresent]);

        var check = GateTestData.Find(result.Checks, StoryGateCheckIds.SelfReviewPresent);
        check.Pass.ShouldBeFalse();
        check.Value.ShouldBe((decimal?)0);
        check.Threshold.ShouldBe((decimal?)1);
        check.Detail.ShouldBe(GateDetailCodes.ReviewStale);
    }

    [Fact]
    public void StoryGate_AbsentModelReview_Fails()
    {
        var audit = GateTestData.Audit() with { ModelReviewPresent = false, Scores = null };

        var result = StoryGateRules.Evaluate(GateTestData.StoryInput(audit: audit), AutopilotGateProfile.Defaults);

        var check = GateTestData.Find(result.Checks, StoryGateCheckIds.SelfReviewPresent);
        check.Pass.ShouldBeFalse();
        check.Detail.ShouldBe(GateDetailCodes.ReviewAbsent);

        // The dimensions are unreadable too, so checks 2 and 3 fail closed rather than pass by default.
        GateTestData.Find(result.Checks, StoryGateCheckIds.SelfReviewMeanScore).Detail
            .ShouldBe(GateDetailCodes.FieldAbsent);
        GateTestData.Find(result.Checks, StoryGateCheckIds.SelfReviewMinDimension).Detail
            .ShouldBe(GateDetailCodes.FieldAbsent);
    }

    [Fact]
    public void StoryGate_MeanScoreBelowThreshold_Fails()
    {
        // Six non-null dimensions, mean 3.00: below the 3.5 floor, but exactly at the 3.0 dimension floor.
        var audit = GateTestData.Audit(scores: new ReviewScores(3, 3, 3, 3, 3, FactualCare: null, LengthFit: 3));

        var result = StoryGateRules.Evaluate(GateTestData.StoryInput(audit: audit), AutopilotGateProfile.Defaults);

        result.Pass.ShouldBeFalse();
        result.Failures.ShouldBe([StoryGateCheckIds.SelfReviewMeanScore]);

        var mean = GateTestData.Find(result.Checks, StoryGateCheckIds.SelfReviewMeanScore);
        mean.Pass.ShouldBeFalse();
        mean.Value.ShouldBe((decimal?)3.0m);
        mean.Threshold.ShouldBe((decimal?)3.5m);
        mean.Detail.ShouldBe("mean of 6 non-null dimensions");

        // The boundary is inclusive: the lowest dimension sitting exactly on its floor is a pass.
        var lowest = GateTestData.Find(result.Checks, StoryGateCheckIds.SelfReviewMinDimension);
        lowest.Pass.ShouldBeTrue();
        lowest.Value.ShouldBe((decimal?)3.0m);
        lowest.Threshold.ShouldBe((decimal?)3.0m);
    }

    [Fact]
    public void StoryGate_ReviewWithNoScoredDimension_FailsClosed()
    {
        var input = GateTestData.StoryInput(audit: GateTestData.Audit() with { Scores = null });

        var result = StoryGateRules.Evaluate(input, AutopilotGateProfile.Defaults);

        result.Failures.ShouldBe([StoryGateCheckIds.SelfReviewMeanScore, StoryGateCheckIds.SelfReviewMinDimension]);
        GateTestData.Find(result.Checks, StoryGateCheckIds.SelfReviewMeanScore).Value.ShouldBeNull();
        GateTestData.Find(result.Checks, StoryGateCheckIds.SelfReviewMinDimension).Value.ShouldBeNull();
    }

    [Fact]
    public void StoryGate_OneBlockingObjectiveIssue_Fails()
    {
        // One blocking objective issue, one advisory taste issue and one advisory objective issue: only the
        // first counts, whichever list it came from.
        var audit = GateTestData.Audit(issues:
        [
            new GateIssueFacts(Severity: "blocking", Kind: "objective"),
            new GateIssueFacts(Severity: "advisory", Kind: "taste"),
            new GateIssueFacts(Severity: "advisory", Kind: "objective"),
        ]);

        var result = StoryGateRules.Evaluate(GateTestData.StoryInput(audit: audit), AutopilotGateProfile.Defaults);

        result.Pass.ShouldBeFalse();
        result.Failures.ShouldBe([StoryGateCheckIds.BlockingObjectiveIssues]);

        var check = GateTestData.Find(result.Checks, StoryGateCheckIds.BlockingObjectiveIssues);
        check.Value.ShouldBe((decimal?)1);
        check.Threshold.ShouldBe((decimal?)0);
        check.Pass.ShouldBeFalse();
    }

    [Fact]
    public void StoryGate_AdvisoryTasteIssue_Passes()
    {
        // F07/D4: a taste disagreement is the owner's call, never the gate's.
        var audit = GateTestData.Audit(issues:
        [
            new GateIssueFacts(Severity: "advisory", Kind: "taste"),
            new GateIssueFacts(Severity: "blocking", Kind: "taste"),
        ]);

        var result = StoryGateRules.Evaluate(GateTestData.StoryInput(audit: audit), AutopilotGateProfile.Defaults);

        result.Pass.ShouldBeTrue();
        GateTestData.Find(result.Checks, StoryGateCheckIds.BlockingObjectiveIssues).Value.ShouldBe((decimal?)0);
    }

    [Fact]
    public void StoryGate_MalformedIssueKind_CountsAsObjective()
    {
        // A severity/kind pair outside the vocabulary must not open the gate.
        var audit = GateTestData.Audit(issues: [new GateIssueFacts(Severity: "blocking", Kind: "unexpected")]);

        var result = StoryGateRules.Evaluate(GateTestData.StoryInput(audit: audit), AutopilotGateProfile.Defaults);

        result.Failures.ShouldBe([StoryGateCheckIds.BlockingObjectiveIssues]);
        StoryGateRules.IsBlockingObjective(new GateIssueFacts("blocking", "objective")).ShouldBeTrue();
        StoryGateRules.IsBlockingObjective(new GateIssueFacts("blocking", "taste")).ShouldBeFalse();
        StoryGateRules.IsBlockingObjective(new GateIssueFacts("advisory", "objective")).ShouldBeFalse();
    }

    [Fact]
    public void StoryGate_UnknownSeverity_CountsAsBlocking()
    {
        // F5: only the two severities the audit writes are read. A differently cased, absent or unknown severity
        // is an issue the gate cannot judge, so it counts — and no kind exempts it, because only the documented
        // `blocking`/`taste` pair is a taste disagreement.
        StoryGateRules.IsBlockingObjective(new GateIssueFacts("Blocking", "objective")).ShouldBeTrue();
        StoryGateRules.IsBlockingObjective(new GateIssueFacts("blocker", "taste")).ShouldBeTrue();
        StoryGateRules.IsBlockingObjective(new GateIssueFacts("advisory", "objective")).ShouldBeFalse();

        var audit = GateTestData.Audit(issues:
        [
            new GateIssueFacts(Severity: string.Empty, Kind: string.Empty),
            new GateIssueFacts(Severity: "advisory", Kind: "taste"),
        ]);

        var result = StoryGateRules.Evaluate(GateTestData.StoryInput(audit: audit), AutopilotGateProfile.Defaults);

        result.Failures.ShouldBe([StoryGateCheckIds.BlockingObjectiveIssues]);
        GateTestData.Find(result.Checks, StoryGateCheckIds.BlockingObjectiveIssues).Value.ShouldBe((decimal?)1);
    }

    [Fact]
    public void StoryGate_UnknownKeywordViolationCount_FailsClosed()
    {
        // F4: the caller could not read the stored local block, so the violation count is unknown and the check
        // fails with field_absent instead of reading "no stored violation" as zero.
        var result = StoryGateRules.Evaluate(
            GateTestData.StoryInput(version: GateTestData.Version(keywordViolationCount: null)),
            AutopilotGateProfile.Defaults);

        var check = GateTestData.Find(result.Checks, StoryGateCheckIds.KeywordRules);
        check.Pass.ShouldBeFalse();
        check.Value.ShouldBeNull();
        check.Threshold.ShouldBe((decimal?)0);
        check.Detail.ShouldBe(GateDetailCodes.FieldAbsent);
        result.Failures.ShouldBe([StoryGateCheckIds.KeywordRules]);
    }

    [Fact]
    public void StoryGate_RevisionCountAboveMax_Fails()
    {
        var audit = GateTestData.Audit(revisionApplied: true);

        // D21 allows one revision, so the default profile accepts it.
        var allowed = StoryGateRules.Evaluate(GateTestData.StoryInput(audit: audit), AutopilotGateProfile.Defaults);
        allowed.Pass.ShouldBeTrue();
        GateTestData.Find(allowed.Checks, StoryGateCheckIds.RevisionCount).Value.ShouldBe((decimal?)1);

        // A profile that allows none refuses the same story, and the check records that threshold.
        var strict = AutopilotGateProfile.Defaults.Overlay(
            new AutopilotGateProfilePatch(Story: new StoryGateThresholdsPatch(MaxRevisions: 0)));
        var refused = StoryGateRules.Evaluate(GateTestData.StoryInput(audit: audit), strict);

        refused.Pass.ShouldBeFalse();
        refused.Failures.ShouldBe([StoryGateCheckIds.RevisionCount]);
        GateTestData.Find(refused.Checks, StoryGateCheckIds.RevisionCount).Threshold.ShouldBe((decimal?)0);
    }

    [Fact]
    public void StoryGate_RevisionBlockAbsent_FailsClosed()
    {
        var input = GateTestData.StoryInput(audit: GateTestData.Audit() with { RevisionApplied = null });

        var result = StoryGateRules.Evaluate(input, AutopilotGateProfile.Defaults);

        var check = GateTestData.Find(result.Checks, StoryGateCheckIds.RevisionCount);
        check.Pass.ShouldBeFalse();
        check.Value.ShouldBeNull();
        check.Detail.ShouldBe(GateDetailCodes.FieldAbsent);
    }

    [Fact]
    public void StoryGate_DraftModerationFlagged_Fails()
    {
        var flagged = StoryGateRules.Evaluate(
            GateTestData.StoryInput(audit: GateTestData.Audit(moderationFlagged: true)),
            AutopilotGateProfile.Defaults);

        flagged.Failures.ShouldBe([StoryGateCheckIds.ModerationDraft]);
        GateTestData.Find(flagged.Checks, StoryGateCheckIds.ModerationDraft).Value.ShouldBe((decimal?)1);

        var absent = StoryGateRules.Evaluate(
            GateTestData.StoryInput(audit: GateTestData.Audit() with { ModerationFlagged = null }),
            AutopilotGateProfile.Defaults);

        var check = GateTestData.Find(absent.Checks, StoryGateCheckIds.ModerationDraft);
        check.Pass.ShouldBeFalse();
        check.Value.ShouldBeNull();
        check.Detail.ShouldBe(GateDetailCodes.FieldAbsent);
    }

    [Fact]
    public void StoryGate_ModerationGate_NotCheckedFlaggedOrUnavailable_FailsClosed()
    {
        var profile = AutopilotGateProfile.Defaults;

        // No result at all (02.8.2-T2 absent, or the call was never made).
        var none = StoryGateRules.Evaluate(
            GateTestData.StoryInput() with { Moderation = null },
            profile);
        none.Failures.ShouldBe([StoryGateCheckIds.ModerationGate]);
        var notChecked = GateTestData.Find(none.Checks, StoryGateCheckIds.ModerationGate);
        notChecked.Value.ShouldBeNull();
        notChecked.Detail.ShouldBe(GateDetailCodes.NotChecked);

        // A flagged category.
        var flagged = StoryGateRules.Evaluate(
            GateTestData.StoryInput(moderation: new ModerationGateOutcome(Flagged: true, UnavailableDetail: null)),
            profile);
        var flaggedCheck = GateTestData.Find(flagged.Checks, StoryGateCheckIds.ModerationGate);
        flaggedCheck.Pass.ShouldBeFalse();
        flaggedCheck.Value.ShouldBe((decimal?)1);

        // The moderator failed after its retry budget: fail closed and say so.
        var unavailable = StoryGateRules.Evaluate(
            GateTestData.StoryInput(moderation: new ModerationGateOutcome(
                Flagged: true,
                UnavailableDetail: GateDetailCodes.ModeratorUnavailable)),
            profile);
        var unavailableCheck = GateTestData.Find(unavailable.Checks, StoryGateCheckIds.ModerationGate);
        unavailableCheck.Pass.ShouldBeFalse();
        unavailableCheck.Detail.ShouldBe(GateDetailCodes.ModeratorUnavailable);
    }

    [Fact]
    public void StoryGate_RealWorldSignal_FailsClosed()
    {
        var profile = AutopilotGateProfile.Defaults;

        var hit = new RealWorldSignal(
            Hit: true,
            Hits: [new RealWorldSignalHit("p:1", RealWorldSignalKind.ModelClaim, "based on real events", "evidence")]);
        var withHit = StoryGateRules.Evaluate(GateTestData.StoryInput(realWorld: hit), profile);

        withHit.Failures.ShouldBe([StoryGateCheckIds.RealWorldSignal]);
        var hitCheck = GateTestData.Find(withHit.Checks, StoryGateCheckIds.RealWorldSignal);
        hitCheck.Value.ShouldBe((decimal?)1);
        hitCheck.Detail.ShouldBe(GateDetailCodes.RealWorldHit);

        // An unreadable claims list is not "no claims".
        var unreadable = StoryGateRules.Evaluate(
            GateTestData.StoryInput() with { RealWorld = null },
            profile);
        var missing = GateTestData.Find(unreadable.Checks, StoryGateCheckIds.RealWorldSignal);
        missing.Pass.ShouldBeFalse();
        missing.Value.ShouldBeNull();
        missing.Detail.ShouldBe(GateDetailCodes.FieldAbsent);
    }

    [Fact]
    public void StoryGate_SimilarityMissing_FailsClosed()
    {
        var input = GateTestData.StoryInput(similarity: new SimilaritySignals(Sources: null, PastEpisodes: null, CorpusMax: 0));

        var result = StoryGateRules.Evaluate(input, AutopilotGateProfile.Defaults);

        result.Failures.ShouldBe([StoryGateCheckIds.SimilaritySources, StoryGateCheckIds.SimilarityPastEpisodes]);
        var sources = GateTestData.Find(result.Checks, StoryGateCheckIds.SimilaritySources);
        sources.Value.ShouldBeNull();
        sources.Detail.ShouldBe(GateDetailCodes.FieldAbsent);
        sources.Threshold.ShouldBe((decimal?)0.05m);
        GateTestData.Find(result.Checks, StoryGateCheckIds.SimilarityPastEpisodes).Threshold
            .ShouldBe((decimal?)0.10m);
    }

    [Fact]
    public void StoryGate_LengthAdvisoryBand_BoundsAreInclusive()
    {
        // The 1600–1800 band with the 15 % advisory tolerance: 1360 .. 2070 (F07/D4, the same rule as Q-LEN-02).
        var band = new LengthBand(Min: 1600, Max: 1800);
        var profile = AutopilotGateProfile.Defaults;

        foreach (var inside in new[] { 1360, 1700, 2070 })
        {
            var pass = StoryGateRules.Evaluate(
                GateTestData.StoryInput(version: GateTestData.Version(wordCount: inside), length: band),
                profile);
            pass.Pass.ShouldBeTrue($"a length of {inside} is inside the advisory band");
        }

        foreach (var (outside, bound) in new[] { (1359, 1360m), (2071, 2070m) })
        {
            var fail = StoryGateRules.Evaluate(
                GateTestData.StoryInput(version: GateTestData.Version(wordCount: outside), length: band),
                profile);
            fail.Failures.ShouldBe([StoryGateCheckIds.LengthInRange]);

            var check = GateTestData.Find(fail.Checks, StoryGateCheckIds.LengthInRange);
            check.Value.ShouldBe((decimal?)outside);
            check.Threshold.ShouldBe((decimal?)bound, "the recorded threshold is the bound the word count missed");
        }
    }

    [Fact]
    public void StoryGate_KeywordViolationAndCap_Fail()
    {
        var profile = AutopilotGateProfile.Defaults;

        var violation = StoryGateRules.Evaluate(
            GateTestData.StoryInput(version: GateTestData.Version(keywordViolationCount: 1)),
            profile);
        var violationCheck = GateTestData.Find(violation.Checks, StoryGateCheckIds.KeywordRules);
        violationCheck.Pass.ShouldBeFalse();
        violationCheck.Value.ShouldBe((decimal?)1);
        violationCheck.Threshold.ShouldBe((decimal?)0);
        violationCheck.Detail.ShouldBeNull("a rule violation is reported by its count");

        // Two keywords against a cap of one: the count is fine, the cap is not, and the code says which.
        var overCap = StoryGateRules.Evaluate(
            GateTestData.StoryInput(version: GateTestData.Version(
                keywords: ["abandoned house at dusk", "quiet lane at night"],
                keywordLimit: 1)),
            profile);
        var capCheck = GateTestData.Find(overCap.Checks, StoryGateCheckIds.KeywordRules);
        capCheck.Pass.ShouldBeFalse();
        capCheck.Value.ShouldBe((decimal?)0);
        capCheck.Detail.ShouldBe(GateDetailCodes.KeywordCapExceeded);
    }

    [Fact]
    public void StoryGate_ContentWarningOutsideAllowList_Fails()
    {
        var result = StoryGateRules.Evaluate(
            GateTestData.StoryInput(version: GateTestData.Version(contentWarnings: ["self_harm_mention"])),
            AutopilotGateProfile.Defaults);

        result.Failures.ShouldBe([StoryGateCheckIds.ContentWarningsAllowed]);
        GateTestData.Find(result.Checks, StoryGateCheckIds.ContentWarningsAllowed).Value.ShouldBe((decimal?)1);
    }

    [Fact]
    public void StoryGate_ContentWarnings_AreCaseInsensitive()
    {
        var result = StoryGateRules.Evaluate(
            GateTestData.StoryInput(version: GateTestData.Version(contentWarnings: ["Violence", "DEATH"])),
            AutopilotGateProfile.Defaults);

        result.Pass.ShouldBeTrue();
        GateTestData.Find(result.Checks, StoryGateCheckIds.ContentWarningsAllowed).Value.ShouldBe((decimal?)0);
    }

    [Fact]
    public void StoryGate_RevisionApplied_ReadsTheSelectedReviewFacts()
    {
        // 02.6.2-T1: once a revision is applied the current state is review #2, so the caller maps
        // revision.review_after into these facts. Clean review-after facts pass while review #1's blocking
        // objective issue would not, and the applied revision is still counted.
        var reviewAfter = GateTestData.Audit(
            scores: new ReviewScores(5, 4, 5, 4, 5, FactualCare: null, LengthFit: 4),
            issues: [new GateIssueFacts(Severity: "advisory", Kind: "taste")],
            revisionApplied: true);
        var reviewOne = reviewAfter with { Issues = [new GateIssueFacts(Severity: "blocking", Kind: "objective")] };
        var profile = AutopilotGateProfile.Defaults;

        var current = StoryGateRules.Evaluate(GateTestData.StoryInput(audit: reviewAfter), profile);
        current.Pass.ShouldBeTrue();
        GateTestData.Find(current.Checks, StoryGateCheckIds.RevisionCount).Value.ShouldBe((decimal?)1);

        var stale = StoryGateRules.Evaluate(GateTestData.StoryInput(audit: reviewOne), profile);
        stale.Failures.ShouldBe([StoryGateCheckIds.BlockingObjectiveIssues]);
    }

    [Fact]
    public void StoryGate_ProfileOverlay_StrictRaisesThresholds()
    {
        // The plan §3.3 strict profile: 4.0 / 3.5 / 0.03 / 0.05, everything else inheriting the defaults.
        var strict = AutopilotGateProfile.Resolve(new AutopilotGateProfilePatch(
            Story: new StoryGateThresholdsPatch(
                MinMeanReviewScore: 4.0m,
                MinDimensionScore: 3.5m,
                MaxSourceSimilarity: 0.03m,
                MaxPastEpisodeSimilarity: 0.05m)));

        // Mean 3.67 with a 3 minimum and two scores between the two profiles.
        var input = GateTestData.StoryInput(
            audit: GateTestData.Audit(scores: new ReviewScores(4, 3, 4, 3, 4, FactualCare: null, LengthFit: 4)),
            similarity: new SimilaritySignals(Sources: 0.04m, PastEpisodes: 0.06m, CorpusMax: 50));

        var relaxed = StoryGateRules.Evaluate(input, AutopilotGateProfile.Defaults);
        relaxed.Pass.ShouldBeTrue();

        var raised = StoryGateRules.Evaluate(input, strict);
        raised.Failures.ShouldBe(
        [
            StoryGateCheckIds.SelfReviewMeanScore,
            StoryGateCheckIds.SelfReviewMinDimension,
            StoryGateCheckIds.SimilaritySources,
            StoryGateCheckIds.SimilarityPastEpisodes,
        ]);
        GateTestData.Find(raised.Checks, StoryGateCheckIds.SelfReviewMeanScore).Threshold.ShouldBe((decimal?)4.0m);
        GateTestData.Find(raised.Checks, StoryGateCheckIds.SimilaritySources).Threshold.ShouldBe((decimal?)0.03m);
        GateTestData.Find(raised.Checks, StoryGateCheckIds.SimilarityPastEpisodes).Threshold.ShouldBe((decimal?)0.05m);

        // The keys the strict patch does not name inherit: a revision is still allowed.
        GateTestData.Find(raised.Checks, StoryGateCheckIds.RevisionCount).Threshold.ShouldBe((decimal?)1);
    }

    [Fact]
    public void StoryGate_NullInputOrProfile_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => StoryGateRules.Evaluate(null!, AutopilotGateProfile.Defaults));
        Should.Throw<ArgumentNullException>(
            () => StoryGateRules.Evaluate(GateTestData.StoryInput(), null!));
    }
}
