using NightTales.Domain.Usage;

namespace NightTales.Infrastructure.Persistence.Usage;

/// <summary>
/// The <c>OpenAi:Pricing</c> section (02 README D14, O3): the source of the <c>price_versions</c> seed
/// and the long-context threshold the calculator uses. An absent <see cref="Models"/> falls back to
/// the O3 defaults, so a host without the block still prices <c>gpt-6-luna</c> and <c>gpt-6-sol</c>.
/// </summary>
public sealed class PricingOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "OpenAi:Pricing";

    /// <summary>O3: an input above this many tokens costs 2× input and 1.5× output, for the whole request.</summary>
    public int SurchargeThresholdTokens { get; set; } = TextCostCalculator.DefaultSurchargeThresholdTokens;

    /// <summary>The flat USD price of one web-search call (02 T59); <c>null</c> leaves it out of the row.</summary>
    public decimal? WebSearchCallUsd { get; set; }

    /// <summary>The text rates per 1M tokens, keyed by model name.</summary>
    public Dictionary<string, ModelPricing> Models { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The per-image prices, keyed by model name (02 CR-27).</summary>
    public Dictionary<string, ImagePricing> Image { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The O3 defaults used when <see cref="Models"/> is empty.</summary>
    public static IReadOnlyDictionary<string, ModelPricing> DefaultModels { get; } =
        new Dictionary<string, ModelPricing>(StringComparer.Ordinal)
        {
            ["gpt-6-luna"] = new() { In = 0.10m, Cached = 0.01m, CacheWrite = 0.125m, Out = 0.50m },
            ["gpt-6-sol"] = new() { In = 2m, Cached = 0.20m, CacheWrite = 2.50m, Out = 10m },
        };

    /// <summary>The largest rate the validator accepts, USD per 1M tokens: a typo guard, not a price ceiling.</summary>
    public const decimal MaxRatePerMillionUsd = 1_000m;

    /// <summary>The largest <see cref="SurchargeThresholdTokens"/> the validator accepts (above any model context).</summary>
    public const int MaxSurchargeThresholdTokens = 2_000_000;

    /// <summary>The largest per-image or per-call price the validator accepts, USD.</summary>
    public const decimal MaxUnitPriceUsd = 100m;

    /// <summary>
    /// Checks the section: every rate is positive and below the cap, <c>cached</c> does not exceed <c>in</c>, the
    /// names and units are non-empty and do not shadow a reserved <c>rates</c> key, and the threshold is within
    /// bounds. Used by <c>ValidateOnStart</c> and again by the seeder, so a bad value can never reach a row.
    /// </summary>
    /// <param name="config">The bound section.</param>
    /// <returns>One message per problem; empty when the section is valid.</returns>
    public static IReadOnlyList<string> Validate(PricingOptions config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var problems = new List<string>();

        if (config.SurchargeThresholdTokens is <= 0 or > MaxSurchargeThresholdTokens)
        {
            problems.Add($"{nameof(SurchargeThresholdTokens)} must be between 1 and {MaxSurchargeThresholdTokens}.");
        }

        if (config.WebSearchCallUsd is { } call && (call <= 0m || call > MaxUnitPriceUsd))
        {
            problems.Add($"{nameof(WebSearchCallUsd)} must be greater than 0 and at most {MaxUnitPriceUsd}.");
        }

        IEnumerable<KeyValuePair<string, ModelPricing>> models = config.Models.Count > 0 ? config.Models : DefaultModels;
        foreach (var (name, rate) in models)
        {
            if (string.IsNullOrWhiteSpace(name) || IsReservedKey(name))
            {
                problems.Add($"Models key '{name}' is empty or a reserved rates key.");
                continue;
            }

            foreach (var (field, value) in new[] { ("in", rate.In), ("cached", rate.Cached), ("cache_write", rate.CacheWrite), ("out", rate.Out) })
            {
                if (value <= 0m || value > MaxRatePerMillionUsd)
                {
                    problems.Add($"Models:{name}:{field} must be greater than 0 and at most {MaxRatePerMillionUsd}.");
                }
            }

            if (rate.Cached > rate.In)
            {
                problems.Add($"Models:{name}: cached must not exceed in.");
            }
        }

        foreach (var (name, image) in config.Image)
        {
            if (string.IsNullOrWhiteSpace(name) || IsReservedKey(name))
            {
                problems.Add($"Image key '{name}' is empty or a reserved rates key.");
            }

            if (string.IsNullOrWhiteSpace(image.Unit))
            {
                problems.Add($"Image:{name}:Unit must not be empty.");
            }

            if (image.PerImageUsd <= 0m || image.PerImageUsd > MaxUnitPriceUsd)
            {
                problems.Add($"Image:{name}:PerImageUsd must be greater than 0 and at most {MaxUnitPriceUsd}.");
            }
        }

        return problems;
    }

    private static bool IsReservedKey(string name) =>
        name is PriceVersionRates.ImageKey or PriceVersionRates.WebSearchCallKey or PriceVersionRates.SurchargeThresholdKey;

    /// <summary>The rates of one model, USD per 1M tokens.</summary>
    public sealed class ModelPricing
    {
        /// <summary>Input tokens.</summary>
        public decimal In { get; set; }

        /// <summary>Cache-hit input tokens.</summary>
        public decimal Cached { get; set; }

        /// <summary>Cache-write tokens.</summary>
        public decimal CacheWrite { get; set; }

        /// <summary>Output tokens, reasoning included.</summary>
        public decimal Out { get; set; }
    }

    /// <summary>The price of one image model.</summary>
    public sealed class ImagePricing
    {
        /// <summary>The price unit; stored so a later unit never reinterprets an old row.</summary>
        public string Unit { get; set; } = "per_image";

        /// <summary>USD per image.</summary>
        public decimal PerImageUsd { get; set; }
    }
}
