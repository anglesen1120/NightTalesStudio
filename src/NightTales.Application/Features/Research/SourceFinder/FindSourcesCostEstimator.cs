using ErrorOr;
using NightTales.Application.Abstractions.Usage;
using NightTales.Domain.Common;
using NightTales.Domain.Usage;

namespace NightTales.Application.Features.Research.SourceFinder;

/// <summary>
/// <see cref="IFindSourcesCostEstimator"/> over the active <c>price_versions</c> row (02 README D14, T56): the one
/// paid finder call priced at the version's rates for the finder model, with the input bounded by
/// <c>SourceFinder:MaxInputBytes</c> and the output by the ruled 2,000, plus <c>MaxSearchCalls</c> times the
/// version's flat <c>web_search_call</c> fee (README W1/T59: $0.01 per call).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately an upper bound, in the shape of <c>FactSheetCostEstimator</c>: nothing is assumed cached, the
/// whole input is charged as a cache write as well (README D14 example C), and the byte ceiling is charged as
/// <em>tokens</em>, which is never fewer than a call's real input tokens. The bound therefore never sits below what
/// the meter settles.
/// </para>
/// <para>
/// A price version without a <c>web_search_call</c> rate cannot price this run at all. Reserving the token cost
/// alone would silently under-book every call, so the estimator fails closed instead of guessing a fee: the
/// request answers 500 and the operator re-seeds the price versions (they carry the fee from the <c>OpenAi:Pricing</c>
/// section).
/// </para>
/// </remarks>
/// <param name="catalog">The price versions.</param>
/// <param name="options">The finder model, the tool-call cap and the input ceiling.</param>
/// <param name="clock">The queue instant the active version is resolved at.</param>
public sealed class FindSourcesCostEstimator(
    IPriceCatalog catalog,
    FindSourcesCostOptions options,
    IClock clock) : IFindSourcesCostEstimator
{
    /// <summary>The finder step's model when <c>OpenAi:Models:Finder</c> is absent (02 plan §OpenAI adapter).</summary>
    public const string DefaultModel = "gpt-6-luna";

    /// <summary>The finder step's <c>max_output_tokens</c> (README T12: 2,000 for one <c>finder</c> call).</summary>
    public const int FinderMaxOutputTokens = 2_000;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The active price version carries no <c>web_search_call</c> rate, so the run cannot be priced without
    /// under-booking the fee.
    /// </exception>
    public async Task<ErrorOr<UsageBound>> WorstCaseAsync(CancellationToken ct)
    {
        var active = await catalog.GetActiveAsync(clock.UtcNow, ct).ConfigureAwait(false);
        if (active.IsError)
        {
            return active.Errors;
        }

        var rates = active.Value.ParseRates();

        // A host that bypassed FindSourcesCostOptionsValidator at start-up must still not produce a negative or a
        // below-token input bound.
        var inputBound = Math.Max(options.MaxInputBytes, 0);
        var searchCalls = Math.Max(options.MaxSearchCalls, 0);

        var tokenCost = TextCostCalculator.Compute(
            rates.RatesFor(options.Model),
            inputBound,
            cachedInputTokens: 0,
            cacheWriteTokens: inputBound,
            FinderMaxOutputTokens,
            rates.SurchargeThresholdTokens)
            ?? throw new InvalidOperationException("The calculator returned no cost for a bounded call.");

        var searchCallUsd = rates.WebSearchCallUsd
            ?? throw new InvalidOperationException(
                "The active price version carries no web_search_call rate; the source-search bound cannot be priced.");

        return new UsageBound(
            tokenCost + (searchCalls * searchCallUsd),
            inputBound,
            FinderMaxOutputTokens);
    }
}
