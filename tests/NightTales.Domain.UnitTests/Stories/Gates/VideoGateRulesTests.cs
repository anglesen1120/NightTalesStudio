using NightTales.Domain.Series;
using NightTales.Domain.Stories.Gates;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Gates;

/// <summary>
/// The video gate (02.8.2 plan §3.2, task 02.8.2-T1): the pass path, each check's own failure and the
/// fail-closed read of the nullable <c>render-quality.v1</c> report (CR-6).
/// </summary>
/// <remarks>
/// Pure unit tests over <see cref="VideoGateRules"/>: the report, the asset row and the series aspect are
/// assembled by <see cref="GateTestData"/>, so a failing test proves the one field it changed.
/// </remarks>
public sealed class VideoGateRulesTests
{
    [Fact]
    public void VideoGate_AllChecksPass_ReturnsPass()
    {
        var result = VideoGateRules.Evaluate(GateTestData.VideoInput(), AutopilotGateProfile.Defaults);

        result.Pass.ShouldBeTrue();
        result.Failures.ShouldBeEmpty();
        result.RenderJobId.ShouldBe(GateTestData.RenderJobId);
        result.AssetId.ShouldBe(GateTestData.AssetId);
        result.Checks.Count.ShouldBe(10);
        result.Checks.Select(check => check.Id).ShouldBe(VideoGateCheckIds.All);
        result.Checks.ShouldAllBe(check => check.Pass);
    }

    [Fact]
    public void VideoGate_CheckIds_AreThePlanOrder()
    {
        VideoGateCheckIds.All.ShouldBe(
        [
            "quality_report_present",
            "quality_report_pass",
            "duration",
            "loudness",
            "resolution_aspect",
            "subtitles_present",
            "failed_scenes",
            "image_moderation",
            "asset_integrity",
            "subtitle_sync",
        ]);
    }

    [Fact]
    public void VideoGate_QualityReportAbsent_FailsClosed()
    {
        var result = VideoGateRules.Evaluate(GateTestData.AbsentReportInput(), AutopilotGateProfile.Defaults);

        result.Pass.ShouldBeFalse();

        // The report is the only source of measurement, so every check that reads one fails, is empty and says
        // why. Two lines are not measured checks: the present-flag records its own boolean (0), and check 8
        // reads the render job's moderation-event count from the database rather than the report.
        var fromReport = VideoGateCheckIds.All.Where(id => id != VideoGateCheckIds.ImageModeration).ToList();
        result.Checks.Count.ShouldBe(10);
        result.Failures.ShouldBe(fromReport);

        var measured = result.Checks
            .Where(check => check.Id != VideoGateCheckIds.QualityReportPresent
                && check.Id != VideoGateCheckIds.ImageModeration)
            .ToList();
        measured.Count.ShouldBe(8);
        measured.ShouldAllBe(check => !check.Pass);
        measured.ShouldAllBe(check => check.Value == null);
        measured.ShouldAllBe(check => check.Detail == GateDetailCodes.FieldAbsent);

        var present = GateTestData.Find(result.Checks, VideoGateCheckIds.QualityReportPresent);
        present.Pass.ShouldBeFalse();
        present.Value.ShouldBe((decimal?)0);
        present.Threshold.ShouldBe((decimal?)1);
        present.Detail.ShouldBe(GateDetailCodes.FieldAbsent);

        GateTestData.Find(result.Checks, VideoGateCheckIds.ImageModeration).Pass.ShouldBeTrue();

        // ...and that count still fails the gate when the row has an event.
        var withEvent = VideoGateRules.Evaluate(
            GateTestData.AbsentReportInput() with { ImageModerationEvents = 1 },
            AutopilotGateProfile.Defaults);
        var eventCheck = GateTestData.Find(withEvent.Checks, VideoGateCheckIds.ImageModeration);
        eventCheck.Pass.ShouldBeFalse();
        eventCheck.Value.ShouldBe((decimal?)1);

        // Every line still records the threshold, so the row reproduces the verdict.
        result.Checks.ShouldAllBe(check => check.Threshold.HasValue);
    }

    [Fact]
    public void VideoGate_SkippedKeyword_Fails()
    {
        var profile = AutopilotGateProfile.Defaults;

        // 03 D7/CR-3: a keyword the linter dropped shows up as images.skipped, and a moderated-away image as
        // images.moderated_count. Both are a failed scene.
        var skipped = VideoGateRules.Evaluate(
            GateTestData.VideoInput(report: GateTestData.Report(skippedSceneCount: 1)),
            profile);
        var skippedCheck = GateTestData.Find(skipped.Checks, VideoGateCheckIds.FailedScenes);
        skippedCheck.Pass.ShouldBeFalse();
        skippedCheck.Value.ShouldBe((decimal?)1);
        skippedCheck.Threshold.ShouldBe((decimal?)0);
        skipped.Failures.ShouldBe([VideoGateCheckIds.FailedScenes]);

        var moderated = VideoGateRules.Evaluate(
            GateTestData.VideoInput(report: GateTestData.Report(skippedSceneCount: 1, moderatedImageCount: 2)),
            profile);
        GateTestData.Find(moderated.Checks, VideoGateCheckIds.FailedScenes).Value.ShouldBe((decimal?)3);

        // An images block the report did not carry is not zero failed scenes.
        var absent = VideoGateRules.Evaluate(
            GateTestData.VideoInput(report: GateTestData.Report(moderatedImageCount: null)),
            profile);
        var absentCheck = GateTestData.Find(absent.Checks, VideoGateCheckIds.FailedScenes);
        absentCheck.Pass.ShouldBeFalse();
        absentCheck.Value.ShouldBeNull();
        absentCheck.Detail.ShouldBe(GateDetailCodes.FieldAbsent);
    }

    [Fact]
    public void VideoGate_LoudnessOutsideRange_Fails()
    {
        var profile = AutopilotGateProfile.Defaults;

        var tooLoud = VideoGateRules.Evaluate(
            GateTestData.VideoInput(report: GateTestData.Report(integratedLufs: -13)),
            profile);
        var loud = GateTestData.Find(tooLoud.Checks, VideoGateCheckIds.Loudness);
        loud.Pass.ShouldBeFalse();
        loud.Value.ShouldBe((decimal?)(-13));
        loud.Threshold.ShouldBe((decimal?)(-14), "the recorded threshold is the end the value crossed");
        tooLoud.Failures.ShouldBe([VideoGateCheckIds.Loudness]);

        var tooQuiet = VideoGateRules.Evaluate(
            GateTestData.VideoInput(report: GateTestData.Report(integratedLufs: -17)),
            profile);
        var quiet = GateTestData.Find(tooQuiet.Checks, VideoGateCheckIds.Loudness);
        quiet.Pass.ShouldBeFalse();
        quiet.Threshold.ShouldBe((decimal?)(-16));

        // Both ends of the band are inclusive passes.
        foreach (var atBound in new[] { -16.0, -15.0, -14.0 })
        {
            var pass = VideoGateRules.Evaluate(
                GateTestData.VideoInput(report: GateTestData.Report(integratedLufs: atBound)),
                profile);
            GateTestData.Find(pass.Checks, VideoGateCheckIds.Loudness).Pass.ShouldBeTrue();
            pass.Pass.ShouldBeTrue();
        }

        // An unmeasured loudness fails closed.
        var absent = VideoGateRules.Evaluate(
            GateTestData.VideoInput(report: GateTestData.Report(integratedLufs: null)),
            profile);
        var absentCheck = GateTestData.Find(absent.Checks, VideoGateCheckIds.Loudness);
        absentCheck.Pass.ShouldBeFalse();
        absentCheck.Detail.ShouldBe(GateDetailCodes.FieldAbsent);
    }

    [Fact]
    public void VideoGate_DurationDrift_AboveToleranceFails()
    {
        var profile = AutopilotGateProfile.Defaults;

        // 600 s of narration, 630 s of container: 0.05 exactly, so the boundary is a pass.
        var atTolerance = VideoGateRules.Evaluate(
            GateTestData.VideoInput(report: GateTestData.Report(durationSec: 630)),
            profile);
        var exact = GateTestData.Find(atTolerance.Checks, VideoGateCheckIds.Duration);
        exact.Pass.ShouldBeTrue();
        exact.Value.ShouldBe((decimal?)0.05m);
        exact.Threshold.ShouldBe((decimal?)0.05m);
        atTolerance.Pass.ShouldBeTrue();

        // One second more: 31/600 = 0.05166…, rounded to four places for storage.
        var beyond = VideoGateRules.Evaluate(
            GateTestData.VideoInput(report: GateTestData.Report(durationSec: 631)),
            profile);
        var drift = GateTestData.Find(beyond.Checks, VideoGateCheckIds.Duration);
        drift.Pass.ShouldBeFalse();
        drift.Value.ShouldBe((decimal?)0.0517m);
        drift.Threshold.ShouldBe((decimal?)0.05m);

        // A narration duration of zero cannot be judged, so it fails like an absent field.
        var noNarration = VideoGateRules.Evaluate(
            GateTestData.VideoInput(report: GateTestData.Report(ttsDurationSec: 0)),
            profile);
        var missing = GateTestData.Find(noNarration.Checks, VideoGateCheckIds.Duration);
        missing.Pass.ShouldBeFalse();
        missing.Value.ShouldBeNull();
        missing.Detail.ShouldBe(GateDetailCodes.FieldAbsent);
    }

    [Fact]
    public void VideoGate_ResolutionAspect_MustMatchTheSeries()
    {
        var profile = AutopilotGateProfile.Defaults;

        var portraitInLandscape = VideoGateRules.Evaluate(
            GateTestData.VideoInput(report: GateTestData.Report(width: 1080, height: 1920)),
            profile);
        var check = GateTestData.Find(portraitInLandscape.Checks, VideoGateCheckIds.ResolutionAspect);
        check.Pass.ShouldBeFalse();
        check.Value.ShouldBe((decimal?)0);
        check.Threshold.ShouldBe((decimal?)1);
        check.Detail.ShouldBe(GateDetailCodes.ResolutionMismatch);

        // The same frame is right for a portrait series.
        var portrait = VideoGateRules.Evaluate(
            GateTestData.VideoInput(aspect: Aspect.Portrait, report: GateTestData.Report(width: 1080, height: 1920)),
            profile);
        portrait.Pass.ShouldBeTrue();

        // A report that measured only one dimension fails closed.
        var halfMeasured = VideoGateRules.Evaluate(
            GateTestData.VideoInput(report: GateTestData.Report(height: null)),
            profile);
        var missing = GateTestData.Find(halfMeasured.Checks, VideoGateCheckIds.ResolutionAspect);
        missing.Pass.ShouldBeFalse();
        missing.Value.ShouldBeNull();
        missing.Detail.ShouldBe(GateDetailCodes.FieldAbsent);
    }

    [Fact]
    public void VideoGate_SubtitlesEnabledButAbsent_Fails()
    {
        var profile = AutopilotGateProfile.Defaults;

        var absent = VideoGateRules.Evaluate(
            GateTestData.VideoInput(report: GateTestData.Report(subtitlesPresent: false)),
            profile);
        var check = GateTestData.Find(absent.Checks, VideoGateCheckIds.SubtitlesPresent);
        check.Pass.ShouldBeFalse();
        check.Value.ShouldBe((decimal?)0);
        check.Threshold.ShouldBe((decimal?)1);
        absent.Failures.ShouldBe([VideoGateCheckIds.SubtitlesPresent]);

        // Not requested: both subtitle checks are recorded as passes with no measured value, so the list keeps
        // its ten entries.
        var disabled = VideoGateRules.Evaluate(
            GateTestData.VideoInput(subtitlesEnabled: false, report: GateTestData.Report(subtitlesPresent: false)),
            profile);
        disabled.Pass.ShouldBeTrue();
        disabled.Checks.Count.ShouldBe(10);
        GateTestData.Find(disabled.Checks, VideoGateCheckIds.SubtitlesPresent).Value.ShouldBeNull();
        GateTestData.Find(disabled.Checks, VideoGateCheckIds.SubtitleSync).Pass.ShouldBeTrue();
    }

    [Fact]
    public void VideoGate_SubtitleSyncHint_FailsWhenSubtitlesAreOn()
    {
        var profile = AutopilotGateProfile.Defaults;
        var report = GateTestData.Report(hints: [VideoGateRules.SubtitleSyncSuspectHint]);

        var on = VideoGateRules.Evaluate(GateTestData.VideoInput(report: report), profile);
        var check = GateTestData.Find(on.Checks, VideoGateCheckIds.SubtitleSync);
        check.Pass.ShouldBeFalse();
        check.Value.ShouldBe((decimal?)1);
        check.Threshold.ShouldBe((decimal?)0);

        // The hint only matters when subtitles were requested.
        var off = VideoGateRules.Evaluate(
            GateTestData.VideoInput(subtitlesEnabled: false, report: report),
            profile);
        off.Pass.ShouldBeTrue();
    }

    [Fact]
    public void VideoGate_AssetShaMismatch_Fails()
    {
        var profile = AutopilotGateProfile.Defaults;

        var mismatched = VideoGateRules.Evaluate(
            GateTestData.VideoInput(asset: GateTestData.Asset(sha256: "00aa")),
            profile);
        var check = GateTestData.Find(mismatched.Checks, VideoGateCheckIds.AssetIntegrity);
        check.Pass.ShouldBeFalse();
        check.Value.ShouldBe((decimal?)0);
        check.Detail.ShouldBe(GateDetailCodes.ShaMismatch);
        mismatched.Failures.ShouldBe([VideoGateCheckIds.AssetIntegrity]);

        // One side may be upper-case hex.
        var upperCase = VideoGateRules.Evaluate(
            GateTestData.VideoInput(asset: GateTestData.Asset(sha256: GateTestData.ReportSha.ToUpperInvariant())),
            profile);
        upperCase.Pass.ShouldBeTrue();

        // A stored file with no duration, or a row with no hash, is not a pass either.
        var zeroDuration = VideoGateRules.Evaluate(
            GateTestData.VideoInput(asset: GateTestData.Asset(durationSec: 0m)),
            profile);
        GateTestData.Find(zeroDuration.Checks, VideoGateCheckIds.AssetIntegrity).Detail
            .ShouldBe(GateDetailCodes.DurationNotPositive);

        var noHash = VideoGateRules.Evaluate(
            GateTestData.VideoInput(asset: GateTestData.Asset(sha256: null)),
            profile);
        var missing = GateTestData.Find(noHash.Checks, VideoGateCheckIds.AssetIntegrity);
        missing.Pass.ShouldBeFalse();
        missing.Value.ShouldBeNull();
        missing.Detail.ShouldBe(GateDetailCodes.FieldAbsent);
    }

    [Fact]
    public void VideoGate_ReportVerdictAndImageEvents_Fail()
    {
        var profile = AutopilotGateProfile.Defaults;

        // Defence in depth: 09.6.3 ends a failed render before this gate, but the flag is still read.
        var failedReport = VideoGateRules.Evaluate(
            GateTestData.VideoInput(report: GateTestData.Report(pass: false)),
            profile);
        var pass = GateTestData.Find(failedReport.Checks, VideoGateCheckIds.QualityReportPass);
        pass.Pass.ShouldBeFalse();
        pass.Value.ShouldBe((decimal?)0);
        pass.Threshold.ShouldBe((decimal?)1);
        failedReport.Failures.ShouldBe([VideoGateCheckIds.QualityReportPass]);

        var events = VideoGateRules.Evaluate(GateTestData.VideoInput(imageModerationEvents: 1), profile);
        var eventCheck = GateTestData.Find(events.Checks, VideoGateCheckIds.ImageModeration);
        eventCheck.Pass.ShouldBeFalse();
        eventCheck.Value.ShouldBe((decimal?)1);
        eventCheck.Threshold.ShouldBe((decimal?)0);
    }

    [Fact]
    public void VideoGate_NullInputOrProfile_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => VideoGateRules.Evaluate(null!, AutopilotGateProfile.Defaults));
        Should.Throw<ArgumentNullException>(
            () => VideoGateRules.Evaluate(GateTestData.VideoInput(), null!));
    }
}
