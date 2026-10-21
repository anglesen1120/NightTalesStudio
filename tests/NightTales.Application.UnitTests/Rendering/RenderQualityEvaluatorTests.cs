using System.Text.Json;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Rendering.Quality;
using NightTales.Domain.Series;
using NightTales.RenderNode.Contract;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Rendering;

/// <summary>
/// The D10 verdict of 05.5.1-T2 over the D14 fixtures and the boundary values of every check: the two
/// pinned reports are reproduced exactly, and every unmeasurable shape fails closed instead of throwing
/// (P2). The check-level helpers keep each test to the rule it names.
/// </summary>
public sealed class RenderQualityEvaluatorTests
{
    public static TheoryData<string> ReportFixtures => RenderFixtures.ReportTheoryData();

    [Fact]
    public void Ok_fixture_passes_every_check()
    {
        var expected = RenderFixtures.ReadReport("render-quality.v1.pass.json");

        var actual = RenderFixtures.Evaluate(RenderFixtures.ReadCheck("render-check.v1.ok.json"));

        Assert.Equivalent(expected, actual, true);
    }

    [Fact]
    public void Loud_fixture_fails_loudness_only()
    {
        var pinned = RenderFixtures.ReadReport("render-quality.v1.fail-loudness.json");

        var actual = RenderFixtures.Evaluate(RenderFixtures.ReadCheck("render-check.v1.loud.json"));

        actual.Pass.ShouldBeFalse();
        actual.Checks.Where(check => !check.Pass).Select(check => check.Id)
            .ShouldBe([RenderQualityEvaluator.Loudness]);

        // `render-check.v1.loud` gained the D9 `loudnorm_dynamic` warning on 2026-09-27, after the
        // pinned report was written, and D10 turns that warning into one more advisory hint; adding it
        // to the expectation reproduces every other field of `render-quality.v1.fail-loudness` exactly.
        Assert.Equivalent(
            pinned with { Hints = [.. pinned.Hints, RenderQualityEvaluator.LoudnormDynamicHint] },
            actual,
            true);
    }

    [Fact]
    public void Null_check_fails_closed()
    {
        var report = RenderFixtures.Evaluate(null);

        report.Pass.ShouldBeFalse();
        report.Checks.Count.ShouldBe(9);
        report.Checks.All(check => !check.Pass && check.Value is null).ShouldBeTrue();
        report.Hints.ShouldBeEmpty();
        report.Images.ShouldBeNull();
    }

    [Fact]
    public void MissingCheck_failsClosed_everyCheckFalse()
    {
        var report = RenderFixtures.Evaluate(null);

        report.Checks.Select(check => check.Id).ShouldBe(
        [
            RenderQualityEvaluator.ContainerValid,
            RenderQualityEvaluator.Streams,
            RenderQualityEvaluator.Resolution,
            RenderQualityEvaluator.Duration,
            RenderQualityEvaluator.Loudness,
            RenderQualityEvaluator.TruePeak,
            RenderQualityEvaluator.Silence,
            RenderQualityEvaluator.SubtitlesPresent,
            RenderQualityEvaluator.SubtitleText,
        ]);
        report.PolicyVersion.ShouldBe(1);
    }

    [Fact]
    public void Duration_boundary_0_05_passes_0_0501_fails()
    {
        var passing = RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(durationS: 105, narrationS: 100));
        var failing = RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(durationS: 105.01, narrationS: 100));

        var passingCheck = RenderFixtures.CheckOf(passing, RenderQualityEvaluator.Duration);
        passingCheck.Pass.ShouldBeTrue();
        passingCheck.Value.ShouldBe(0.05);
        RenderFixtures.CheckOf(failing, RenderQualityEvaluator.Duration).Pass.ShouldBeFalse();
    }

    [Fact]
    public void Duration_null_narration_fails()
    {
        var report = RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(narrationS: null));

        var check = RenderFixtures.CheckOf(report, RenderQualityEvaluator.Duration);

        check.Pass.ShouldBeFalse();
        check.Value.ShouldBeNull();
    }

    [Fact]
    public void Loudness_minus16_and_minus14_are_inclusive_passes()
    {
        RenderFixtures.CheckOf(RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(lufs: -16)), RenderQualityEvaluator.Loudness).Pass.ShouldBeTrue();
        RenderFixtures.CheckOf(RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(lufs: -14)), RenderQualityEvaluator.Loudness).Pass.ShouldBeTrue();
        RenderFixtures.CheckOf(RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(lufs: -16.1)), RenderQualityEvaluator.Loudness).Pass.ShouldBeFalse();
        RenderFixtures.CheckOf(RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(lufs: -13.9)), RenderQualityEvaluator.Loudness).Pass.ShouldBeFalse();
    }

    [Fact]
    public void TruePeak_minus1_0_passes_minus0_9_fails()
    {
        RenderFixtures.CheckOf(RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(truePeak: -1.0)), RenderQualityEvaluator.TruePeak).Pass.ShouldBeTrue();
        RenderFixtures.CheckOf(RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(truePeak: -0.9)), RenderQualityEvaluator.TruePeak).Pass.ShouldBeFalse();
    }

    [Fact]
    public void Silence_exactly_10s_passes()
    {
        RenderFixtures.CheckOf(RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(longestSilenceS: 10)), RenderQualityEvaluator.Silence).Pass.ShouldBeTrue();
        RenderFixtures.CheckOf(RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(longestSilenceS: 10.1)), RenderQualityEvaluator.Silence).Pass.ShouldBeFalse();
    }

    [Fact]
    public void SubtitleCoverage_0_50_passes()
    {
        RenderFixtures.CheckOf(RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(coverageShare: 0.5)), RenderQualityEvaluator.SubtitlesPresent).Pass.ShouldBeTrue();
        RenderFixtures.CheckOf(RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(coverageShare: 0.49)), RenderQualityEvaluator.SubtitlesPresent).Pass.ShouldBeFalse();
    }

    [Fact]
    public void ZeroDurationCue_present_fails_subtitles_present()
    {
        var report = RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(zeroDuration: 1));

        var check = RenderFixtures.CheckOf(report, RenderQualityEvaluator.SubtitlesPresent);

        check.Pass.ShouldBeFalse();
        check.Value.ShouldBe(212);
    }

    [Fact]
    public void ZeroCues_fixture_fails_subtitles_present()
    {
        var report = RenderFixtures.Evaluate(RenderFixtures.ReadCheck("render-check.v1.zero-cues.json"));

        report.Pass.ShouldBeFalse();
        RenderFixtures.CheckOf(report, RenderQualityEvaluator.SubtitlesPresent).Pass.ShouldBeFalse();
    }

    [Fact]
    public void TextMismatch_fails_subtitle_text()
    {
        RenderFixtures.CheckOf(RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(textMatchesScript: false)), RenderQualityEvaluator.SubtitleText).Pass.ShouldBeFalse();

        // [DECISION] `script_missing` (TextMatchesScript null) fails the check: only `SubtitlesEnabled =
        // false` skips it, so a render we cannot check never passes.
        var missingScript = RenderFixtures.CheckOf(
            RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(textMatchesScript: null)),
            RenderQualityEvaluator.SubtitleText);
        missingScript.Pass.ShouldBeFalse();
        missingScript.Value.ShouldBeNull();
    }

    [Fact]
    public void SubtitlesDisabled_skips_both_subtitle_checks_with_null_value()
    {
        var check = RenderFixtures.ReadCheck("render-check.v1.no-subtitles.json");

        var skipped = RenderFixtures.Evaluate(check, subtitlesEnabled: false);
        var present = RenderFixtures.CheckOf(skipped, RenderQualityEvaluator.SubtitlesPresent);
        var text = RenderFixtures.CheckOf(skipped, RenderQualityEvaluator.SubtitleText);
        present.Pass.ShouldBeTrue();
        present.Value.ShouldBeNull();
        text.Pass.ShouldBeTrue();
        text.Value.ShouldBeNull();

        RenderFixtures.CheckOf(RenderFixtures.Evaluate(check), RenderQualityEvaluator.SubtitlesPresent).Pass.ShouldBeFalse();
    }

    [Fact]
    public void NonEmptyErrors_fails_container_valid()
    {
        var report = RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(errors: ["container_invalid"]));

        var check = RenderFixtures.CheckOf(report, RenderQualityEvaluator.ContainerValid);

        check.Pass.ShouldBeFalse();
        check.Value.ShouldBe(0);
    }

    [Fact]
    public void Silent_fixture_fails_every_unmeasurable_check()
    {
        var report = RenderFixtures.Evaluate(RenderFixtures.ReadCheck("render-check.v1.silent.json"));

        report.Pass.ShouldBeFalse();
        report.Checks.Where(check => !check.Pass).Select(check => check.Id).ShouldBe(
        [
            RenderQualityEvaluator.ContainerValid,
            RenderQualityEvaluator.Loudness,
            RenderQualityEvaluator.TruePeak,
            RenderQualityEvaluator.Silence,
            RenderQualityEvaluator.SubtitlesPresent,
            RenderQualityEvaluator.SubtitleText,
        ]);
        report.IntegratedLufs.ShouldBeNull();
    }

    [Fact]
    public void Truncated_fixture_fails_closed()
    {
        var report = RenderFixtures.Evaluate(RenderFixtures.ReadCheck("render-check.v1.truncated.json"));

        report.Pass.ShouldBeFalse();
        report.Checks.Count.ShouldBe(9);
        report.Checks.ShouldAllBe(check => !check.Pass);
        report.Subtitles.ShouldBeNull();
        report.DurationSec.ShouldBeNull();
    }

    [Fact]
    public void Hints_orderIsStable_syncSuspect_then_cuesLong_then_loudnormDynamic()
    {
        var report = RenderFixtures.Evaluate(
            RenderFixtures.SyntheticCheck(syncSuspect: 30, longCues: 1, warnings: ["loudnorm_dynamic"]),
            musicSelected: false);

        report.Hints.ShouldBe(
        [
            RenderQualityEvaluator.SubtitleSyncSuspectHint,
            RenderQualityEvaluator.SubtitleCuesLongHint,
            RenderQualityEvaluator.LoudnormDynamicHint,
            RenderQualityEvaluator.MusicNoneHint,
        ]);
    }

    [Fact]
    public void MusicNone_hint_when_no_track_selected()
    {
        var report = RenderFixtures.Evaluate(RenderFixtures.ReadCheck("render-check.v1.ok.json"), musicSelected: false);

        report.Hints.ShouldBe([RenderQualityEvaluator.SubtitleCuesLongHint, RenderQualityEvaluator.MusicNoneHint]);
    }

    [Fact]
    public void Portrait_aspect_requires_portrait_resolution()
    {
        var landscape = RenderFixtures.Evaluate(RenderFixtures.ReadCheck("render-check.v1.ok.json"), aspect: Aspect.Portrait);
        var portrait = RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(width: 1080, height: 1920), aspect: Aspect.Portrait);

        landscape.Pass.ShouldBeFalse();
        RenderFixtures.CheckOf(portrait, RenderQualityEvaluator.Resolution).Pass.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(ReportFixtures))]
    public void RenderQualityReportV1Fixture_round_trip_is_lossless(string file)
    {
        var json = RenderFixtures.ReadJson(file);

        var report = RenderFixtures.ReadReport(file);
        var roundTripped = JsonSerializer.Serialize(report, RenderNodeJson.Strict);

        RenderFixtures.DocumentsAreEqual(json, roundTripped).ShouldBeTrue($"'{file}' must round-trip");

        // Reading the canonical document again must produce the same canonical document. Record
        // equality is not usable here: the collection members (checks/hints) compare by reference.
        var canonical = JsonSerializer.Serialize(
            JsonSerializer.Deserialize<RenderQualityReportV1>(roundTripped, RenderNodeJson.Strict),
            RenderNodeJson.Strict);
        canonical.ShouldBe(roundTripped);
    }
}
