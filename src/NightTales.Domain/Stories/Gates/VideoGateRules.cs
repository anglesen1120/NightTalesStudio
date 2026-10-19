using NightTales.Domain.Series;

namespace NightTales.Domain.Stories.Gates;

/// <summary>
/// The video half of the auto-approval gate (02.8.2 plan §3.2): the ten checks over the finished render's
/// stored quality report, its asset row and the series aspect, with no I/O, no clock and no randomness.
/// </summary>
/// <remarks>
/// <para>
/// It fails closed: the report's members are all nullable on purpose (CR-6), so a field that is missing,
/// renamed or unmeasurable fails its own check with <see cref="GateDetailCodes.FieldAbsent"/> instead of being
/// read as a zero or a <c>false</c>, and an absent report fails check 1 and closes every dependent check.
/// Nothing here throws on data: a number the report could not have measured — non-finite, or beyond the
/// magnitude of a real duration or loudness — is read as absent rather than cast (SEC-3).
/// </para>
/// <para>
/// Checks 6 and 10 are recorded as passes with no measured value when subtitles were not requested, exactly
/// like <c>RenderQualityEvaluator</c>'s <c>subtitles_present</c> check, so the check list keeps its ten
/// entries. Every check records the threshold it faced (plan §3.3).
/// </para>
/// </remarks>
public static class VideoGateRules
{
    /// <summary>The advisory hint of <c>render-quality.v1</c> that check 10 reads (05 README D10, CR-6).</summary>
    public const string SubtitleSyncSuspectHint = "subtitle_sync_suspect";

    /// <summary>The landscape frame width check 5 requires (C14).</summary>
    public const int LandscapeWidth = 1920;

    /// <summary>The landscape frame height check 5 requires (C14).</summary>
    public const int LandscapeHeight = 1080;

    /// <summary>The portrait frame width check 5 requires (C14).</summary>
    public const int PortraitWidth = 1080;

    /// <summary>The portrait frame height check 5 requires (C14).</summary>
    public const int PortraitHeight = 1920;

    private const int DriftDecimals = 4;

    /// <summary>
    /// The largest magnitude a stored measurement may carry before the gate refuses to read it (SEC-3): no render
    /// reports a duration or a loudness this far out, and a value above it cannot be cast to <see cref="decimal"/>
    /// without overflowing. Check 3 and check 4 read such a number as absent and fail closed instead of throwing.
    /// </summary>
    private const double MaxMeasurableMagnitude = 1e9;

    /// <summary>Evaluates the ten video checks over one assembled input.</summary>
    /// <param name="input">The stored facts; see <see cref="VideoGateInput"/>.</param>
    /// <param name="profile">The resolved thresholds, normally <see cref="AutopilotGateProfile.Resolve"/>'s result.</param>
    /// <returns>The verdict, in plan §3.2 order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="profile"/> is null.</exception>
    public static VideoGateResult Evaluate(VideoGateInput input, AutopilotGateProfile profile)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(profile);

        var thresholds = profile.Video;
        var report = input.Report;
        var checks = new List<GateCheck>(VideoGateCheckIds.All.Count);

        // 1 — the report exists at all.
        var present = report is not null;
        checks.Add(new GateCheck(
            VideoGateCheckIds.QualityReportPresent,
            GateCheckKinds.Flag,
            present ? 1 : 0,
            1,
            present,
            present ? null : GateDetailCodes.FieldAbsent));

        // 2 — the report's own verdict (defence in depth: 09.6.3 ends a failed render before this gate).
        checks.Add(report?.Pass is { } reportPass
            ? Flag(VideoGateCheckIds.QualityReportPass, reportPass, required: true, detail: null)
            : Missing(VideoGateCheckIds.QualityReportPass, GateCheckKinds.Flag, 1));

        // 3 — the container duration against the measured narration duration (05.5.1). A narration duration of
        // zero or less cannot be judged, so it fails like an absent field, and so does a duration the report
        // could not have measured (non-finite, or beyond MaxMeasurableMagnitude) — SEC-3. The verdict compares the
        // exact drift; only the stored value is rounded.
        var tolerance = (decimal)thresholds.DurationTolerance;
        var containerSec = report?.DurationSec;
        var narrationSec = report?.TtsDurationSec;
        if (containerSec is { } container
            && narrationSec is { } narration
            && Measurable(container)
            && Measurable(narration)
            && narration > 0
            && Drift(container, narration) is { } exact)
        {
            var drift = Math.Round(exact, DriftDecimals, MidpointRounding.AwayFromZero);
            checks.Add(new GateCheck(VideoGateCheckIds.Duration, GateCheckKinds.Maximum, drift, tolerance, exact <= tolerance, null));
        }
        else
        {
            checks.Add(Missing(VideoGateCheckIds.Duration, GateCheckKinds.Maximum, tolerance));
        }

        // 4 — the integrated loudness, both ends inclusive. The recorded threshold is the deciding bound: the
        // band's lower end when the value is below it, otherwise its upper end. A loudness outside
        // MaxMeasurableMagnitude is not a measurement (SEC-3), so it fails closed like an absent field.
        var minLufs = thresholds.LoudnessMinLufs;
        var maxLufs = thresholds.LoudnessMaxLufs;
        if (report?.IntegratedLufs is { } lufs && Measurable(lufs))
        {
            checks.Add(new GateCheck(
                VideoGateCheckIds.Loudness,
                GateCheckKinds.Range,
                (decimal)lufs,
                (decimal)(lufs < minLufs ? minLufs : maxLufs),
                lufs >= minLufs && lufs <= maxLufs,
                null));
        }
        else
        {
            checks.Add(Missing(VideoGateCheckIds.Loudness, GateCheckKinds.Range, (decimal)maxLufs));
        }

        // 5 — the measured frame size against the series aspect (C14).
        var (expectedWidth, expectedHeight) = input.Aspect == Aspect.Portrait
            ? (PortraitWidth, PortraitHeight)
            : (LandscapeWidth, LandscapeHeight);
        var width = report?.Width;
        var height = report?.Height;
        if (width is { } measuredWidth && height is { } measuredHeight)
        {
            var matches = measuredWidth == expectedWidth && measuredHeight == expectedHeight;
            checks.Add(new GateCheck(
                VideoGateCheckIds.ResolutionAspect,
                GateCheckKinds.Flag,
                matches ? 1 : 0,
                1,
                matches,
                matches ? null : GateDetailCodes.ResolutionMismatch));
        }
        else
        {
            checks.Add(Missing(VideoGateCheckIds.ResolutionAspect, GateCheckKinds.Flag, 1));
        }

        // 6 — an SRT was measured when subtitles were requested.
        if (!input.SubtitlesEnabled)
        {
            checks.Add(new GateCheck(VideoGateCheckIds.SubtitlesPresent, GateCheckKinds.Flag, null, 1, true, null));
        }
        else if (report?.SubtitlesPresent is { } subtitlesPresent)
        {
            checks.Add(Flag(VideoGateCheckIds.SubtitlesPresent, subtitlesPresent, required: true, detail: null));
        }
        else
        {
            checks.Add(Missing(VideoGateCheckIds.SubtitlesPresent, GateCheckKinds.Flag, 1));
        }

        // 7 — no skipped and no moderated-away image (03 D7/CR-3): the silent case MPT creates by dropping a
        // keyword must not auto-publish.
        var skipped = report?.SkippedSceneCount;
        var moderated = report?.ModeratedImageCount;
        if (skipped is { } skippedCount && moderated is { } moderatedCount)
        {
            var failed = skippedCount + moderatedCount;
            checks.Add(new GateCheck(VideoGateCheckIds.FailedScenes, GateCheckKinds.Maximum, failed, 0, failed == 0, null));
        }
        else
        {
            checks.Add(Missing(VideoGateCheckIds.FailedScenes, GateCheckKinds.Maximum, 0));
        }

        // 8 — no openai_image moderation event for this render job.
        var events = input.ImageModerationEvents;
        checks.Add(new GateCheck(VideoGateCheckIds.ImageModeration, GateCheckKinds.Maximum, events, 0, events == 0, null));

        // 9 — the stored asset matches the report: same hash (one side may be upper-case hex) and a duration
        // above zero. A missing or unreadable asset row fails closed.
        var reportSha = report?.Sha256;
        var assetSha = input.Asset.Sha256;
        if (string.IsNullOrEmpty(reportSha)
            || string.IsNullOrEmpty(assetSha)
            || input.Asset.DurationSec is not { } assetDuration)
        {
            checks.Add(Missing(VideoGateCheckIds.AssetIntegrity, GateCheckKinds.Flag, 1));
        }
        else
        {
            var shaMatches = string.Equals(reportSha, assetSha, StringComparison.OrdinalIgnoreCase);
            var durationPositive = assetDuration > 0;
            var intact = shaMatches && durationPositive;
            checks.Add(Flag(
                VideoGateCheckIds.AssetIntegrity,
                intact,
                required: true,
                intact ? null : shaMatches ? GateDetailCodes.DurationNotPositive : GateDetailCodes.ShaMismatch));
        }

        // 10 — the advisory sync-suspect hint, only meaningful when subtitles were requested.
        if (!input.SubtitlesEnabled)
        {
            checks.Add(new GateCheck(VideoGateCheckIds.SubtitleSync, GateCheckKinds.Flag, null, 0, true, null));
        }
        else if (report is { } measuredReport)
        {
            var suspect = measuredReport.Hints.Any(hint => string.Equals(hint, SubtitleSyncSuspectHint, StringComparison.Ordinal));
            checks.Add(Flag(VideoGateCheckIds.SubtitleSync, suspect, required: false, detail: null));
        }
        else
        {
            checks.Add(Missing(VideoGateCheckIds.SubtitleSync, GateCheckKinds.Flag, 0));
        }

        var failures = checks.Where(check => !check.Pass).Select(check => check.Id).ToList();

        return new VideoGateResult(
            Pass: failures.Count == 0,
            RenderJobId: input.RenderJob.Id,
            AssetId: input.Asset.Id,
            Checks: checks,
            Failures: failures);
    }

    private static GateCheck Flag(string id, bool value, bool required, string? detail) =>
        new(id, GateCheckKinds.Flag, value ? 1 : 0, required ? 1 : 0, value == required, detail);

    // SEC-3: a number the report could not have measured — non-finite, or past the magnitude of a real duration
    // or loudness — is not a measurement. Its check fails closed with field_absent, so the (decimal) cast of a
    // poisoned stored value can never throw an OverflowException.
    private static bool Measurable(double value) =>
        double.IsFinite(value) && Math.Abs(value) <= MaxMeasurableMagnitude;

    // The exact relative duration drift, or null when the quotient is not a usable measurement: a non-finite one
    // (a denormal narration duration makes the ratio infinite) or one past the largest drift worth storing.
    private static decimal? Drift(double container, double narration)
    {
        var ratio = Math.Abs(container - narration) / narration;
        return double.IsFinite(ratio) && ratio <= MaxMeasurableMagnitude ? (decimal)ratio : null;
    }

    private static GateCheck Missing(string id, string kind, decimal threshold) =>
        new(id, kind, null, threshold, false, GateDetailCodes.FieldAbsent);
}
