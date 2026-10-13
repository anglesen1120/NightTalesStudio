using ErrorOr;
using NightTales.Application.Abstractions.Usage;
using NightTales.Domain.Common;
using NightTales.Domain.Usage;

namespace NightTales.Application.Features.Research.GenerateFactSheet;

/// <summary>
/// <see cref="IFactSheetCostEstimator"/> over the active <c>price_versions</c> row (02 README D14, T56): the one
/// paid call of a fact-sheet run — the <c>facts</c> extraction — priced at the version's rates for the facts
/// model, with its input bounded by <c>Facts:MaxInputTokens</c> and its output by the ruled 4,000.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately an upper bound, in the shape of <c>StoryCostEstimator</c>: nothing is assumed cached, and the
/// input is bounded by <em>bytes</em>. <c>FactInputBudget</c> bounds the serialised data object at
/// <c>Facts:MaxInputTokens</c> <em>estimated tokens</em>, where the estimate is the UTF-8 byte count divided by
/// four, so the byte ceiling of the object — and therefore a bound on the call's real input tokens, which are
/// never more than its bytes — is four times that ceiling. The bound charges the whole input as a cache write
/// too (02 README D14 example C), exactly as the story steps do, so it never sits below what the meter settles.
/// </para>
/// <para>
/// The facts step's <c>max_output_tokens</c> comes from <c>price_versions.limits</c> per README T56; the seeded
/// limits document carries the <c>story</c> block only today, so the estimator uses the T56 value
/// (<see cref="MaxOutputTokens"/>) until a <c>facts</c> block lands (02.8.1 / 02.6.3).
/// </para>
/// </remarks>
/// <param name="catalog">The price versions.</param>
/// <param name="options">The facts model and the input ceiling.</param>
/// <param name="clock">The queue instant the active version is resolved at.</param>
public sealed class FactSheetCostEstimator(
    IPriceCatalog catalog,
    FactSheetCostOptions options,
    IClock clock) : IFactSheetCostEstimator
{
    /// <summary>The facts step's model when <c>OpenAi:Models:Facts</c> is absent (02 plan §OpenAI adapter).</summary>
    public const string DefaultModel = "gpt-6-luna";

    /// <summary>The facts step's <c>max_output_tokens</c> (README §10 T56: 4,000 for one <c>facts</c> call).</summary>
    public const int MaxOutputTokens = 4_000;

    /// <summary>
    /// The byte ceiling of one estimated token: <c>FactInputBudget</c> estimates a call's input as its UTF-8
    /// byte count divided by four, so four times the token ceiling is the object's byte bound.
    /// </summary>
    private const long BytesPerEstimatedToken = 4;

    /// <inheritdoc />
    public async Task<ErrorOr<UsageBound>> WorstCaseAsync(CancellationToken ct)
    {
        var active = await catalog.GetActiveAsync(clock.UtcNow, ct).ConfigureAwait(false);
        if (active.IsError)
        {
            return active.Errors;
        }

        var rates = active.Value.ParseRates();
        var inputBound = BytesPerEstimatedToken * options.MaxInputTokens;

        var cost = TextCostCalculator.Compute(
            rates.RatesFor(options.Model),
            inputBound,
            cachedInputTokens: 0,
            cacheWriteTokens: inputBound,
            MaxOutputTokens,
            rates.SurchargeThresholdTokens)
            ?? throw new InvalidOperationException("The calculator returned no cost for a bounded call.");

        return new UsageBound(cost, checked((int)inputBound), MaxOutputTokens);
    }
}
