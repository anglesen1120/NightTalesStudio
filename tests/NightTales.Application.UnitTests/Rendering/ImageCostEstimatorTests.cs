using System.Text.Json;
using ErrorOr;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Rendering;
using NightTales.Domain.Rendering.Images;
using NightTales.Domain.Series;
using NightTales.Domain.Usage;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Rendering;

/// <summary>
/// <c>IImageCostEstimator</c>, <c>ImagesOptions</c> validation and <c>ImagePlanJson</c> (03 README D8/D9, 03.2.1-T1):
/// the bound is <c>maxTerms × per-image price</c> - the version's flat per-image price when it has one, the 6,000-token
/// placeholder otherwise - and the stored plan document never carries keyword text.
/// </summary>
public sealed class ImageCostEstimatorTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<string, ImagePriceRate> NoImage = [];

    [Fact]
    public async Task WorstCase_UsesP95Cell_ElsePlaceholder()
    {
        // No image block in the version yet: maxTerms × (6,000 × 30 + 200 × 5) / 1e6.
        var sut = new ImageCostEstimator(new FakeCatalog(NoImage), new ImagesOptions());

        var bound = await sut.WorstCaseAsync(Aspect.Landscape, 6, 1, TestContext.Current.CancellationToken);

        bound.WorstCaseUsd.ShouldBe(6 * ((6_000m * 30m) + (200m * 5m)) / 1_000_000m);
        bound.WorstCaseUsd.ShouldBe(1.086m);
    }

    [Fact]
    public async Task WorstCase_PricedImage_UsesTheVersionsPerImagePrice()
    {
        var image = new Dictionary<string, ImagePriceRate> { ["gpt-image-2"] = new("per_image", 0.007176m) };
        var sut = new ImageCostEstimator(new FakeCatalog(image), new ImagesOptions());

        var bound = await sut.WorstCaseAsync(Aspect.Portrait, 48, 1, TestContext.Current.CancellationToken);

        bound.WorstCaseUsd.ShouldBe(48 * 0.007176m, "Standard worst case = 48 images at the CR-27 flat price");
    }

    [Fact]
    public async Task WorstCase_OtherModelPriced_FallsBackToPlaceholder()
    {
        var image = new Dictionary<string, ImagePriceRate> { ["some-other-model"] = new("per_image", 0.5m) };
        var sut = new ImageCostEstimator(new FakeCatalog(image), new ImagesOptions());

        var bound = await sut.WorstCaseAsync(Aspect.Landscape, 1, 1, TestContext.Current.CancellationToken);

        bound.WorstCaseUsd.ShouldBe(0.181m);
    }

    [Fact]
    public async Task WorstCase_ScalesLinearlyWithMaxTerms()
    {
        var sut = new ImageCostEstimator(new FakeCatalog(NoImage), new ImagesOptions());
        var ct = TestContext.Current.CancellationToken;

        var one = await sut.WorstCaseAsync(Aspect.Landscape, 1, 1, ct);
        var six = await sut.WorstCaseAsync(Aspect.Landscape, 6, 1, ct);
        var zero = await sut.WorstCaseAsync(Aspect.Landscape, 0, 1, ct);

        six.WorstCaseUsd.ShouldBe(one.WorstCaseUsd * 6);
        zero.WorstCaseUsd.ShouldBe(0m);
    }

    [Fact]
    public async Task WorstCase_NegativeMaxTerms_Throws()
    {
        var sut = new ImageCostEstimator(new FakeCatalog(NoImage), new ImagesOptions());

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            sut.WorstCaseAsync(Aspect.Landscape, -1, 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Options_Defaults_PassValidation() => ImagesOptionsValidator.Validate(new ImagesOptions()).ShouldBeEmpty();

    [Theory]
    [InlineData(4)]
    [InlineData(21)]
    [InlineData(0)]
    public void Options_InvalidClipSeconds_FailsValidation(int clip)
    {
        var problems = ImagesOptionsValidator.Validate(new ImagesOptions { ClipSeconds = clip });

        problems.ShouldHaveSingleItem().ShouldContain(nameof(ImagesOptions.ClipSeconds));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    public void Options_ClipSecondsBounds_Pass(int clip) =>
        ImagesOptionsValidator.Validate(new ImagesOptions { ClipSeconds = clip }).ShouldBeEmpty();

    [Fact]
    public void Options_OtherBadValues_AreEachReported()
    {
        var problems = ImagesOptionsValidator.Validate(new ImagesOptions
        {
            SpareShare = 0.6,
            SparseShare = 0.2,
            MaxSkippedShare = 1.5,
            Model = " ",
            Quality = "ultra",
            PlaceholderTokensPerImage = 999,
        });

        problems.Count.ShouldBe(6);
    }

    [Theory]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]
    [InlineData("medium", "medium")]
    public void Options_UnsetQuality_IsAuto(string? quality, string expected) =>
        new ImagesOptions { Quality = quality }.QualityOrAuto.ShouldBe(expected);

    [Fact]
    public void ImagePlanJson_ContainsNoKeywordText()
    {
        var plan = new RenderImagePlan(
            [new PlannedTerm(0, "hook", "haunted lighthouse stairs"), new PlannedTerm(1, "p:0", "drowned bell tower")],
            clipSeconds: 10,
            neededImages: 3,
            maxTerms: 4,
            trimmedCount: 1,
            hintCodes: [RenderImagePlanner.ScenesTrimmed]);

        var json = ImagePlanJson.Serialize(plan, "style=dark-folklore-ink@1a2b3c4d;neg=v1");

        json.ShouldNotContain("lighthouse");
        json.ShouldNotContain("bell tower");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.GetProperty("clip_seconds").GetInt32().ShouldBe(10);
        root.GetProperty("needed").GetInt32().ShouldBe(3);
        root.GetProperty("max_terms").GetInt32().ShouldBe(4);
        root.GetProperty("planned").GetInt32().ShouldBe(2);
        root.GetProperty("trimmed").GetInt32().ShouldBe(1);
        root.GetProperty("hints").EnumerateArray().Select(e => e.GetString()).ShouldBe(["scenes_trimmed"]);
        root.GetProperty("slot_segments").EnumerateArray().Select(e => e.GetString()).ShouldBe(["hook", "p:0"]);
        root.GetProperty("style_prompt_version").GetString().ShouldBe("style=dark-folklore-ink@1a2b3c4d;neg=v1");
    }

    private sealed class FakeCatalog(IReadOnlyDictionary<string, ImagePriceRate> image) : IPriceCatalog
    {
        public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct) =>
            Task.FromResult<ErrorOr<PriceVersion>>(Build());

        public Task<PriceVersion> GetAsync(int version, CancellationToken ct) => Task.FromResult(Build());

        private PriceVersion Build()
        {
            var rates = new PriceVersionRates(
                new Dictionary<string, PriceRates> { ["gpt-6-luna"] = new(0.10m, 0.01m, 0.125m, 0.50m) },
                image,
                null).ToJson();

            return PriceVersion.Create(1, At, rates, PriceVersionJson.WriteLimits(PriceLimits.D4Defaults), "fake", At);
        }
    }
}
