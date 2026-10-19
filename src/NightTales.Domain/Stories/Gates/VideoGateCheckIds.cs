namespace NightTales.Domain.Stories.Gates;

/// <summary>
/// The video gate's check ids (02.8.2 plan §3.2), in evaluation order, over the finished render's
/// <c>quality_report</c>, its <c>assets</c> row and the episode's series aspect. One source for
/// <c>gate_results</c>, the run-log copy keys and the tests.
/// </summary>
public static class VideoGateCheckIds
{
    /// <summary>1: <c>render_jobs.quality_report</c> is present.</summary>
    public const string QualityReportPresent = "quality_report_present";

    /// <summary>2: <c>quality_report.pass</c> (defence in depth; 09.6.3 already ends a failed render).</summary>
    public const string QualityReportPass = "quality_report_pass";

    /// <summary>3: the container duration against the narration duration.</summary>
    public const string Duration = "duration";

    /// <summary>4: the integrated loudness inside the accepted band.</summary>
    public const string Loudness = "loudness";

    /// <summary>5: the frame size against <c>series.aspect</c> (C14).</summary>
    public const string ResolutionAspect = "resolution_aspect";

    /// <summary>6: an SRT was measured, when subtitles were requested.</summary>
    public const string SubtitlesPresent = "subtitles_present";

    /// <summary>7: no image was skipped and no image was moderated away (03 D7/CR-3).</summary>
    public const string FailedScenes = "failed_scenes";

    /// <summary>8: no <c>openai_image</c> moderation event for this render job.</summary>
    public const string ImageModeration = "image_moderation";

    /// <summary>9: the stored asset matches the report's SHA-256 and has a duration (row missing → fail).</summary>
    public const string AssetIntegrity = "asset_integrity";

    /// <summary>10: the advisory <c>subtitle_sync_suspect</c> hint is absent, when subtitles were requested.</summary>
    public const string SubtitleSync = "subtitle_sync";

    /// <summary>The ten ids in plan §3.2 order; 6 and 10 only apply when subtitles are enabled.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        QualityReportPresent,
        QualityReportPass,
        Duration,
        Loudness,
        ResolutionAspect,
        SubtitlesPresent,
        FailedScenes,
        ImageModeration,
        AssetIntegrity,
        SubtitleSync,
    ];
}
