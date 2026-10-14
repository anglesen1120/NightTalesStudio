using NightTales.Domain.Rendering;
using NightTales.Domain.Rendering.Music;

namespace NightTales.Application.Features.Rendering;

/// <summary>
/// What 09.6.1-T1 hands to <see cref="RenderSubmitMapper.BuildParams"/>: the non-content render settings of
/// one job (05 README D11) plus the two values D11 does not own — the picker's <see cref="Music"/> and the
/// catalog attribution frozen next to it (D8, T9).
/// </summary>
/// <remarks>
/// The record carries no content: the story text and the scene keywords arrive later, through
/// <see cref="RenderSubmitMapper.BuildSubmit"/> and <c>StoryRenderInput</c> (P6).
/// </remarks>
/// <param name="StoryVersionId">The approved story version the render reads its text from.</param>
/// <param name="Aspect">The series aspect wire name, <c>landscape</c> or <c>portrait</c> (C14).</param>
/// <param name="Language">The series language wire name, <c>vi</c> or <c>en</c>.</param>
/// <param name="VoiceId">The catalog voice id (04 D10); the submit sends <c>kokoro:&lt;id&gt;</c>.</param>
/// <param name="StyleTemplateId">The style template the scene prompts are built from.</param>
/// <param name="StylePromptVersion">The <c>style=&lt;name&gt;@&lt;8 hex&gt;;neg=v1</c> string (03 D1).</param>
/// <param name="ImagePlan">The counts-only plan projection; also the source of the submit's <c>image_plan</c>.</param>
/// <param name="Subtitles">Whether subtitles are on and which preset revision applies,
/// <c>&lt;aspect&gt;@&lt;revision&gt;</c> (D6/D11).</param>
/// <param name="Music">The picked track, or <c>null</c> for no music (D8).</param>
/// <param name="MusicAttribution">The picked track's catalog attribution, frozen at render time; <c>null</c>
/// unless the track is <c>CC-BY-4.0</c> (D8, T9).</param>
/// <param name="ClipSeconds">Seconds per image; <c>10</c> in phase P (03 D3).</param>
public sealed record RenderParamsInput(
    Guid StoryVersionId,
    string Aspect,
    string Language,
    string VoiceId,
    Guid StyleTemplateId,
    string StylePromptVersion,
    ImagePlanSummary ImagePlan,
    SubtitlesParams Subtitles,
    MusicChoice? Music,
    string? MusicAttribution,
    int ClipSeconds);
