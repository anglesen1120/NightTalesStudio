using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Abstractions.Usage;
using NightTales.Domain.Series;

namespace NightTales.Application.Features.Rendering;

/// <summary>
/// <see cref="IImageCostEstimator"/> over <c>price_versions.rates.image</c> (03 README D8). When the version prices
/// <see cref="ImagesOptions.Model"/> per image (02 CR-27: 184 VND ≈ $0.0072 flat) that price is the per-image cost.
/// Before the image block exists (03.3.1-T2) the bound falls back to the pessimistic token estimate
/// <c>tokens × image_out + 200 × text_in</c> at <see cref="ImagesOptions.PlaceholderTokensPerImage"/>, a high but safe figure.
/// </summary>
/// <param name="catalog">The price versions.</param>
/// <param name="options">The bound <c>Images</c> section.</param>
public sealed class ImageCostEstimator(IPriceCatalog catalog, ImagesOptions options) : IImageCostEstimator
{
    private const decimal PerMillion = 1_000_000m;

    /// <inheritdoc />
    public async Task<CostBound> WorstCaseAsync(Aspect aspect, int maxTerms, int priceVersion, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxTerms);

        var version = await catalog.GetAsync(priceVersion, ct).ConfigureAwait(false);
        var perImage = version.ParseRates().Image.TryGetValue(options.Model, out var price)
            ? price.PerImageUsd
            : PlaceholderPerImage();

        return new CostBound(maxTerms * perImage);
    }

    private decimal PlaceholderPerImage() =>
        (options.PlaceholderTokensPerImage * options.PlaceholderImageOutPer1MUsd / PerMillion)
        + (ImagesOptions.PromptTextTokens * options.PlaceholderTextInPer1MUsd / PerMillion);
}
