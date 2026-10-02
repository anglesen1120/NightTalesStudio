namespace NightTales.Application.Rendering.Quality;

/// <summary>
/// Every threshold of the <c>render-quality.v1</c> verdict (05 README D10), bound from the
/// <c>RenderQuality</c> configuration section and validated at startup. The values marked <c>[target]</c>
/// (the loudness band, silence, coverage, the sync-suspect share) are calibrated by 05.2.1-T1 / 05.5.2-T1;
/// that is exactly why they are configuration and not constants — a calibration changes the section, not
/// the evaluator.
/// </summary>
/// <remarks>
/// The section key names beyond the <c>RenderQuality</c> prefix are [INFERENCE] (the README names the
/// prefix only), so renaming one is free until 09's config binding lands. A value outside its declared
/// range fails DI validation at startup (the validator lives in the Infrastructure composition, so this
/// layer keeps its ErrorOr-only reference set), never at evaluation time — a policy that cannot mean
/// anything must not silently pass videos.
/// </remarks>
public sealed record RenderQualityPolicy
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "RenderQuality";

    /// <summary>The largest allowed <c>|duration − narration| ÷ narration</c> (D10; 5 %).</summary>
    public double MaxDurationDrift { get; init; } = 0.05;

    /// <summary>The bottom of the accepted integrated-loudness band, in LUFS.</summary>
    public double MinLufs { get; init; } = -16;

    /// <summary>The top of the accepted integrated-loudness band, in LUFS.</summary>
    public double MaxLufs { get; init; } = -14;

    /// <summary>The highest accepted true peak, in dBTP.</summary>
    public double MaxTruePeakDbtp { get; init; } = -1.0;

    /// <summary>The longest accepted silence, in seconds.</summary>
    public double MaxLongestSilenceSeconds { get; init; } = 10;

    /// <summary>The smallest accepted subtitle coverage share (0–1).</summary>
    public double MinSubtitleCoverage { get; init; } = 0.50;

    /// <summary>The fewest cues a subtitled render must carry.</summary>
    public int MinCues { get; init; } = 1;

    /// <summary>The sync-suspect share above which the advisory hint is raised.</summary>
    public double SyncSuspectHintShare { get; init; } = 0.10;

    /// <summary>The policy revision stored in the verdict, so a stored report says which policy read it.</summary>
    public int PolicyVersion { get; init; } = 1;
}

