using ErrorOr;
using NightTales.Application.Abstractions.Usage;

namespace NightTales.Application.Features.Research.SourceFinder;

/// <summary>
/// The worst-case cost of one source-search run (02 README D1/D14, T56): the single paid finder call — prompt and
/// data object at the <c>SourceFinder:MaxInputBytes</c> ceiling, output at the ruled 2,000 — plus the platform fee
/// of every allow-listed <c>web_search</c> call the run may make. It is what <see cref="IBudgetGuard"/> checks and
/// what the credit reservation holds before the run row is written (02.7 plan §4 U1).
/// </summary>
public interface IFindSourcesCostEstimator
{
    /// <summary>The bound of one source search at the rates of the price version active now.</summary>
    /// <param name="ct">Cancels the price-version lookup.</param>
    /// <returns>The bound, or the catalogue's own error (<c>price_catalog_empty</c>) when no version is active.</returns>
    Task<ErrorOr<UsageBound>> WorstCaseAsync(CancellationToken ct);
}
