using ErrorOr;

namespace NightTales.Application.Abstractions.Usage;

/// <summary>
/// The worst-case cost of one fact-sheet extraction run (02 README D1/D14, T56): the single paid
/// <c>facts</c> call, bounded by the data object's input ceiling and the call's <c>max_output_tokens</c>.
/// It is what <see cref="IBudgetGuard"/> checks and what the credit reservation holds before the run
/// row is written (02.5.3 plan §4.1).
/// </summary>
public interface IFactSheetCostEstimator
{
    /// <summary>The bound of one fact-sheet run at the rates of the price version active now.</summary>
    /// <param name="ct">Cancels the price-version lookup.</param>
    /// <returns>The bound, or the catalogue's own error (<c>price_catalog_empty</c>) when no version is active.</returns>
    Task<ErrorOr<UsageBound>> WorstCaseAsync(CancellationToken ct);
}
