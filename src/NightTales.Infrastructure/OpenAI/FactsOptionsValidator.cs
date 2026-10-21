using Microsoft.Extensions.Options;
using NightTales.Application.Features.Research.Facts;

namespace NightTales.Infrastructure.OpenAI;

/// <summary>
/// Fails the start-up on an unusable <c>Facts</c> section (02.5.3-T1): a source ceiling the alias scheme
/// cannot express, a fact ceiling outside the schema's bounds, or an input ceiling outside the bound the cost
/// estimator can price (02.5.3-T2, P7).
/// </summary>
public sealed class FactsOptionsValidator : IValidateOptions<FactsOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, FactsOptions options)
    {
        var problems = new List<string>();

        if (options.MaxSources < 1 || options.MaxSources > FactInputLimits.MaxSourcesCeiling)
        {
            problems.Add($"'{FactsOptions.SectionName}:{nameof(FactsOptions.MaxSources)}' must be 1..{FactInputLimits.MaxSourcesCeiling} (aliases s1..s10).");
        }

        if (options.PerSourceCharCap <= 0)
        {
            problems.Add($"'{FactsOptions.SectionName}:{nameof(FactsOptions.PerSourceCharCap)}' must be positive.");
        }

        if (options.MaxInputTokens <= 0 || options.MaxInputTokens > FactInputLimits.MaxMaxInputTokens)
        {
            problems.Add(
                $"'{FactsOptions.SectionName}:{nameof(FactsOptions.MaxInputTokens)}' must be 1..{FactInputLimits.MaxMaxInputTokens}.");
        }

        if (options.MaxFacts < FactsOptions.MinMaxFacts || options.MaxFacts > FactsOptions.MaxMaxFacts)
        {
            problems.Add($"'{FactsOptions.SectionName}:{nameof(FactsOptions.MaxFacts)}' must be {FactsOptions.MinMaxFacts}..{FactsOptions.MaxMaxFacts}.");
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
