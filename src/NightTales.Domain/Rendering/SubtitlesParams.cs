using System.Text.Json.Serialization;

namespace NightTales.Domain.Rendering;

/// <summary>The <c>subtitles</c> block of <c>render-params.v1</c> (05 README D11).</summary>
/// <param name="Enabled">Whether subtitles are burned in; always <c>true</c> in phase P.</param>
/// <param name="Preset">The preset in force, <c>&lt;aspect&gt;@&lt;revision&gt;</c>, e.g. <c>landscape@1</c>.</param>
public sealed record SubtitlesParams(
    [property: JsonPropertyName("enabled")][property: JsonRequired] bool Enabled,
    [property: JsonPropertyName("preset")][property: JsonRequired] string Preset);
