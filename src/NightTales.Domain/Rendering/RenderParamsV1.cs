using System.Text.Json.Serialization;

namespace NightTales.Domain.Rendering;

/// <summary>
/// <c>render_jobs.params</c> = <c>render-params.v1</c> (05 README D11): the non-content settings of one render.
/// Script, scene prompts and keyword text are never stored here (P6, P8); they are read again at submit time.
/// Built only by <c>RenderSubmitMapper.BuildParams</c> (05.1.2-T1). The members are written in the D11 order.
/// </summary>
/// <param name="MapperVersion">The mapper revision that built the document; <see cref="CurrentMapperVersion"/>.</param>
/// <param name="StoryVersionId">The approved story version the render reads its text from.</param>
/// <param name="Aspect">The series aspect wire name, <c>landscape</c> or <c>portrait</c> (C14).</param>
/// <param name="Language">The series language wire name, <c>vi</c> or <c>en</c>.</param>
/// <param name="VoiceId">The catalog voice id (04 D10); the submit sends <c>kokoro:&lt;id&gt;</c>.</param>
/// <param name="StyleTemplateId">The style template the scene prompts are built from.</param>
/// <param name="StylePromptVersion">The <c>style=&lt;name&gt;@&lt;8 hex&gt;;neg=v1</c> string (03 D1).</param>
/// <param name="ImagePlan">The counts-only image plan projection (03.2 plan §3.2).</param>
/// <param name="Subtitles">Whether subtitles are on and which preset revision applies.</param>
/// <param name="Music">The picked track, or <c>null</c> for no music (D8).</param>
/// <param name="ClipSeconds">Seconds per image (<c>video_clip_duration</c>, 03 D3).</param>
public sealed record RenderParamsV1(
    [property: JsonPropertyName("mapper_version")][property: JsonRequired] int MapperVersion,
    [property: JsonPropertyName("story_version_id")][property: JsonRequired] Guid StoryVersionId,
    [property: JsonPropertyName("aspect")][property: JsonRequired] string Aspect,
    [property: JsonPropertyName("language")][property: JsonRequired] string Language,
    [property: JsonPropertyName("voice_id")][property: JsonRequired] string VoiceId,
    [property: JsonPropertyName("style_template_id")][property: JsonRequired] Guid StyleTemplateId,
    [property: JsonPropertyName("style_prompt_version")][property: JsonRequired] string StylePromptVersion,
    [property: JsonPropertyName("image_plan")][property: JsonRequired] ImagePlanSummary ImagePlan,
    [property: JsonPropertyName("subtitles")][property: JsonRequired] SubtitlesParams Subtitles,
    [property: JsonPropertyName("music")][property: JsonRequired] MusicParams? Music,
    [property: JsonPropertyName("clip_seconds")][property: JsonRequired] int ClipSeconds)
{
    /// <summary>The value of <c>schema</c>.</summary>
    public const string SchemaName = "render-params.v1";

    /// <summary>The mapper revision written by this code.</summary>
    public const int CurrentMapperVersion = 1;

    /// <summary>The document's <c>schema</c> key; first in the written JSON.</summary>
    [JsonPropertyName("schema")]
    [JsonPropertyOrder(-1)]
    [JsonRequired]
    public string Schema { get; init; } = SchemaName;
}
