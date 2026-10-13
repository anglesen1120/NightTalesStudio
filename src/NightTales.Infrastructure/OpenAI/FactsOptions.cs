using NightTales.Application.Features.Research.Facts;

namespace NightTales.Infrastructure.OpenAI;

/// <summary>
/// The <c>Facts</c> section of the fact-extraction step (02.5.3-T1): the input budget and the fact ceiling.
/// </summary>
/// <remarks>
/// Every key has an in-code default, so a host without the block still works; the section only overrides.
/// The per-step model, effort and timeout of the facts call are the <c>OpenAi</c> section's
/// (<c>OpenAi:Models:Facts</c>, <c>OpenAi:Effort:Facts</c>, <c>OpenAi:RequestTimeout:Facts</c>), not repeated
/// here. The call's <c>max_output_tokens</c> is not a key either: the caller supplies it from the price
/// version (README T56).
/// </remarks>
public sealed class FactsOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Facts";

    /// <summary>The smallest <c>MaxFacts</c> the validator accepts.</summary>
    public const int MinMaxFacts = 5;

    /// <summary>The largest <c>MaxFacts</c> the validator accepts; also the <c>facts.v1</c> <c>maxItems</c>.</summary>
    public const int MaxMaxFacts = 40;

    /// <summary>The default of <see cref="MaxFacts"/>.</summary>
    public const int DefaultMaxFacts = MaxMaxFacts;

    /// <summary>How many sources one extraction may send; at most <see cref="FactInputLimits.MaxSourcesCeiling"/> (<c>s1…s10</c>).</summary>
    public int MaxSources { get; set; } = FactInputLimits.MaxSourcesCeiling;

    /// <summary>The per-source character cap before the total budget is applied.</summary>
    public int PerSourceCharCap { get; set; } = FactInputLimits.DefaultPerSourceCharCap;

    /// <summary>The estimated-token ceiling of the serialised data object.</summary>
    public int MaxInputTokens { get; set; } = FactInputLimits.DefaultMaxInputTokens;

    /// <summary>The fact ceiling the validator (02.5.3-T2) enforces and the schema's <c>maxItems</c> mirrors.</summary>
    public int MaxFacts { get; set; } = DefaultMaxFacts;

    /// <summary>The input bounds this section states, as the input budget reads them.</summary>
    /// <returns>The <see cref="FactInputLimits"/> of <see cref="MaxSources"/>, <see cref="PerSourceCharCap"/> and <see cref="MaxInputTokens"/>.</returns>
    public FactInputLimits ToLimits() => new(MaxSources, PerSourceCharCap, MaxInputTokens);
}
