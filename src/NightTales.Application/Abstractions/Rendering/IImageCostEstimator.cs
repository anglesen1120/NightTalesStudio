using NightTales.Application.Abstractions.Usage;
using NightTales.Domain.Series;

namespace NightTales.Application.Abstractions.Rendering;

/// <summary>
/// The worst-case image cost of one render (03 README D8, CR-4): what <c>IBudgetGuard</c> checks before
/// dispatch and before each auto-retry. Every planned term may be called once (I7).
/// </summary>
public interface IImageCostEstimator
{
    /// <summary>The bound of <paramref name="maxTerms"/> images at the rates of one price version.</summary>
    /// <param name="aspect">The series aspect; keys the measured token cell (<c>landscape:auto</c>) once rates carry one.</param>
    /// <param name="maxTerms"><c>RenderImagePlan.MaxTerms</c>.</param>
    /// <param name="priceVersion">The <c>price_versions.version</c> the render runs under.</param>
    /// <param name="ct">Cancels the lookup.</param>
    /// <returns>The bound.</returns>
    /// <exception cref="KeyNotFoundException">No such price version.</exception>
    Task<CostBound> WorstCaseAsync(Aspect aspect, int maxTerms, int priceVersion, CancellationToken ct);
}
