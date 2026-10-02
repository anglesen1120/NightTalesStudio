using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightTales.RenderNode.Contract;

/// <summary>
/// The MPT field set of 05.4.1-T2 §3 (05 D3): every field the backend sends, with the fixed types and
/// names of the frozen contract, in table order. <c>n_threads</c> is deliberately absent — the adapter
/// injects it from its own configuration (05 D15 P6, F22).
/// <para>
/// Every member is <see cref="JsonRequiredAttribute"/>: the key set is exactly this type minus nothing,
/// so an extra or a missing key is a contract violation (05 D3 group 1).
/// </para>
/// <para>
/// The decimals (<c>video_clip_speed</c>, <c>voice_volume</c>, <c>voice_rate</c>, <c>bgm_volume</c>,
/// <c>custom_position</c>, <c>stroke_width</c>) are <see cref="decimal"/> on purpose: the D14 fixtures
/// carry <c>70.0</c>, <c>1.0</c> and <c>0</c>, and only a decimal keeps the written scale byte-identical
/// for 05.1.2-T1's golden comparison.
/// </para>
/// </summary>
public sealed record MptSubmitFields(
    [property: JsonPropertyName("video_subject")][property: JsonRequired] string VideoSubject,
    [property: JsonPropertyName("video_script")][property: JsonRequired] string VideoScript,
    [property: JsonPropertyName("video_terms")][property: JsonRequired] IReadOnlyList<string> VideoTerms,
    [property: JsonPropertyName("video_aspect")][property: JsonRequired] string VideoAspect,
    [property: JsonPropertyName("video_fit_mode")][property: JsonRequired] string VideoFitMode,
    [property: JsonPropertyName("video_concat_mode")][property: JsonRequired] string VideoConcatMode,
    [property: JsonPropertyName("video_transition_mode")][property: JsonRequired] string? VideoTransitionMode,
    [property: JsonPropertyName("video_clip_duration")][property: JsonRequired] int VideoClipDuration,
    [property: JsonPropertyName("video_clip_speed")][property: JsonRequired] decimal VideoClipSpeed,
    [property: JsonPropertyName("match_materials_to_script")][property: JsonRequired] bool MatchMaterialsToScript,
    [property: JsonPropertyName("video_count")][property: JsonRequired] int VideoCount,
    [property: JsonPropertyName("video_source")][property: JsonRequired] string VideoSource,
    [property: JsonPropertyName("video_materials")][property: JsonRequired] JsonElement? VideoMaterials,
    [property: JsonPropertyName("custom_audio_file")][property: JsonRequired] string? CustomAudioFile,
    [property: JsonPropertyName("video_language")][property: JsonRequired] string VideoLanguage,
    [property: JsonPropertyName("voice_name")][property: JsonRequired] string VoiceName,
    [property: JsonPropertyName("voice_volume")][property: JsonRequired] decimal VoiceVolume,
    [property: JsonPropertyName("voice_rate")][property: JsonRequired] decimal VoiceRate,
    [property: JsonPropertyName("bgm_type")][property: JsonRequired] string BgmType,
    [property: JsonPropertyName("bgm_file")][property: JsonRequired] string BgmFile,
    [property: JsonPropertyName("bgm_volume")][property: JsonRequired] decimal BgmVolume,
    [property: JsonPropertyName("video_music_prompt")][property: JsonRequired] string VideoMusicPrompt,
    [property: JsonPropertyName("sonilo_bgm_prompt")][property: JsonRequired] string SoniloBgmPrompt,
    [property: JsonPropertyName("subtitle_enabled")][property: JsonRequired] bool SubtitleEnabled,
    [property: JsonPropertyName("subtitle_display_mode")][property: JsonRequired] string SubtitleDisplayMode,
    [property: JsonPropertyName("subtitle_animation")][property: JsonRequired] string SubtitleAnimation,
    [property: JsonPropertyName("subtitle_position")][property: JsonRequired] string SubtitlePosition,
    [property: JsonPropertyName("custom_position")][property: JsonRequired] decimal CustomPosition,
    [property: JsonPropertyName("font_size")][property: JsonRequired] int FontSize,
    [property: JsonPropertyName("stroke_color")][property: JsonRequired] string StrokeColor,
    [property: JsonPropertyName("stroke_width")][property: JsonRequired] decimal StrokeWidth,
    [property: JsonPropertyName("text_fore_color")][property: JsonRequired] string TextForeColor,
    [property: JsonPropertyName("text_background_color")][property: JsonRequired] bool TextBackgroundColor,
    [property: JsonPropertyName("rounded_subtitle_background")][property: JsonRequired] bool RoundedSubtitleBackground,
    [property: JsonPropertyName("font_name")][property: JsonRequired] string FontName,
    [property: JsonPropertyName("paragraph_number")][property: JsonRequired] int ParagraphNumber,
    [property: JsonPropertyName("video_script_prompt")][property: JsonRequired] string VideoScriptPrompt,
    [property: JsonPropertyName("custom_system_prompt")][property: JsonRequired] string CustomSystemPrompt);
