using System.Text.Json.Serialization;

namespace NightTales.Application.Rendering.Quality;

/// <summary>
/// One blocking check of the <c>render-quality.v1</c> verdict (05 README D10): the id, the measured
/// <see cref="Value"/> (or <c>null</c> when the measure is unrepresentable or the check was skipped),
/// the <see cref="Threshold"/> it was compared against, and the verdict.
/// </summary>
/// <param name="Id">The check id, from <see cref="RenderQualityEvaluator"/>'s constants; D10's order.</param>
/// <param name="Value">The measured or boolean value, encoded exactly as the D14 fixtures pin it.</param>
/// <param name="Threshold">The policy threshold the value was compared against.</param>
/// <param name="Pass">The verdict; a skipped check is <c>true</c> with a <c>null</c> value.</param>
public sealed record QualityCheck(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("value")] double? Value,
    [property: JsonPropertyName("threshold")] double? Threshold,
    [property: JsonPropertyName("pass")] bool Pass);
