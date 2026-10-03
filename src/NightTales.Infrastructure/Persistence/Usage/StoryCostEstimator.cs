using System.Collections.Concurrent;
using System.Text;
using ErrorOr;
using Microsoft.Extensions.Configuration;
using NightTales.Application.Common.Errors;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Usage;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Length;
using NightTales.Domain.Stories.Prompts;
using NightTales.Domain.Usage;

namespace NightTales.Infrastructure.Persistence.Usage;

/// <summary>
/// The worst-case price of one story run (02 README D14, CR-6/T46): four paid calls — writer, review,
/// revision, review #2 — each at its own <c>max_output_tokens</c> from the active price version, at the
/// version's rates with the long-context surcharge, plus two moderation calls priced at 0 (O7).
/// </summary>
/// <remarks>
/// Deliberately an upper bound: input tokens are bounded by UTF-8 <em>bytes</em> (P6), output by the cap,
/// and nothing is assumed cached. A review or revision call also carries the story and the review
/// findings, which the data-object budget (<c>Story:Prompt:MaxDataObjectBytes</c>) bounds; they are counted
/// at that budget each. Every step's bound also charges a cache write of its whole input (D14 example C). The
/// prompt bytes are cached per <c>(tier, language, prompt release, data budget)</c>, so a new prompt release or
/// budget recomputes them; a step whose prompt is not shipped yet returns <c>prompt_not_shipped</c>.
/// </remarks>
public sealed class StoryCostEstimator(
    IPriceCatalog catalog,
    IPromptCatalog prompts,
    IConfiguration configuration,
    IClock clock) : IStoryCostEstimator
{
    /// <summary>The model of a step when <c>OpenAi:Models:{step}</c> is absent (02 plan §OpenAI adapter).</summary>
    public const string DefaultModel = "gpt-6-luna";

    /// <summary>The default of <c>Story:Prompt:MaxDataObjectBytes</c> (02.6.3-T3, D6).</summary>
    public const int DefaultMaxDataObjectBytes = 65_536;

    private readonly ConcurrentDictionary<(LengthTier Tier, Language Language, string Release, int DataBudget), StepInputs> _inputs = new();

    /// <inheritdoc />
    public async Task<ErrorOr<UsageBound>> WorstCaseAsync(LengthTier tier, Language language, CancellationToken ct)
    {
        var active = await catalog.GetActiveAsync(clock.UtcNow, ct).ConfigureAwait(false);
        if (active.IsError)
        {
            return active.Errors;
        }

        var version = active.Value;
        var rates = version.ParseRates();
        var target = LengthTargetResolver.Resolve(tier, language, version.ParseLimits());

        var dataBudget = configuration.GetValue("Story:Prompt:MaxDataObjectBytes", DefaultMaxDataObjectBytes);
        var key = (tier, language, prompts.Release, dataBudget);
        if (!_inputs.TryGetValue(key, out var inputs))
        {
            var measured = MeasureInputs(language, dataBudget);
            if (measured.IsError)
            {
                return measured.Errors;
            }

            inputs = _inputs.GetOrAdd(key, measured.Value);
        }

        var threshold = rates.SurchargeThresholdTokens;

        // Each step's bound also charges the largest possible cache write (every input token written to the
        // cache, D14 example C) at the same rates, long-context branch included: the bound never sits below
        // what the meter settles for a call that wrote its prompt to the cache.
        decimal Cost(string step, int inputBytes, int maxOutput) =>
            TextCostCalculator.Compute(rates.RatesFor(ModelOf(step)), inputBytes, 0, inputBytes, maxOutput, threshold)
            ?? throw new InvalidOperationException("The calculator returned no cost for a bounded call.");

        // The two moderation calls (O7) are free: counted so the call count is explicit, adding 0.
        const decimal ModerationCost = 0m;
        var worst =
            Cost("Writer", inputs.Writer, target.WriterMaxOutput)
            + Cost("Review", inputs.Review, target.ReviewMaxOutput)
            + Cost("Revision", inputs.Revision, target.RevisionMaxOutput)
            + Cost("Review", inputs.Review, target.ReviewMaxOutput)
            + (2 * ModerationCost);

        return new UsageBound(worst, Math.Max(inputs.Writer, Math.Max(inputs.Review, inputs.Revision)), target.WriterMaxOutput);
    }

    private string ModelOf(string step) => configuration[$"OpenAi:Models:{step}"] is { Length: > 0 } model ? model : DefaultModel;

    private ErrorOr<StepInputs> MeasureInputs(Language language, int dataBudget)
    {
        try
        {
            // The writer prompt: step prompt + the largest preset + every shared block + the data object.
            var shared = Enum.GetValues<PromptFile>().Sum(file => Bytes(prompts.GetShared(file, language)));
            var preset = Enum.GetValues<Topic>().Max(topic => Bytes(prompts.GetPreset(topic, language)));
            var writer = Bytes(prompts.GetStepPrompt(PromptStep.Writer)) + preset + shared + dataBudget;

            // Review and revision carry the story (bounded by the data budget) next to their own prompt;
            // revision also carries the review findings.
            var review = Bytes(prompts.GetStepPrompt(PromptStep.Review)) + (2 * dataBudget);
            var revision = Bytes(prompts.GetStepPrompt(PromptStep.Revision)) + (3 * dataBudget);

            return new StepInputs(writer, review, revision);
        }
        catch (PromptCatalogException exception)
        {
            // A step whose prompt the manifest does not ship yet cannot be bounded: fail closed with a typed
            // error instead of throwing out of the budget guard's reserve path.
            return Error.Failure(ErrorCodes.PromptNotShipped, exception.Message);
        }
    }

    private static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    private sealed record StepInputs(int Writer, int Review, int Revision);
}
