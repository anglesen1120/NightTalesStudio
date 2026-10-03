using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Prompts;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Application.Features.Stories.Writing;

/// <summary>
/// Assembles the two strings of a writer call (02 README D5/D6, 02.2.3-T2): a static system prompt —
/// writer system, then the preset with its placeholders substituted in place — and one JSON data object
/// that carries every piece of untrusted material.
/// </summary>
/// <remarks>
/// <para>
/// Untrusted text (brief, bible, facts, avoid-plots) is only ever a JSON string value, serialised with
/// <c>System.Text.Json</c>; it is never substituted into a template, so it cannot break the JSON or the
/// turn structure. The placeholder scan runs on prompt files only.
/// </para>
/// <para>
/// The system prompt depends on <c>(topic, language, tier)</c> alone — every run-varying value lives in
/// the data object — so it is byte-identical across runs and the provider's prompt cache applies (O9).
/// </para>
/// </remarks>
public sealed partial class PromptAssembler(IPromptCatalog catalog, ILogger<PromptAssembler> logger) : IPromptAssembler
{
    /// <summary>The longest brief the assembler sends, in UTF-16 code units; the create command rejects longer ones first.</summary>
    public const int MaxBriefLength = 1000;

    private const string WriteStoryTask = "write_story";
    private const string WriterStep = "writer";
    private const string SystemKey = "system/writer.v1.md";
    private const string SchemaKey = "schemas/story.v1.json";

    /// <summary>The pinned serialiser options (02 T68): readable non-ASCII text, HTML-sensitive characters still escaped.</summary>
    private static readonly JsonSerializerOptions DataOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <inheritdoc />
    public AssembledPrompt Assemble(StoryWriteRequest req, AssemblerContext ctx)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(ctx);

        var values = PlaceholderValues(req, ctx);
        var systemPrompt = string.Concat(
            Substitute(catalog.GetSystemPrompt(), values),
            "\n\n",
            Substitute(catalog.GetPreset(ctx.Topic, ctx.Language), values));

        var promptVersion = catalog.BuildPromptVersion(new PromptVersionParts(SystemKey, ctx.PresetKey, SchemaKey));
        var cacheKey = string.Concat(WriterStep, ":", Path.GetFileNameWithoutExtension(ctx.PresetKey));

        return new AssembledPrompt(systemPrompt, JsonSerializer.Serialize(BuildDataObject(req), DataOptions), promptVersion, cacheKey);
    }

    private Dictionary<string, string> PlaceholderValues(StoryWriteRequest req, AssemblerContext ctx) =>
        new(StringComparer.Ordinal)
        {
            [PromptPlaceholders.LanguageName] = LanguageName(ctx.Language),
            [PromptPlaceholders.TierMinutes] = TierMinutes(req.Episode.LengthTier),
            [PromptPlaceholders.TokenRange] = Range(req.Length.MinTokens, req.Length.MaxTokens),
            [PromptPlaceholders.SegmentRange] = Range(req.Length.MinSegments, req.Length.MaxSegments),
            [PromptPlaceholders.KeywordRules] = catalog.GetShared(PromptFile.KeywordRules, ctx.Language),
            [PromptPlaceholders.EarRules] = catalog.GetShared(PromptFile.EarRules, ctx.Language),
            [PromptPlaceholders.OverusedPhrases] = catalog.GetShared(PromptFile.Overused, ctx.Language),
            [PromptPlaceholders.StructureVocab] = catalog.GetShared(PromptFile.StructureVocab, ctx.Language),
        };

    /// <summary>Single-pass substitution: inserted text is never rescanned, so a shared block cannot inject a placeholder.</summary>
    private static string Substitute(string template, Dictionary<string, string> values) =>
        PlaceholderPattern().Replace(
            template,
            match => values.TryGetValue(match.Groups[1].Value, out var value)
                ? value
                : throw new InvalidOperationException(
                    FormattableString.Invariant($"Prompt placeholder '{match.Groups[1].Value}' is not in the closed list (02 README D5).")));

    private StoryDataObject BuildDataObject(StoryWriteRequest req)
    {
        var length = req.Length;
        var target = (int)Math.Round((length.MinTokens + length.MaxTokens) / 2.0, MidpointRounding.ToEven);
        var unitsPerSegment = (int)Math.Round(target / (double)length.MinSegments, MidpointRounding.ToEven);

        return new StoryDataObject(
            WriteStoryTask,
            TruncateBrief(req.Episode.Brief),
            new DataLength(
                req.Episode.LengthTier.ToString().ToLowerInvariant(),
                [length.MinTokens, length.MaxTokens],
                [length.MinSegments, length.MaxSegments],
                unitsPerSegment,
                target),
            req.Bible,
            req.Facts is null ? [] : [.. req.Facts.Select(fact => new DataFact(fact.Id, fact.Statement, fact.SourceAliases))],
            req.AvoidPlots,
            req.Episode.Structure is { } plan ? ToData(plan) : null,
            req.Run.Attempt == 2 && req.ViolatedRules is { } rules ? new DataCorrectiveNote(rules) : null);
    }

    private string TruncateBrief(string brief)
    {
        if (brief.Length <= MaxBriefLength)
        {
            return brief;
        }

        LogBriefTruncated(brief.Length, MaxBriefLength);

        // Never cut between the halves of a surrogate pair: that would leave an invalid UTF-16 string.
        var end = char.IsHighSurrogate(brief[MaxBriefLength - 1]) ? MaxBriefLength - 1 : MaxBriefLength;
        return brief[..end];
    }

    private static DataStructurePlan ToData(StructurePlan plan) =>
        new(
            new DataStructureRequired(plan.Required.SubGenre, plan.Required.Setting, plan.Required.Opening, plan.Required.TwistType),
            new DataStructureForbidden(
                NullIfEmpty(plan.Forbidden.SubGenre),
                NullIfEmpty(plan.Forbidden.Setting),
                NullIfEmpty(plan.Forbidden.Opening),
                NullIfEmpty(plan.Forbidden.TwistType)));

    private static IReadOnlyList<string>? NullIfEmpty(IReadOnlyList<string> values) => values.Count == 0 ? null : values;

    private static string LanguageName(Language language) => language switch
    {
        Language.Vi => "Vietnamese",
        Language.En => "English",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Unknown content language."),
    };

    /// <summary>The tier's headline length in minutes (README P6: Short ≤ 1, Standard ≤ 8, Long ≤ 15).</summary>
    private static string TierMinutes(LengthTier tier) => tier switch
    {
        LengthTier.Short => "1",
        LengthTier.Standard => "8",
        LengthTier.Long => "15",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown length tier."),
    };

    private static string Range(int min, int max) =>
        string.Create(CultureInfo.InvariantCulture, $"{min}-{max}");

    [GeneratedRegex(@"\{\{([a-z_]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();

    [LoggerMessage(
        EventId = 6000,
        Level = LogLevel.Warning,
        Message = "story.prompt.brief-truncated original_length={OriginalLength} limit={Limit}")]
    private partial void LogBriefTruncated(int originalLength, int limit);
}
