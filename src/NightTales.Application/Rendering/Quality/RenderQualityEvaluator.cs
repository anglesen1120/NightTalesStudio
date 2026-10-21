using NightTales.Application.Abstractions.Rendering;
using NightTales.Domain.Series;
using NightTales.RenderNode.Contract;

namespace NightTales.Application.Rendering.Quality;

/// <summary>
/// The pass/fail policy of <c>render-check.v1</c> (05 README D10, 05.5.1-T2): the responsible backend
/// turns one measured report into the stored verdict <c>render-quality.v1</c>. The tool measures, the
/// backend decides (F20) — this class therefore owns no media access, no database and no HTTP, and
/// <c>quality_report.images</c> is not produced here at all (03.4.2-T1, P3).
/// </summary>
/// <remarks>
/// <para>
/// The evaluation is <em>total and fail-closed</em> (P2): a missing report, an absent block or an
/// unrepresentable measure never throws — the affected check is <c>pass = false</c>. With a <c>null</c>
/// <paramref name="check"/> every check is <c>fail</c>, because a report that never arrived is not
/// evidence that the video is good.
/// </para>
/// <para>
/// The encoded values are pinned by the D14 fixtures (<c>render-quality.v1.{pass,fail-loudness}</c>):
/// the three "is it structurally there" checks encode their verdict as <c>1</c>/<c>0</c> against a
/// threshold of <c>1</c>, <c>subtitles_present</c> carries the cue count, <c>subtitle_text</c> the
/// boolean, <c>loudness</c> the integrated value against the band's top, and the two derived ratios
/// (<c>duration</c> drift, <c>subtitles.sync_suspect_share</c>) are rounded — 4 and 3 decimals — because
/// the stored verdict is a document other systems compare, not a float payload.
/// </para>
/// </remarks>
public static class RenderQualityEvaluator
{
    /// <summary>Check 1: the container probed cleanly, i.e. the report carries no errors.</summary>
    public const string ContainerValid = "container_valid";

    /// <summary>Check 2: an h264 video stream and an aac audio stream.</summary>
    public const string Streams = "streams";

    /// <summary>Check 3: the frame size matches the series' aspect.</summary>
    public const string Resolution = "resolution";

    /// <summary>Check 4: the container duration is within the drift of the narration duration.</summary>
    public const string Duration = "duration";

    /// <summary>Check 5: the integrated loudness is inside the accepted band.</summary>
    public const string Loudness = "loudness";

    /// <summary>Check 6: the true peak is at or below the ceiling.</summary>
    public const string TruePeak = "true_peak";

    /// <summary>Check 7: no silence longer than the limit.</summary>
    public const string Silence = "silence";

    /// <summary>Check 8: the SRT exists, has cues, none of them zero-length, and covers enough of the video.</summary>
    public const string SubtitlesPresent = "subtitles_present";

    /// <summary>Check 9: every cue's text matched the script.</summary>
    public const string SubtitleText = "subtitle_text";

    /// <summary>Advisory hint: the share of mis-timed-looking cues is above the hint threshold.</summary>
    public const string SubtitleSyncSuspectHint = "subtitle_sync_suspect";

    /// <summary>Advisory hint: at least one cue is longer than the preset allows.</summary>
    public const string SubtitleCuesLongHint = "subtitle_cues_long";

    /// <summary>Advisory hint: <c>loudnorm</c> fell back to its dynamic mode, so the band is approximate.</summary>
    public const string LoudnormDynamicHint = "loudnorm_dynamic";

    /// <summary>Advisory hint: the render had no music track (T12).</summary>
    public const string MusicNoneHint = "music_none";

    /// <summary>The landscape frame size the <c>resolution</c> check requires (C14).</summary>
    public const int LandscapeWidth = 1920;

    /// <summary>The landscape frame height.</summary>
    public const int LandscapeHeight = 1080;

    /// <summary>The portrait frame width.</summary>
    public const int PortraitWidth = 1080;

    /// <summary>The portrait frame height (C14).</summary>
    public const int PortraitHeight = 1920;

    /// <summary>The decimals the stored <c>duration</c> drift is rounded to (D14 fixture).</summary>
    public const int DurationDriftDecimals = 4;

    /// <summary>The decimals the stored <c>sync_suspect_share</c> is rounded to (D14 fixture).</summary>
    public const int SyncSuspectShareDecimals = 3;

    private const double VerdictPass = 1;

    private const string H264 = "h264";

    private const string Aac = "aac";

    /// <summary>Evaluates the policy over one measured report and returns the stored verdict.</summary>
    /// <param name="check">The measured <c>render-check.v1</c>, or <c>null</c> when the node never returned one.</param>
    /// <param name="expect">The render request's own expectations (aspect, subtitles, music).</param>
    /// <param name="policy">The thresholds; bound from <c>RenderQuality</c> and validated at startup.</param>
    public static RenderQualityReportV1 Evaluate(
        RenderCheckV1? check,
        RenderExpectations expect,
        RenderQualityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(expect);
        ArgumentNullException.ThrowIfNull(policy);

        if (check is null)
        {
            return FailClosed(policy);
        }

        var subtitles = check.Subtitles;
        var cues = subtitles?.Cues ?? 0;
        var coverageShare = Finite(subtitles?.CoverageShare) ?? 0;
        var longCues = subtitles?.LongCues ?? 0;
        // The hint compares the raw quotient, never the 3-decimal display rounding: 211/2100 = 0.1004…
        // is above the share even though the stored value reads 0.100 (D10 says "share > 0.10").
        var syncSuspectShareRaw = (subtitles?.SyncSuspect ?? 0) / (double)Math.Max(cues, 1);
        var syncSuspectShare = Math.Round(syncSuspectShareRaw, SyncSuspectShareDecimals);
        // T65: a non-finite measure is an unmeasured measure, so it is read as null here — it can then
        // neither pass a comparison nor reach the stored document, which RenderNodeJson.Strict refuses
        // to serialise. The null coalescing is per pass, so a non-finite "after" falls back to "before".
        var durationSec = Finite(check.Container?.DurationS);
        var ttsDurationSec = Finite(check.Narration?.DurationS);
        var integratedLufs = Finite(check.Loudness?.After?.IntegratedLufs)
            ?? Finite(check.Loudness?.Before?.IntegratedLufs);
        var truePeakDbtp = Finite(check.Loudness?.After?.TruePeakDbtp)
            ?? Finite(check.Loudness?.Before?.TruePeakDbtp);
        var longestSilenceSeconds = Finite(check.Silence?.LongestS);
        // The served bytes must be identifiable: with neither an output nor an input sha256 the report
        // cannot say which file it judged, so the verdict fails closed (P2) instead of passing silently.
        var sha256 = check.Output?.Sha256 ?? check.Input?.Sha256;

        var checks = new List<QualityCheck>
        {
            ContainerValidCheck(check),
            StreamsCheck(check),
            ResolutionCheck(check, expect.Aspect),
            DurationCheck(durationSec, ttsDurationSec, policy),
            LoudnessCheck(integratedLufs, policy),
            TruePeakCheck(truePeakDbtp, policy),
            SilenceCheck(longestSilenceSeconds, policy),
            SubtitlesPresentCheck(subtitles, expect.SubtitlesEnabled, cues, coverageShare, policy),
            SubtitleTextCheck(subtitles, expect.SubtitlesEnabled),
        };

        return new RenderQualityReportV1
        {
            Pass = sha256 is not null && checks.TrueForAll(item => item.Pass),
            PolicyVersion = policy.PolicyVersion,
            CheckToolVersion = check.ToolVersion,
            DurationSec = durationSec,
            TtsDurationSec = ttsDurationSec,
            IntegratedLufs = integratedLufs,
            TruePeakDbtp = truePeakDbtp,
            Width = check.Video?.Width,
            Height = check.Video?.Height,
            SubtitlesPresent = subtitles?.Present ?? false,
            Sha256 = sha256,
            Normalized = check.Output?.Normalized ?? false,
            Subtitles = subtitles is null
                ? null
                : new RenderQualitySubtitles(cues, coverageShare, syncSuspectShare, longCues, subtitles.TextMatchesScript),
            Checks = checks,
            Hints = BuildHints(check, expect, policy, syncSuspectShareRaw, longCues),
            Images = null,
        };
    }

    /// <summary>The verdict when no report arrived: every check fails with no value (P2).</summary>
    private static RenderQualityReportV1 FailClosed(RenderQualityPolicy policy) => new()
    {
        Pass = false,
        PolicyVersion = policy.PolicyVersion,
        CheckToolVersion = null,
        Checks =
        [
            new QualityCheck(ContainerValid, null, VerdictPass, false),
            new QualityCheck(Streams, null, VerdictPass, false),
            new QualityCheck(Resolution, null, VerdictPass, false),
            new QualityCheck(Duration, null, policy.MaxDurationDrift, false),
            new QualityCheck(Loudness, null, policy.MaxLufs, false),
            new QualityCheck(TruePeak, null, policy.MaxTruePeakDbtp, false),
            new QualityCheck(Silence, null, policy.MaxLongestSilenceSeconds, false),
            new QualityCheck(SubtitlesPresent, null, policy.MinCues, false),
            new QualityCheck(SubtitleText, null, VerdictPass, false),
        ],
        Hints = [],
        Images = null,
    };

    private static List<string> BuildHints(
        RenderCheckV1 check,
        RenderExpectations expect,
        RenderQualityPolicy policy,
        double syncSuspectShareRaw,
        int longCues)
    {
        var hints = new List<string>(4);

        if (syncSuspectShareRaw > policy.SyncSuspectHintShare)
        {
            hints.Add(SubtitleSyncSuspectHint);
        }

        if (longCues >= 1)
        {
            hints.Add(SubtitleCuesLongHint);
        }

        // `"warnings": null` is wire-reachable ([JsonRequired] demands presence, not non-null); a
        // missing list means no warning, so it never raises the hint and never throws (P2).
        if (check.Warnings?.Contains(LoudnormDynamicHint, StringComparer.Ordinal) == true)
        {
            hints.Add(LoudnormDynamicHint);
        }

        if (!expect.MusicSelected)
        {
            hints.Add(MusicNoneHint);
        }

        return hints;
    }

    /// <summary>T65: returns the value when it is finite, otherwise <c>null</c> (a non-finite measure is unmeasured).</summary>
    private static double? Finite(double? value) =>
        value is { } finite && double.IsFinite(finite) ? finite : null;

    /// <summary>Fails on any tool error, the known ones (<c>container_invalid</c>, <c>audio_silent</c>) or a future code.</summary>
    private static QualityCheck ContainerValidCheck(RenderCheckV1 check)
    {
        var errors = check.Errors;

        return new QualityCheck(
            ContainerValid,
            errors is { Count: 0 } ? VerdictPass : 0,
            VerdictPass,
            errors is { Count: 0 });
    }

    private static QualityCheck StreamsCheck(RenderCheckV1 check)
    {
        var pass = check.Video?.Codec == H264 && check.Audio?.Codec == Aac;

        return new QualityCheck(Streams, pass ? VerdictPass : 0, VerdictPass, pass);
    }

    private static QualityCheck ResolutionCheck(RenderCheckV1 check, Aspect aspect)
    {
        var (width, height) = aspect switch
        {
            Aspect.Portrait => (PortraitWidth, PortraitHeight),
            _ => (LandscapeWidth, LandscapeHeight),
        };

        var pass = check.Video?.Width == width && check.Video?.Height == height;

        return new QualityCheck(Resolution, pass ? VerdictPass : 0, VerdictPass, pass);
    }

    private static QualityCheck DurationCheck(double? durationSec, double? ttsDurationSec, RenderQualityPolicy policy)
    {
        if (durationSec is not { } duration || ttsDurationSec is not { } narration || narration <= 0)
        {
            // F17: a render whose narration duration was never measured cannot be judged, so it fails.
            return new QualityCheck(Duration, null, policy.MaxDurationDrift, false);
        }

        var drift = Math.Abs(duration - narration) / narration;

        return new QualityCheck(
            Duration,
            Math.Round(drift, DurationDriftDecimals),
            policy.MaxDurationDrift,
            drift <= policy.MaxDurationDrift);
    }

    private static QualityCheck LoudnessCheck(double? integratedLufs, RenderQualityPolicy policy)
    {
        // Both ends of the band are inclusive passes (D10).
        var pass = integratedLufs is { } value && value >= policy.MinLufs && value <= policy.MaxLufs;

        return new QualityCheck(Loudness, integratedLufs, policy.MaxLufs, pass);
    }

    private static QualityCheck TruePeakCheck(double? truePeakDbtp, RenderQualityPolicy policy)
    {
        var pass = truePeakDbtp is { } value && value <= policy.MaxTruePeakDbtp;

        return new QualityCheck(TruePeak, truePeakDbtp, policy.MaxTruePeakDbtp, pass);
    }

    private static QualityCheck SilenceCheck(double? longestSilenceSeconds, RenderQualityPolicy policy)
    {
        var pass = longestSilenceSeconds is { } value && value <= policy.MaxLongestSilenceSeconds;

        return new QualityCheck(Silence, longestSilenceSeconds, policy.MaxLongestSilenceSeconds, pass);
    }

    private static QualityCheck SubtitlesPresentCheck(
        RenderCheckSubtitlesV1? subtitles,
        bool subtitlesEnabled,
        int cues,
        double coverageShare,
        RenderQualityPolicy policy)
    {
        if (!subtitlesEnabled)
        {
            return new QualityCheck(SubtitlesPresent, null, policy.MinCues, true);
        }

        var pass = subtitles is { Present: true }
            && cues >= policy.MinCues
            && subtitles.ZeroDuration == 0
            && coverageShare >= policy.MinSubtitleCoverage;

        return new QualityCheck(SubtitlesPresent, cues, policy.MinCues, pass);
    }

    private static QualityCheck SubtitleTextCheck(RenderCheckSubtitlesV1? subtitles, bool subtitlesEnabled)
    {
        if (!subtitlesEnabled)
        {
            return new QualityCheck(SubtitleText, null, VerdictPass, true);
        }

        var matches = subtitles?.TextMatchesScript;

        return new QualityCheck(SubtitleText, matches is null ? null : matches.Value ? VerdictPass : 0, VerdictPass, matches == true);
    }
}
