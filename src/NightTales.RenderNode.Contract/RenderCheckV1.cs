using System.Text.Json.Serialization;

namespace NightTales.RenderNode.Contract;

/// <summary>
/// The <c>render-check.v1</c> report of 05.4.1-T2 §5 and 05 D9, measured on the node by
/// <c>worker-node/checks/render_check.py</c>. "The tool measures, the backend decides" (F20): this type
/// carries measurements only and is never interpreted here. Every block except <see cref="Input"/> is
/// nullable — a non-representable measure is null, never a non-finite number (§10 T65).
/// </summary>
public sealed record RenderCheckV1(
    [property: JsonPropertyName("schema")][property: JsonRequired] string Schema,
    [property: JsonPropertyName("tool_version")][property: JsonRequired] string ToolVersion,
    [property: JsonPropertyName("ffmpeg_version")][property: JsonRequired] string FfmpegVersion,
    [property: JsonPropertyName("input")][property: JsonRequired] RenderCheckInputV1 Input,
    [property: JsonPropertyName("output")][property: JsonRequired] RenderCheckOutputV1? Output,
    [property: JsonPropertyName("container")][property: JsonRequired] RenderCheckContainerV1? Container,
    [property: JsonPropertyName("video")][property: JsonRequired] RenderCheckVideoV1? Video,
    [property: JsonPropertyName("audio")][property: JsonRequired] RenderCheckAudioV1? Audio,
    [property: JsonPropertyName("narration")][property: JsonRequired] RenderCheckNarrationV1? Narration,
    [property: JsonPropertyName("loudness")][property: JsonRequired] RenderCheckLoudnessV1? Loudness,
    [property: JsonPropertyName("silence")][property: JsonRequired] RenderCheckSilenceV1? Silence,
    [property: JsonPropertyName("subtitles")][property: JsonRequired] RenderCheckSubtitlesV1? Subtitles,
    [property: JsonPropertyName("warnings")][property: JsonRequired] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("errors")][property: JsonRequired] IReadOnlyList<string> Errors);

/// <summary>The file that was measured.</summary>
public sealed record RenderCheckInputV1(
    [property: JsonPropertyName("file")][property: JsonRequired] string File,
    [property: JsonPropertyName("bytes")][property: JsonRequired] long Bytes,
    [property: JsonPropertyName("sha256")][property: JsonRequired] string Sha256);

/// <summary>The normalised file that is served as the render output (§10 T60).</summary>
public sealed record RenderCheckOutputV1(
    [property: JsonPropertyName("file")][property: JsonRequired] string File,
    [property: JsonPropertyName("bytes")][property: JsonRequired] long Bytes,
    [property: JsonPropertyName("sha256")][property: JsonRequired] string Sha256,
    [property: JsonPropertyName("normalized")][property: JsonRequired] bool Normalized);

public sealed record RenderCheckContainerV1(
    [property: JsonPropertyName("format")][property: JsonRequired] string Format,
    [property: JsonPropertyName("duration_s")][property: JsonRequired] double DurationS,
    [property: JsonPropertyName("faststart")][property: JsonRequired] bool Faststart);

public sealed record RenderCheckVideoV1(
    [property: JsonPropertyName("codec")][property: JsonRequired] string Codec,
    [property: JsonPropertyName("pix_fmt")][property: JsonRequired] string PixFmt,
    [property: JsonPropertyName("width")][property: JsonRequired] int Width,
    [property: JsonPropertyName("height")][property: JsonRequired] int Height,
    [property: JsonPropertyName("fps")][property: JsonRequired] double Fps);

public sealed record RenderCheckAudioV1(
    [property: JsonPropertyName("codec")][property: JsonRequired] string Codec,
    [property: JsonPropertyName("sample_rate")][property: JsonRequired] int SampleRate,
    [property: JsonPropertyName("channels")][property: JsonRequired] int Channels);

public sealed record RenderCheckNarrationV1(
    [property: JsonPropertyName("duration_s")][property: JsonRequired] double DurationS);

/// <summary>Before/after loudness passes; a silent input is <c>before = after = null</c> plus <c>audio_silent</c>.</summary>
public sealed record RenderCheckLoudnessV1(
    [property: JsonPropertyName("before")][property: JsonRequired] RenderCheckLoudnessMeasureV1? Before,
    [property: JsonPropertyName("after")][property: JsonRequired] RenderCheckLoudnessMeasureV1? After,
    [property: JsonPropertyName("normalization_type")][property: JsonRequired] string? NormalizationType);

public sealed record RenderCheckLoudnessMeasureV1(
    [property: JsonPropertyName("integrated_lufs")][property: JsonRequired] double? IntegratedLufs,
    [property: JsonPropertyName("true_peak_dbtp")][property: JsonRequired] double? TruePeakDbtp,
    [property: JsonPropertyName("lra")][property: JsonRequired] double? Lra);

public sealed record RenderCheckSilenceV1(
    [property: JsonPropertyName("longest_s")][property: JsonRequired] double LongestS,
    [property: JsonPropertyName("total_s")][property: JsonRequired] double TotalS);

/// <summary>
/// Subtitle measures; <see cref="TextMatchesScript"/> is null when the checker ran with no script
/// (05.5.1-T2 reads null as "not comparable"), and <see cref="ZeroDuration"/> is the whisper fail-open
/// signal (§10 T55).
/// </summary>
public sealed record RenderCheckSubtitlesV1(
    [property: JsonPropertyName("present")][property: JsonRequired] bool Present,
    [property: JsonPropertyName("cues")][property: JsonRequired] int Cues,
    [property: JsonPropertyName("zero_duration")][property: JsonRequired] int ZeroDuration,
    [property: JsonPropertyName("overlapping")][property: JsonRequired] int Overlapping,
    [property: JsonPropertyName("non_monotonic")][property: JsonRequired] int NonMonotonic,
    [property: JsonPropertyName("out_of_range")][property: JsonRequired] int OutOfRange,
    [property: JsonPropertyName("coverage_share")][property: JsonRequired] double CoverageShare,
    [property: JsonPropertyName("max_cue_chars")][property: JsonRequired] int MaxCueChars,
    [property: JsonPropertyName("long_cues")][property: JsonRequired] int LongCues,
    [property: JsonPropertyName("cps_p95")][property: JsonRequired] double CpsP95,
    [property: JsonPropertyName("sync_suspect")][property: JsonRequired] int SyncSuspect,
    [property: JsonPropertyName("text_matches_script")][property: JsonRequired] bool? TextMatchesScript,
    [property: JsonPropertyName("text_mismatch_cues")][property: JsonRequired] int TextMismatchCues);
