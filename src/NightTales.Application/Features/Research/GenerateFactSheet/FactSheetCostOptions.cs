using NightTales.Application.Features.Research.Facts;

namespace NightTales.Application.Features.Research.GenerateFactSheet;

/// <summary>
/// The inputs <see cref="FactSheetCostEstimator"/> reads from configuration (02.5.3-T1/T2): the facts
/// step's model (<c>OpenAi:Models:Facts</c>, default <c>gpt-6-luna</c>) and the data object's ceiling
/// (<c>Facts:MaxInputTokens</c>, the same key <see cref="FactsOptions"/> binds).
/// </summary>
/// <remarks>
/// The step's <c>max_output_tokens</c> is deliberately not here: README T56 rules that the runtime reads it
/// from <c>price_versions.limits</c> with no config fallback, so
/// <see cref="FactSheetCostEstimator.MaxOutputTokens"/> is the fixed 4,000 of that ruling.
/// </remarks>
public sealed class FactSheetCostOptions
{
    /// <summary>The configuration section name; the model is overridden from <c>OpenAi:Models:Facts</c>.</summary>
    public const string SectionName = "Facts";

    /// <summary>The model whose rates price one facts call.</summary>
    public string Model { get; set; } = FactSheetCostEstimator.DefaultModel;

    /// <summary>The estimated-token ceiling of the serialised data object (<c>Facts:MaxInputTokens</c>).</summary>
    public int MaxInputTokens { get; set; } = FactInputLimits.DefaultMaxInputTokens;
}
