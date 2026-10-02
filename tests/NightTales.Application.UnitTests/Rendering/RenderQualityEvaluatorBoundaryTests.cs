using System.Text.Json;
using NightTales.Application.Rendering.Quality;
using NightTales.Domain.Series;
using NightTales.RenderNode.Contract;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Rendering;

/// <summary>
/// QA boundary pass over <see cref="RenderQualityEvaluator"/> (05.5.1-T2, 05 README D10): every
/// threshold exactly at, just below and just above its edge; the D10 check order on a real report;
/// fail-closed behaviour for every null block a wire document can carry; and the rule that a
/// non-finite measure never reaches the stored verdict (T65).
/// </summary>
public sealed class RenderQualityEvaluatorBoundaryTests
{
    private static readonly string[] CheckOrder =
    [
        "container_valid", "streams", "resolution", "duration", "loudness",
        "true_peak", "silence", "subtitles_present", "subtitle_text",
    ];

    private static readonly string[] HintOrder =
    [
        "subtitle_sync_suspect", "subtitle_cues_long", "loudnorm_dynamic", "music_none",
    ];

    [Fact]
    public void Check_order_on_a_real_report_follows_D10()
    {
        var report = RenderFixtures.Evaluate(RenderFixtures.ReadCheck("render-check.v1.ok.json"));

        report.Checks.Select(check => check.Id).ShouldBe(CheckOrder);
    }

    [Fact]
    public void Hint_order_on_a_real_report_follows_D10()
    {
        var report = RenderFixtures.Evaluate(
            RenderFixtures.ReadCheck("render-check.v1.loud.json"),
            musicSelected: false);

        // loud has cues=212/long_cues=3 + loudnorm_dynamic warning; music deselected adds music_none.
        report.Hints.ShouldBe(["subtitle_cues_long", "loudnorm_dynamic", "music_none"]);
    }

    [Fact]
    public void Duration_drift_boundary_below_at_above()
    {
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(durationS: 104.999, narrationS: 100)),
                RenderQualityEvaluator.Duration).Pass.ShouldBeTrue();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(durationS: 105, narrationS: 100)),
                RenderQualityEvaluator.Duration).Pass.ShouldBeTrue();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(durationS: 105.01, narrationS: 100)),
                RenderQualityEvaluator.Duration).Pass.ShouldBeFalse();
    }

    [Fact]
    public void Duration_zero_or_negative_narration_fails_without_dividing()
    {
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(narrationS: 0)),
                RenderQualityEvaluator.Duration).Pass.ShouldBeFalse();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(narrationS: -5)),
                RenderQualityEvaluator.Duration).Pass.ShouldBeFalse();
    }
    [Fact]
    public void Loudness_boundary_below_min_above_max()
    {
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(lufs: -15.9999)),
                RenderQualityEvaluator.Loudness).Pass.ShouldBeTrue();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(lufs: -16.0001)),
                RenderQualityEvaluator.Loudness).Pass.ShouldBeFalse();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(lufs: -14.0001)),
                RenderQualityEvaluator.Loudness).Pass.ShouldBeTrue();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(lufs: -13.9999)),
                RenderQualityEvaluator.Loudness).Pass.ShouldBeFalse();
    }

    [Fact]
    public void TruePeak_boundary_below_at_above()
    {
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(truePeak: -1.0001)),
                RenderQualityEvaluator.TruePeak).Pass.ShouldBeTrue();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(truePeak: -1.0)),
                RenderQualityEvaluator.TruePeak).Pass.ShouldBeTrue();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(truePeak: -0.9999)),
                RenderQualityEvaluator.TruePeak).Pass.ShouldBeFalse();
    }

    [Fact]
    public void Silence_boundary_below_at_above()
    {
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(longestSilenceS: 9.999)),
                RenderQualityEvaluator.Silence).Pass.ShouldBeTrue();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(longestSilenceS: 10)),
                RenderQualityEvaluator.Silence).Pass.ShouldBeTrue();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(longestSilenceS: 10.001)),
                RenderQualityEvaluator.Silence).Pass.ShouldBeFalse();
    }

    [Fact]
    public void Resolution_boundary_wrong_width_height_and_aspect()
    {
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(width: 1919, height: 1080)),
                RenderQualityEvaluator.Resolution).Pass.ShouldBeFalse();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(width: 1921, height: 1080)),
                RenderQualityEvaluator.Resolution).Pass.ShouldBeFalse();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(width: 1920, height: 1079)),
                RenderQualityEvaluator.Resolution).Pass.ShouldBeFalse();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(width: 1080, height: 1920)),
                RenderQualityEvaluator.Resolution).Pass.ShouldBeFalse();

        // Portrait expectation: exact pass, swapped fails.
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(
                    RenderFixtures.SyntheticCheck(width: 1080, height: 1920), aspect: Aspect.Portrait),
                RenderQualityEvaluator.Resolution).Pass.ShouldBeTrue();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(
                    RenderFixtures.SyntheticCheck(width: 1079, height: 1920), aspect: Aspect.Portrait),
                RenderQualityEvaluator.Resolution).Pass.ShouldBeFalse();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(
                    RenderFixtures.SyntheticCheck(width: 1920, height: 1080), aspect: Aspect.Portrait),
                RenderQualityEvaluator.Resolution).Pass.ShouldBeFalse();
    }

    [Fact]
    public void Streams_boundary_wrong_or_missing_codec()
    {
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(videoCodec: "vp9")),
                RenderQualityEvaluator.Streams).Pass.ShouldBeFalse();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(audioCodec: "opus")),
                RenderQualityEvaluator.Streams).Pass.ShouldBeFalse();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(audioCodec: null)),
                RenderQualityEvaluator.Streams).Pass.ShouldBeFalse();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(width: null)),
                RenderQualityEvaluator.Streams).Pass.ShouldBeFalse();
    }

    [Fact]
    public void Subtitles_present_boundary_cues_coverage_zero_duration()
    {
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(cues: 1)),
                RenderQualityEvaluator.SubtitlesPresent).Pass.ShouldBeTrue();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(cues: 0)),
                RenderQualityEvaluator.SubtitlesPresent).Pass.ShouldBeFalse();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(coverageShare: 0.5001)),
                RenderQualityEvaluator.SubtitlesPresent).Pass.ShouldBeTrue();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(coverageShare: 0.4999)),
                RenderQualityEvaluator.SubtitlesPresent).Pass.ShouldBeFalse();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(subtitlesPresent: false)),
                RenderQualityEvaluator.SubtitlesPresent).Pass.ShouldBeFalse();
        RenderFixtures.CheckOf(
                RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(subtitlesPresent: null)),
                RenderQualityEvaluator.SubtitlesPresent).Pass.ShouldBeFalse();
    }

    [Fact]
    public void Sync_suspect_share_boundary_at_and_above_hint_threshold()
    {
        // Share exactly at 0.10 must NOT raise the hint (D10: share > 0.10).
        var atThreshold = RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(cues: 210, syncSuspect: 21));
        atThreshold.Subtitles!.SyncSuspectShare.ShouldBe(0.1);
        atThreshold.Hints.ShouldNotContain(RenderQualityEvaluator.SubtitleSyncSuspectHint);

        // 105/1000 = 0.105 clearly above → hint.
        var above = RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(cues: 1000, syncSuspect: 105));
        above.Hints.ShouldContain(RenderQualityEvaluator.SubtitleSyncSuspectHint);
    }

    [Fact]
    public void Sync_suspect_share_just_above_threshold_raises_hint()
    {
        // 211/2100 = 0.100476… > 0.10 → the hint must fire even though the stored
        // (rounded-to-3) share reads 0.100; the stored value is a display rounding, not the measure.
        var report = RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(cues: 2100, syncSuspect: 211));

        report.Hints.ShouldContain(RenderQualityEvaluator.SubtitleSyncSuspectHint);
    }

    [Fact]
    public void Output_null_falls_back_to_input_sha256_and_normalized_false()
    {
        var check = RenderFixtures.SyntheticCheck() with { Output = null };

        var report = RenderFixtures.Evaluate(check);

        report.Sha256.ShouldBe("in");
        report.Normalized.ShouldBeFalse();
    }

    [Fact]
    public void Null_errors_fails_container_valid_not_throw()
    {
        // [JsonRequired] rejects a missing member, but a wire document can still carry an explicit
        // null for a required member — the evaluator must fail closed, not crash (P2).
        var report = RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck() with { Errors = null! });

        RenderFixtures.CheckOf(report, RenderQualityEvaluator.ContainerValid).Pass.ShouldBeFalse();
    }

    [Fact]
    public void Null_warnings_fails_closed_not_throw()
    {
        var report = RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck() with { Warnings = null! });

        report.Pass.ShouldBeTrue();
        report.Hints.ShouldNotContain(RenderQualityEvaluator.LoudnormDynamicHint);
    }

    [Fact]
    public void Null_input_without_output_fails_closed_not_throw()
    {
        // `"input": null` is wire-reachable ([JsonRequired] demands presence, not non-null); with no
        // `output` either, the sha256 fallback must fail closed instead of dereferencing null.
        var report = RenderFixtures.Evaluate(
            RenderFixtures.SyntheticCheck() with { Output = null, Input = null! });

        report.Pass.ShouldBeFalse();
        report.Sha256.ShouldBeNull();
    }

    [Fact]
    public void Non_finite_measures_fail_and_never_reach_the_verdict()
    {
        // T65: an unmeasurable measure is null in the stored document, never NaN/Infinity — and the
        // report must stay serializable through RenderNodeJson.Strict.
        foreach (var nonFinite in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var loudness = RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(lufs: nonFinite));
            RenderFixtures.CheckOf(loudness, RenderQualityEvaluator.Loudness).Pass.ShouldBeFalse();

            var truePeak = RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(truePeak: nonFinite));
            RenderFixtures.CheckOf(truePeak, RenderQualityEvaluator.TruePeak).Pass.ShouldBeFalse();

            var silence = RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(longestSilenceS: nonFinite));
            RenderFixtures.CheckOf(silence, RenderQualityEvaluator.Silence).Pass.ShouldBeFalse();

            var duration = RenderFixtures.Evaluate(RenderFixtures.SyntheticCheck(durationS: nonFinite));
            RenderFixtures.CheckOf(duration, RenderQualityEvaluator.Duration).Pass.ShouldBeFalse();

            JsonSerializer.Serialize(loudness, RenderNodeJson.Strict); // must not throw
            JsonSerializer.Serialize(truePeak, RenderNodeJson.Strict);
            JsonSerializer.Serialize(silence, RenderNodeJson.Strict);
            JsonSerializer.Serialize(duration, RenderNodeJson.Strict);
        }
    }

    [Fact]
    public void Subtitles_disabled_skips_checks_even_when_subtitle_block_absent()
    {
        var report = RenderFixtures.Evaluate(
            RenderFixtures.SyntheticCheck(subtitlesPresent: null), subtitlesEnabled: false);

        report.Checks.Where(check => !check.Pass).Select(check => check.Id)
            .ShouldBeEmpty();
        report.Pass.ShouldBeTrue();
    }
}
