using System.Text.Json.Serialization;

namespace NightTales.Application.Rendering.Quality;

/// <summary>
/// The subtitle block of the <c>render-quality.v1</c> verdict (05 README D10): the counts 02.8.2's video
/// gate and the review UI read, plus the derived sync-suspect share the <c>subtitle_sync_suspect</c>
/// hint is computed from.
/// </summary>
/// <param name="Cues">The number of cues in the SRT (0 with no SRT).</param>
/// <param name="CoverageShare">The share of the video covered by cues (0–1).</param>
/// <param name="SyncSuspectShare"><c>sync_suspect ÷ max(cues, 1)</c>, rounded to three decimals.</param>
/// <param name="LongCues">The cues longer than the preset's <c>maxCharsPerLine × maxLines</c>.</param>
/// <param name="TextMatchesScript">Whether every cue matched the script; <c>null</c> when it could not be checked.</param>
public sealed record RenderQualitySubtitles(
    [property: JsonPropertyName("cues")] int Cues,
    [property: JsonPropertyName("coverage_share")] double CoverageShare,
    [property: JsonPropertyName("sync_suspect_share")] double SyncSuspectShare,
    [property: JsonPropertyName("long_cues")] int LongCues,
    [property: JsonPropertyName("text_matches_script")] bool? TextMatchesScript);
