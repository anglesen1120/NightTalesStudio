using System.Text.Json;
using System.Text.Json.Nodes;

namespace NightTales.Domain.Usage;

/// <summary>
/// The per-1M-token text rates of one model inside a <c>price_versions.rates</c> document
/// (02 README D14/O3): <c>{"in": 0.10, "cached": 0.01, "cache_write": 0.125, "out": 0.50}</c>.
/// </summary>
/// <param name="Input">USD per 1M input tokens.</param>
/// <param name="CachedInput">USD per 1M cache-hit input tokens.</param>
/// <param name="CacheWrite">USD per 1M cache-write tokens.</param>
/// <param name="Output">USD per 1M output tokens (reasoning included, O4).</param>
public sealed record PriceRates(decimal Input, decimal CachedInput, decimal CacheWrite, decimal Output);

/// <summary>
/// A per-image price entry inside <c>rates.image</c> (02 CR-27):
/// <c>{"unit": "per_image", "per_image_usd": 0.007176}</c>. The unit stays stored so a later shape
/// never silently reinterprets an old row.
/// </summary>
public sealed record ImagePriceRate(string Unit, decimal PerImageUsd)
{
    /// <summary>The <see cref="Unit"/> value the seed writes for a flat per-image price (02 CR-27).</summary>
    public const string PerImageUnit = "per_image";
}

/// <summary>
/// The typed view of one <c>price_versions.rates</c> jsonb document (02 README D14, 00 §6): every
/// top-level key is a text model name except the reserved <c>image</c> object, the flat
/// <c>web_search_call</c> price (02 CR-2) and the <c>surcharge_threshold_tokens</c> integer. The O3
/// long-context threshold is a price parameter: it is stored in the row and hashed with the rates,
/// so a run settles at the threshold of its own version (02 T111, QA-4).
/// </summary>
/// <param name="Text">The per-model text rates, keyed by model name.</param>
/// <param name="Image">The per-image prices keyed by model name.</param>
/// <param name="WebSearchCallUsd">The flat USD price of one web-search call, or <c>null</c>.</param>
/// <param name="SurchargeThresholdTokens">Input tokens above which O3 doubles input and raises output 1.5×.</param>
public sealed record PriceVersionRates(
    IReadOnlyDictionary<string, PriceRates> Text,
    IReadOnlyDictionary<string, ImagePriceRate> Image,
    decimal? WebSearchCallUsd,
    int SurchargeThresholdTokens = TextCostCalculator.DefaultSurchargeThresholdTokens)
{
    /// <summary>The reserved jsonb key of the flat web-search price.</summary>
    public const string WebSearchCallKey = "web_search_call";

    /// <summary>The reserved jsonb key of the per-image price object.</summary>
    public const string ImageKey = "image";

    /// <summary>The reserved jsonb key of the O3 long-context threshold, in input tokens.</summary>
    public const string SurchargeThresholdKey = "surcharge_threshold_tokens";

    /// <summary>
    /// The rates of one model. Called at estimate time, before dispatch, so an unpriced model stops
    /// the step instead of running a paid call that cannot be costed.
    /// </summary>
    /// <exception cref="UnknownModelPricingException">The model is not in the document.</exception>
    public PriceRates RatesFor(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        return Text.TryGetValue(model, out var rates)
            ? rates
            : throw new UnknownModelPricingException(model);
    }

    /// <summary>Parses the stored <c>rates</c> jsonb document.</summary>
    /// <exception cref="JsonException">A reserved key has the wrong shape or a model block misses a field.</exception>
    public static PriceVersionRates Parse(string ratesJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ratesJson);

        var document = JsonNode.Parse(ratesJson) as JsonObject
            ?? throw new JsonException("price_versions.rates must be a JSON object.");

        var text = new Dictionary<string, PriceRates>(StringComparer.Ordinal);
        var image = new Dictionary<string, ImagePriceRate>(StringComparer.Ordinal);
        decimal? webSearchCall = null;
        var threshold = TextCostCalculator.DefaultSurchargeThresholdTokens;

        foreach (var (key, node) in document)
        {
            switch (key)
            {
                case WebSearchCallKey:
                    webSearchCall = node?.GetValue<decimal>();
                    break;
                case ImageKey:
                    ParseImage(node, image);
                    break;
                case SurchargeThresholdKey:
                    threshold = node?.GetValue<int>()
                        ?? throw new JsonException("price_versions.rates.surcharge_threshold_tokens must be an integer.");
                    break;
                default:
                    text[key] = ParseModel(node, key);
                    break;
            }
        }

        return new PriceVersionRates(text, image, webSearchCall, threshold);
    }

    /// <summary>
    /// The canonical compact JSON of this document (reserved keys first, model names in ordinal
    /// order), so two seeds of the same logical config produce byte-identical rows. Decimals are
    /// scale-normalised, so a config that writes <c>0.10</c> and one that writes <c>0.1</c> hash equal.
    /// </summary>
    public string ToJson()
    {
        var document = new JsonObject
        {
            [SurchargeThresholdKey] = JsonValue.Create(SurchargeThresholdTokens),
        };

        if (WebSearchCallUsd is { } call)
        {
            document[WebSearchCallKey] = JsonValue.Create(Normalize(call));
        }

        if (Image.Count > 0)
        {
            var images = new JsonObject();
            foreach (var (model, rate) in Image.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                images[model] = new JsonObject
                {
                    ["unit"] = JsonValue.Create(rate.Unit),
                    ["per_image_usd"] = JsonValue.Create(Normalize(rate.PerImageUsd)),
                };
            }

            document[ImageKey] = images;
        }

        foreach (var (model, rate) in Text.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            document[model] = new JsonObject
            {
                ["in"] = JsonValue.Create(Normalize(rate.Input)),
                ["cached"] = JsonValue.Create(Normalize(rate.CachedInput)),
                ["cache_write"] = JsonValue.Create(Normalize(rate.CacheWrite)),
                ["out"] = JsonValue.Create(Normalize(rate.Output)),
            };
        }

        return document.ToJsonString();
    }

    /// <summary>Drops trailing zeros, so <c>0.10m</c> and <c>0.1m</c> serialise to the same literal.</summary>
    private static decimal Normalize(decimal value) => value / 1.000000000000000000000000000000000m;

    private static PriceRates ParseModel(JsonNode? node, string model)
    {
        var block = node as JsonObject
            ?? throw new JsonException($"price_versions.rates.{model} must be an object.");

        return new PriceRates(
            Required(block, "in", model),
            Required(block, "cached", model),
            Required(block, "cache_write", model),
            Required(block, "out", model));
    }

    private static decimal Required(JsonObject block, string key, string model)
    {
        if (!block.TryGetPropertyValue(key, out var node) || node is null)
        {
            throw new JsonException($"price_versions.rates.{model} misses '{key}'.");
        }

        return node.GetValue<decimal>();
    }

    private static void ParseImage(JsonNode? node, Dictionary<string, ImagePriceRate> images)
    {
        if (node is null)
        {
            return;
        }

        foreach (var (model, entry) in node.AsObject())
        {
            var block = entry as JsonObject
                ?? throw new JsonException($"price_versions.rates.image.{model} must be an object.");
            var unit = block["unit"]?.GetValue<string>()
                ?? throw new JsonException($"price_versions.rates.image.{model} misses 'unit'.");

            images[model] = new ImagePriceRate(unit, Required(block, "per_image_usd", model));
        }
    }
}
