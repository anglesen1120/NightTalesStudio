using System.Text.RegularExpressions;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.Research.Facts;
using NightTales.Domain.Stories.Prompts;

namespace NightTales.Infrastructure.OpenAI;

/// <summary>
/// The fact-extraction step (02 README D21, 02.5.3-T1): build the one JSON data object, take the
/// <c>facts/v1</c> step prompt and the <c>facts.v1</c> schema from the catalog, make the call and return the
/// outcome as data.
/// </summary>
/// <remarks>
/// <para>
/// The sources are a <see cref="FactInputBudget"/> plan: already alias-assigned (<c>s1…s10</c>), truncated
/// and sanitised. Only <c>alias</c>, <c>title</c> and <c>text</c> travel in the data object;
/// <see cref="FactSourceInput.SourceId"/>, <see cref="FactSourceInput.IsUserNote"/> and any URL stay
/// backend-side (D6/F12).
/// </para>
/// <para>
/// Nothing here catches: every provider outcome is a <see cref="TextCallResult{T}"/> value returned
/// untouched (D7), and an <see cref="OperationCanceledException"/> from the caller's own deadline is left to
/// escape — the caller (the fact-sheet job) knows the request may have been billed and settles it (SEC-2).
/// Metering is the job's, not this step's (D14).
/// </para>
/// <para>
/// The request is a programming-error boundary: empty sources, an alias outside <c>s1…s10</c>, a duplicate
/// alias, more than ten sources or a non-positive <c>max_output_tokens</c> throw, because the caller builds
/// the request from the plan and must never hand a malformed call to the provider.
/// </para>
/// </remarks>
/// <param name="client">Makes the structured-output call.</param>
/// <param name="catalog">Supplies the <c>facts/v1</c> step prompt and the <c>facts.v1</c> schema.</param>
public sealed partial class OpenAiFactExtractor(ITextClient client, IPromptCatalog catalog) : IFactExtractor
{
    private const string SchemaName = "facts.v1";
    private const string SchemaFile = "facts.v1.json";
    private const string PromptKey = "facts/v1.md";
    private const string SchemaKey = "schemas/facts.v1.json";
    private const string CacheKeyPrefix = "facts:";

    /// <inheritdoc />
    public async ValueTask<TextCallResult<FactsV1>> ExtractAsync(FactExtractionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        Guard(req);

        var language = FactDataObjectJson.LanguageSpelling(req.Language);

        var call = new TextCallRequest(
            req.Run,
            TextStep.Facts,
            catalog.GetStepPrompt(PromptStep.Facts),
            FactDataObjectJson.Serialize(req.Language, req.Sources),
            SchemaName,
            catalog.GetSchema(SchemaFile),
            req.MaxOutputTokens,
            string.Concat(CacheKeyPrefix, language),
            // D5: the composite version names the files this release shipped for the step. The facts step is
            // not preset-based, so its own step prompt stands in for the preset component too; the schema
            // component is what distinguishes it.
            catalog.BuildPromptVersion(new PromptVersionParts(PromptKey, PromptKey, SchemaKey)));

        return await client.CallAsync<FactsV1>(call, ct).ConfigureAwait(false);
    }

    private static void Guard(FactExtractionRequest req)
    {
        if (req.Sources.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(req), "A fact-extraction call needs at least one source.");
        }

        if (req.Sources.Count > FactInputLimits.MaxSourcesCeiling)
        {
            throw new ArgumentOutOfRangeException(nameof(req), req.Sources.Count, $"A fact-extraction call carries at most {FactInputLimits.MaxSourcesCeiling} sources (aliases s1..s10).");
        }

        if (req.MaxOutputTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(req), req.MaxOutputTokens, "max_output_tokens must be positive.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in req.Sources)
        {
            if (!AliasPattern().IsMatch(source.Alias))
            {
                throw new ArgumentException("A source alias must match s1..s10.", nameof(req));
            }

            if (!seen.Add(source.Alias))
            {
                throw new ArgumentException("Source aliases must be unique.", nameof(req));
            }
        }
    }

    [GeneratedRegex(@"^s([1-9]|10)$", RegexOptions.CultureInvariant)]
    private static partial Regex AliasPattern();
}
