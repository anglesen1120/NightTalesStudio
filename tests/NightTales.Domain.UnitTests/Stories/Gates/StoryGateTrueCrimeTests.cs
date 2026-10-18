using NightTales.Domain.Series;
using NightTales.Domain.Stories.Gates;
using NightTales.Domain.Stories.Review;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Gates;

/// <summary>
/// The true-crime hold and the fail-closed guarantees of the story gate (02.8.2 plan §3.1 checks 14–15, §3.3
/// thresholds, §3.4 details): a true-crime story never passes, and every recorded verdict carries enough to
/// reproduce itself.
/// </summary>
public sealed class StoryGateTrueCrimeTests
{
    [Fact]
    public void StoryGate_TrueCrime_AlwaysHolds()
    {
        // Every other check is green: a current review of 5s, no issues, low similarity, an in-band length,
        // an approved sheet. The hold is the designed outcome, not a failed check.
        var version = GateTestData.Version(
            factSheetId: GateTestData.FactSheetId,
            approvedFactSheetId: GateTestData.FactSheetId);
        var audit = GateTestData.Audit(
            scores: new ReviewScores(5, 5, 5, 5, 5, FactualCare: 5, LengthFit: 5),
            revisionApplied: false);

        var result = StoryGateRules.Evaluate(
            GateTestData.StoryInput(version: version, audit: audit, series: new SeriesGateContext(Topic.TrueCrime)),
            AutopilotGateProfile.Defaults);

        result.Pass.ShouldBeFalse();
        result.Hold.ShouldBeTrue();
        result.Failures.ShouldBe([StoryGateCheckIds.TrueCrimeHold]);

        // Fifteen checks, and only the hold failed.
        result.Checks.Count.ShouldBe(15);
        result.Checks.Select(check => check.Id).ShouldBe(StoryGateCheckIds.All);
        result.Checks.Count(check => !check.Pass).ShouldBe(1);
        GateTestData.Find(result.Checks, StoryGateCheckIds.FactSheetCurrent).Pass.ShouldBeTrue();

        var hold = GateTestData.Find(result.Checks, StoryGateCheckIds.TrueCrimeHold);
        hold.Kind.ShouldBe(GateCheckKinds.Flag);
        hold.Value.ShouldBe((decimal?)1);
        hold.Threshold.ShouldBeNull("the hold is a decision, not a threshold");
        hold.Detail.ShouldBe(GateDetailCodes.TrueCrime);

        // factual_care is scored for true crime, so the mean covers seven dimensions.
        GateTestData.Find(result.Checks, StoryGateCheckIds.SelfReviewMeanScore).Detail
            .ShouldBe("mean of 7 non-null dimensions");
    }

    [Fact]
    public void StoryGate_TrueCrime_UnknownOrStaleFactSheet_Fails()
    {
        // Neither side known (no sheet): the check cannot pass on an unknown.
        var unknown = StoryGateRules.Evaluate(
            GateTestData.StoryInput(series: new SeriesGateContext(Topic.TrueCrime)),
            AutopilotGateProfile.Defaults);
        var unknownCheck = GateTestData.Find(unknown.Checks, StoryGateCheckIds.FactSheetCurrent);
        unknownCheck.Pass.ShouldBeFalse();
        unknownCheck.Detail.ShouldBe(GateDetailCodes.FieldAbsent);
        unknown.Checks.Count(check => !check.Pass).ShouldBe(2);
        unknown.Hold.ShouldBeTrue();
        unknown.Pass.ShouldBeFalse();

        // A sheet that is no longer the approved one: a content failure, not an unknown.
        var stale = StoryGateRules.Evaluate(
            GateTestData.StoryInput(
                version: GateTestData.Version(
                    factSheetId: GateTestData.FactSheetId,
                    approvedFactSheetId: Guid.NewGuid()),
                series: new SeriesGateContext(Topic.TrueCrime)),
            AutopilotGateProfile.Defaults);
        var staleCheck = GateTestData.Find(stale.Checks, StoryGateCheckIds.FactSheetCurrent);
        staleCheck.Pass.ShouldBeFalse();
        staleCheck.Value.ShouldBe((decimal?)0);
        staleCheck.Threshold.ShouldBe((decimal?)1);
        staleCheck.Detail.ShouldBeNull();
        stale.Failures.ShouldBe([StoryGateCheckIds.FactSheetCurrent, StoryGateCheckIds.TrueCrimeHold]);
        stale.Hold.ShouldBeTrue();
    }

    [Fact]
    public void StoryGate_NonTrueCrime_SkipsTheTrueCrimeChecks()
    {
        // The two true-crime checks do not appear at all for another topic, so no row can be misread as a
        // vacuous pass.
        var result = StoryGateRules.Evaluate(GateTestData.StoryInput(), AutopilotGateProfile.Defaults);

        result.Checks.ShouldNotContain(check => check.Id == StoryGateCheckIds.TrueCrimeHold);
        result.Checks.ShouldNotContain(check => check.Id == StoryGateCheckIds.FactSheetCurrent);
        result.Hold.ShouldBeFalse();
    }

    [Fact]
    public void StoryGate_EverythingAbsent_FailsEveryCheck()
    {
        var input = GateTestData.StoryInput(
            version: GateTestData.Version(
                wordCount: 0,
                keywords: [],
                contentWarnings: ["self_harm_mention"],
                keywordViolationCount: 1,
                keywordLimit: 0),
            audit: GateTestData.AuditAbsent(),
            similarity: new SimilaritySignals(Sources: null, PastEpisodes: null, CorpusMax: 0),
            series: new SeriesGateContext(Topic.TrueCrime),
            attempt: 2) with
        {
            Moderation = null,
            RealWorld = null,
        };

        var result = StoryGateRules.Evaluate(input, AutopilotGateProfile.Defaults);

        result.Pass.ShouldBeFalse();
        result.Hold.ShouldBeTrue();
        result.Attempt.ShouldBe(2);
        result.Checks.Count.ShouldBe(15);
        result.Failures.ShouldBe(StoryGateCheckIds.All);
        result.Checks.ShouldAllBe(check => !check.Pass);

        // Nothing here throws or passes on an unknown: each failing line carries a code from the closed set
        // when it carries anything at all (a count and a threshold are self-describing).
        result.Checks.ShouldAllBe(check => check.Detail == null || GateDetailCodes.All.Contains(check.Detail));
    }

    [Fact]
    public void StoryGate_EveryCheckRecordsItsThresholdAndCode()
    {
        // Plan §3.3: a stored verdict reproduces itself from the run row alone, so every line carries the
        // threshold it faced and a detail that is a code, never story text.
        var green = StoryGateRules.Evaluate(GateTestData.StoryInput(), AutopilotGateProfile.Defaults);
        var allAbsent = StoryGateRules.Evaluate(
            GateTestData.StoryInput(audit: GateTestData.AuditAbsent()) with { Moderation = null, RealWorld = null },
            AutopilotGateProfile.Defaults);

        foreach (var result in new[] { green, allAbsent })
        {
            result.Checks.ShouldAllBe(check => check.Threshold.HasValue);
            result.Checks.ShouldAllBe(check => check.Id.Length > 0);
            result.Checks.ShouldAllBe(check => check.Detail == null || GateDetailCodes.All.Contains(check.Detail)
                || check.Detail.StartsWith("mean of ", StringComparison.Ordinal));
            result.Checks.ShouldAllBe(check => check.Kind == GateCheckKinds.Minimum
                || check.Kind == GateCheckKinds.Maximum
                || check.Kind == GateCheckKinds.Range
                || check.Kind == GateCheckKinds.Flag);
        }

        green.Checks.ShouldAllBe(check => check.Pass);
        green.Checks.Count(check => check.Detail != null).ShouldBe(1, "only the mean carries its provenance");
    }
}
