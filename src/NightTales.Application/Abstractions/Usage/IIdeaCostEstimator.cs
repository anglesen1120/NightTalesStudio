using ErrorOr;

namespace NightTales.Application.Abstractions.Usage;

/// <summary>
/// The worst-case cost of one episode-idea call (02 README D1/D14, 02.8.1-T1 plan §7.1): the one paid dispatch,
/// bounded by the whole data object's byte ceiling and by the call's <c>max_output_tokens</c> from the run's own
/// <c>price_versions.limits.ideas</c>.
/// </summary>
/// <remarks>
/// <para>
/// It is the bound <see cref="IBudgetGuard"/> checks before the retry, which is the run's <em>second</em> paid
/// dispatch: the caller's admission reserved one call, so the planner re-checks the platform cap with a bound of
/// its own before spending the second one (D1: "before every paid dispatch").
/// </para>
/// <para>
/// The version is the run's own <c>RunContext.PriceVersion</c>, never the version active at check time, so
/// the cap the retry is admitted under is the cap the request sends and the usage row is written at.
/// </para>
/// </remarks>
public interface IIdeaCostEstimator
{
    /// <summary>The bound of one idea call at the rates and the limits of <paramref name="priceVersion"/>.</summary>
    /// <param name="priceVersion">The run's <c>price_versions.version</c> as text (<c>RunContext.PriceVersion</c>).</param>
    /// <param name="ct">Cancels the price-version lookup.</param>
    /// <returns>
    /// The bound — whose <c>MaxOutputTokens</c> is the step's ruled <c>max_output_tokens</c>, the ceiling the
    /// generator sends — or <c>price_catalog_empty</c> when the run names no usable version, or
    /// <c>prompt_not_shipped</c> when the release does not carry the step prompt.
    /// </returns>
    Task<ErrorOr<UsageBound>> WorstCaseAsync(string priceVersion, CancellationToken ct);
}
