using ErrorOr;

namespace NightTales.Application.Abstractions.Usage;

/// <summary>
/// The worst-case cost of one bible-proposal run (02 README D1/D14, 02.6.3-T2 plan §7.1, README T56): the one
/// paid call — the <c>bible_proposal</c> step — bounded by the whole D6 data object's byte ceiling and by the
/// call's <c>max_output_tokens</c> from <c>price_versions.limits.bible_proposal</c>.
/// </summary>
/// <remarks>
/// It is what <see cref="IBudgetGuard"/> checks before the run row is written and again at the job's start (D1:
/// "before every paid dispatch"), and what the reserve holds until the run settles.
/// </remarks>
public interface IBibleProposalCostEstimator
{
    /// <summary>The bound of one bible-proposal run at the rates of the price version active now.</summary>
    /// <param name="ct">Cancels the price-version lookup.</param>
    /// <returns>
    /// The bound — whose <c>MaxOutputTokens</c> is the step's ruled <c>max_output_tokens</c>, the ceiling the
    /// generator sends — or the catalogue's own error (<c>price_catalog_empty</c>) when no version is active, or
    /// <c>prompt_not_shipped</c> when the release does not carry the proposal prompt.
    /// </returns>
    Task<ErrorOr<UsageBound>> WorstCaseAsync(CancellationToken ct);
}
