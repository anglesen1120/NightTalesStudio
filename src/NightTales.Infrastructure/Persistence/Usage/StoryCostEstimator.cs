using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
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
/// at that budget each. The prompt bytes are cached per <c>(tier, language, prompt release)</c>, so a new
/// prompt release recomputes them.
/// </remarks>
public sealed class StoryCostEstimator(
    IPriceCatalog catalog,
    IPromptCatalog prompts,
    IOptions<PricingOptions> options,
    IConfiguration configuration,
    IClock clock) : IStoryCostEstimator
{
    /// <summary>The model of a step when <c>OpenAi:Models:{step}</c> is absent (02 plan §OpenAI adapter).</summary>
    public const string DefaultModel = "gpt-6-luna";

    /// <summary>The default of <c>Story:Prompt:MaxDataObjectBytes</c> (02.6.3-T3, D6).</summary>
    public const int DefaultMaxDataObjectBytes = 65_536;

    private readonly ConcurrentDictionary<(LengthTier Tier, Language Language, string Release), StepInputs> _inputs = new();

    /// <inheritdoc />
    public async Task<UsageBound> WorstCaseAsync(LengthTier tier, Language language, CancellationToken ct)
    {
        var version = await catalog.GetActiveAsync(clock.UtcNow, ct).ConfigureAwait(false);
        var rates = version.ParseRates();
        var target = LengthTargetResolver.Resolve(tier, language, version.ParseLimits());
        var inputs = _inputs.GetOrAdd((tier, language, prompts.Release), _ => MeasureInputs(language));
        var threshold = options.Value.SurchargeThresholdTokens;

        decimal Cost(string step, int inputBytes, int maxOutput) =>
            TextCostCalculator.Compute(rates.RatesFor(ModelOf(step)), inputBytes, 0, 0, maxOutput, threshold)
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

    private StepInputs MeasureInputs(Language language)
    {
        var dataBudget = configuration.GetValue("Story:Prompt:MaxDataObjectBytes", DefaultMaxDataObjectBytes);

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

    private static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    private sealed record StepInputs(int Writer, int Review, int Revision);
}
