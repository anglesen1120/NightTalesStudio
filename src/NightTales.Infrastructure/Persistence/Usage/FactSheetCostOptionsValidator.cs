using Microsoft.Extensions.Options;
using NightTales.Application.Features.Research.Facts;
using NightTales.Application.Features.Research.GenerateFactSheet;

namespace NightTales.Infrastructure.Persistence.Usage;

/// <summary>
/// Fails the start-up on an unusable <c>Facts</c> section as the fact-sheet <em>cost</em> estimator reads it
/// (02.5.3-T2, P7): the same key <c>FactsOptionsValidator</c> bounds for the extractor, bound once more here
/// because the Api composes <see cref="UsageServiceCollectionExtensions.AddUsageMetering"/> without the
/// extractor, and the estimator is what turns the ceiling into money.
/// </summary>
/// <remarks>
/// <para>
/// The ceiling exists because <c>FactSheetCostEstimator</c> books four times <c>Facts:MaxInputTokens</c> as
/// input bytes: an unbounded value would reserve a platform-budget bound far above any real request, and above
/// ~536M tokens the bound's token count no longer fits an <see cref="int"/>. The model must be named, or the
/// estimator silently prices the default model.
/// </para>
/// </remarks>
public sealed class FactSheetCostOptionsValidator : IValidateOptions<FactSheetCostOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, FactSheetCostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            problems.Add($"'{FactSheetCostOptions.SectionName}:Model' must name the facts model to price.");
        }

        if (options.MaxInputTokens <= 0 || options.MaxInputTokens > FactInputLimits.MaxMaxInputTokens)
        {
            problems.Add(
                $"'{FactSheetCostOptions.SectionName}:{nameof(FactSheetCostOptions.MaxInputTokens)}' must be 1..{FactInputLimits.MaxMaxInputTokens}.");
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
