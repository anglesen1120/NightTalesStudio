using System.Text.Json;
using System.Text.Json.Nodes;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;

namespace NightTales.Domain.Usage;

/// <summary>
/// One immutable <c>price_versions</c> row (02 README D14, CR-2/F35 — the P subset): the rates and
/// the D4 limits that were in force from <see cref="ActiveFrom"/>. <c>IPriceCatalog.GetActive</c>
/// picks the newest row with <c>active_from &lt;= queued_at</c>, so a run queued before a re-price
/// settles at the old rates.
/// </summary>
/// <remarks>
/// Append-only by contract: a rate change inserts a <em>new</em> row, because a historical
/// <c>usage_records.price_version</c> must stay resolvable forever — an update would silently rewrite
/// the cost of every settled call. <see cref="Rates"/> and <see cref="Limits"/> are the canonical jsonb
/// documents (raw JSON like <c>generation_runs.models</c>); decode them with
/// <see cref="PriceVersionRates.Parse"/> and <see cref="PriceVersionJson.ParseLimits"/>.
/// </remarks>
public sealed class PriceVersion : ICreatedAt
{
    private PriceVersion()
    {
    }

    /// <summary>The monotonic primary key, assigned as <c>max(version) + 1</c> by the seeder.</summary>
    /// <remarks>
    /// <c>RunContext.PriceVersion</c> and <c>usage_records.price_version</c> carry this value as text
    /// (12 CR-17); it is the integer it reads as, parsed at the store boundary.
    /// </remarks>
    public int Version { get; private set; }

    /// <summary>The instant this version becomes selectable; a future-dated row is skipped.</summary>
    public DateTimeOffset ActiveFrom { get; private set; }

    /// <summary>The <c>rates</c> jsonb document (<see cref="PriceVersionRates"/>).</summary>
    public string Rates { get; private set; } = string.Empty;

    /// <summary>The <c>limits</c> jsonb document (<see cref="PriceLimits"/>).</summary>
    public string Limits { get; private set; } = string.Empty;

    /// <summary>Sha256 of the seed config block the row was written from; the seeder's idempotence key.</summary>
    public string ConfigHash { get; private set; } = string.Empty;

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Inserts the version for one config snapshot. The caller passes the canonical documents
    /// (key-sorted, compact) that <see cref="ConfigHash"/> was computed over, so the hash is always of
    /// exactly the bytes stored.
    /// </summary>
    public static PriceVersion Create(
        int version,
        DateTimeOffset activeFrom,
        string ratesJson,
        string limitsJson,
        string configHash,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(ratesJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(limitsJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(configHash);

        return new PriceVersion
        {
            Version = version,
            ActiveFrom = activeFrom,
            Rates = ratesJson,
            Limits = limitsJson,
            ConfigHash = configHash,
            CreatedAt = now,
        };
    }

    /// <summary>The typed <c>rates</c> document of this row.</summary>
    public PriceVersionRates ParseRates() => PriceVersionRates.Parse(Rates);

    /// <summary>The typed <c>limits</c> document of this row (<c>limits.story</c>, 02 D4).</summary>
    public PriceLimits ParseLimits() => PriceVersionJson.ParseLimits(Limits);
}

/// <summary>
/// The JSON codec of the <c>price_versions.limits</c> document (02 README D4/D14):
/// <c>{"story": {"short": {"vi": {"min_tokens": 195, …, "writer": 2500, "review": 1200, "revision": 1500}, …}},
/// "bible_proposal": {"max_output_tokens": 1200}, "ideas": {"max_output_tokens": 1500}}</c>.
/// Hand-written over <see cref="JsonNode"/> so the stored key names are the documented ones, not a
/// serializer naming policy's idea of them; keys are written in enum order, so equal limits give equal bytes.
/// </summary>
public static class PriceVersionJson
{
    /// <summary>The reserved top-level key of the proposal step's limits block (02.6.3-T2).</summary>
    public const string BibleProposalKey = "bible_proposal";

    /// <summary>The reserved top-level key of the idea-generation step's limits block (02.8.1-T1).</summary>
    public const string IdeasKey = "ideas";

    /// <summary>The reserved top-level key of the idea-novelty judge step's limits block (02.8.1-T2).</summary>
    public const string NoveltyKey = "novelty";

    /// <summary>Serializes the limits document.</summary>
    public static string WriteLimits(PriceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        var story = new JsonObject();
        foreach (var (tier, byLanguage) in limits.Story.OrderBy(pair => pair.Key))
        {
            var cells = new JsonObject();
            foreach (var (language, limit) in byLanguage.OrderBy(pair => pair.Key))
            {
                cells[Key(language)] = new JsonObject
                {
                    ["min_tokens"] = limit.MinTokens,
                    ["max_tokens"] = limit.MaxTokens,
                    ["min_segments"] = limit.MinSegments,
                    ["max_segments"] = limit.MaxSegments,
                    ["writer"] = limit.WriterMaxOutput,
                    ["review"] = limit.ReviewMaxOutput,
                    ["revision"] = limit.RevisionMaxOutput,
                };
            }

            story[Key(tier)] = cells;
        }

        return new JsonObject
        {
            ["story"] = story,
            [BibleProposalKey] = new JsonObject { ["max_output_tokens"] = limits.BibleProposalMaxOutput },
            [IdeasKey] = new JsonObject { ["max_output_tokens"] = limits.IdeasMaxOutput },
            [NoveltyKey] = new JsonObject { ["max_output_tokens"] = limits.NoveltyMaxOutput },
        }.ToJsonString();
    }

    /// <summary>Deserializes a stored limits document.</summary>
    /// <exception cref="JsonException">The document is not a valid limits object.</exception>
    public static PriceLimits ParseLimits(string limitsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(limitsJson);

        var root = JsonNode.Parse(limitsJson) as JsonObject
            ?? throw new JsonException("price_versions.limits must be an object with a 'story' object.");
        var story = root["story"] as JsonObject
            ?? throw new JsonException("price_versions.limits must be an object with a 'story' object.");

        var result = new Dictionary<LengthTier, IReadOnlyDictionary<Language, LengthLimit>>();
        foreach (var (tierKey, cellsNode) in story)
        {
            var tier = Enum.Parse<LengthTier>(tierKey, ignoreCase: true);
            var cells = new Dictionary<Language, LengthLimit>();
            foreach (var (languageKey, cell) in cellsNode as JsonObject
                ?? throw new JsonException($"price_versions.limits.story.{tierKey} must be an object."))
            {
                var block = cell as JsonObject
                    ?? throw new JsonException($"price_versions.limits.story.{tierKey}.{languageKey} must be an object.");
                cells[Enum.Parse<Language>(languageKey, ignoreCase: true)] = new LengthLimit(
                    Int(block, "min_tokens"),
                    Int(block, "max_tokens"),
                    Int(block, "min_segments"),
                    Int(block, "max_segments"),
                    Int(block, "writer"),
                    Int(block, "review"),
                    Int(block, "revision"));
            }

            result[tier] = cells;
        }

        // 02.6.3-T2: the proposal step's own cap. A row written before the block existed keeps the ruled
        // default, so a historical version stays readable (a usage_records.price_version is never rewritten).
        var maxOutput = root[BibleProposalKey] is JsonObject bibleProposal
            ? Int(bibleProposal, "max_output_tokens")
            : PriceLimits.DefaultBibleProposalMaxOutput;

        // 02.8.1-T1: the idea-generation step's cap, with the same historical-row rule.
        var ideasMaxOutput = root[IdeasKey] is JsonObject ideas
            ? Int(ideas, "max_output_tokens")
            : PriceLimits.DefaultIdeasMaxOutput;

        // 02.8.1-T2: the novelty judge step's cap, additive and with the same historical-row rule.
        var noveltyMaxOutput = root[NoveltyKey] is JsonObject novelty
            ? Int(novelty, "max_output_tokens")
            : PriceLimits.DefaultNoveltyMaxOutput;

        return new PriceLimits(result)
        {
            BibleProposalMaxOutput = maxOutput,
            IdeasMaxOutput = ideasMaxOutput,
            NoveltyMaxOutput = noveltyMaxOutput,
        };
    }

    private static string Key<TEnum>(TEnum value)
        where TEnum : struct, Enum => value.ToString().ToLowerInvariant();

    private static int Int(JsonObject block, string key) =>
        block[key]?.GetValue<int>() ?? throw new JsonException($"price_versions.limits cell misses '{key}'.");
}
