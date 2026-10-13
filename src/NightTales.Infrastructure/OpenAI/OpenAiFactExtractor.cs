using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
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
/// Nothing here catches a provider outcome: every one is a <see cref="TextCallResult{T}"/> value returned
/// untouched (D7). The one exception is a cancellation: an <see cref="OperationCanceledException"/> raised after
/// the request was dispatched is metered first — the provider can still bill a request that was sent — and then
/// rethrown, exactly as <c>OpenAiStoryWriter</c> does (SEC-2). A cancellation before the request left this
/// process records nothing. Every other outcome is the job's to meter (D14), not this step's.
/// </para>
/// <para>
/// The request is a programming-error boundary: empty sources, an alias outside <c>s1…s10</c>, a duplicate
/// alias, more sources than <c>Facts:MaxSources</c>, a serialised data object above <c>Facts:MaxInputTokens</c>
/// or a non-positive <c>max_output_tokens</c> throw, because the caller builds the request from the plan and
/// must never hand a malformed or over-budget call to the provider. The budget check repeats what
/// <see cref="FactInputBudget.Plan"/> already reports, so a caller that ignores
/// <see cref="FactInputBudgetStatus.OverBudget"/> or hand-builds the sources still cannot exceed the cap.
/// </para>
/// </remarks>
/// <param name="client">Makes the structured-output call.</param>
/// <param name="catalog">Supplies the <c>facts/v1</c> step prompt and the <c>facts.v1</c> schema.</param>
/// <param name="options">The <c>Facts</c> input budget this call is checked against before the client is touched.</param>
/// <param name="meter">Records the estimated row of a dispatched call its caller cancelled (SEC-2).</param>
/// <param name="estimator">The D1 bound that estimated row is written at.</param>
public sealed partial class OpenAiFactExtractor(
    ITextClient client,
    IPromptCatalog catalog,
    IOptions<FactsOptions> options,
    IUsageMeter meter,
    IFactSheetCostEstimator estimator) : IFactExtractor
{
    /// <summary>
    /// The D14 operation of the one paid call of a run, the same value
    /// <c>GenerateFactSheetPipeline.Operation</c> meters every returned outcome under.
    /// </summary>
    public const string Operation = "fact_sheet";

    private const string SchemaName = "facts.v1";
    private const string SchemaFile = "facts.v1.json";
    private const string PromptKey = "facts/v1.md";
    private const string SchemaKey = "schemas/facts.v1.json";
    private const string CacheKeyPrefix = "facts:";

    /// <inheritdoc />
    public async ValueTask<TextCallResult<FactsV1>> ExtractAsync(FactExtractionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var settings = options.Value;
        Guard(req, settings);

        var language = FactDataObjectJson.LanguageSpelling(req.Language);

        // F-1: the cap is enforced here, not only reported by FactInputBudget.Plan. The data object is
        // serialised once, so the bytes the estimate is taken from are exactly the bytes the client sends, and
        // the check runs before the text client is touched: an over-budget request never reaches (and never
        // bills) the provider.
        var userDataJson = FactDataObjectJson.Serialize(req.Language, req.Sources);
        var estimatedTokens = FactInputBudget.EstimateTokens(userDataJson);

        if (estimatedTokens > settings.MaxInputTokens)
        {
            throw new ArgumentOutOfRangeException(
                nameof(req),
                estimatedTokens,
                $"The serialised data object is an estimated {estimatedTokens} tokens, above Facts:MaxInputTokens ({settings.MaxInputTokens}).");
        }

        var call = new TextCallRequest(
            req.Run,
            TextStep.Facts,
            catalog.GetStepPrompt(PromptStep.Facts),
            userDataJson,
            SchemaName,
            catalog.GetSchema(SchemaFile),
            req.MaxOutputTokens,
            string.Concat(CacheKeyPrefix, language),
            // D5: the composite version names the files this release shipped for the step. The facts step is
            // not preset-based, so its own step prompt stands in for the preset component too; the schema
            // component is what distinguishes it.
            catalog.BuildPromptVersion(new PromptVersionParts(PromptKey, PromptKey, SchemaKey)));

        try
        {
            return await client.CallAsync<FactsV1>(call, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (call.DispatchedModel is { } dispatchedModel)
        {
            // SEC-2: the caller gave up after the request may have been sent, and the provider can still bill it.
            // Record one estimated row at the D1 bound — on the meter's own token, so the settlement survives the
            // caller's cancellation and a shutdown — then let the cancellation through: the job still decides the
            // run's outcome (a deadline fails the run, a shutdown leaves it to the sweeper).
            var bound = await estimator.WorstCaseAsync(CancellationToken.None).ConfigureAwait(false);
            if (!bound.IsError)
            {
                await meter
                    .RecordAsync(
                        TextCallResultUsageExtensions.CancelledCallUsageItem(
                            req.Run,
                            Operation,
                            req.Run.EpisodeId,
                            dispatchedModel,
                            bound.Value.WorstCaseUsd),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw;
        }
    }

    private static void Guard(FactExtractionRequest req, FactsOptions options)
    {
        if (req.Sources.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(req), "A fact-extraction call needs at least one source.");
        }

        // The alias scheme's own ceiling is s1…s10; a lower Facts:MaxSources tightens the call further.
        var maxSources = Math.Min(options.MaxSources, FactInputLimits.MaxSourcesCeiling);
        if (req.Sources.Count > maxSources)
        {
            throw new ArgumentOutOfRangeException(nameof(req), req.Sources.Count, $"A fact-extraction call carries at most {maxSources} sources (aliases s1..s{maxSources}).");
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
