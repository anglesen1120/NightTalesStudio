using System.Globalization;
using ErrorOr;
using Microsoft.Extensions.Configuration;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Stories.Prompts;
using NightTales.Domain.Usage;

namespace NightTales.Infrastructure.Autopilot;

/// <summary>
/// The worst-case price of one idea-generation call (02 README D1/D14/T56, 02.8.1-T1 plan §7.1): the one paid
/// call at its own <c>max_output_tokens</c> from the run's price version, at that version's model rates with the
/// long-context surcharge. 02.8.1-T2 adds <see cref="NoveltyWorstCaseAsync"/>, the same bound for the
/// <c>novelty</c> judge call, priced with <c>OpenAi:Models:Novelty</c> and capped by
/// <c>limits.novelty.max_output_tokens</c>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately an upper bound, in the shape of <c>BibleProposalCostEstimator</c>: the system prompt is counted
/// at UTF-8 <em>bytes</em> (6 per UTF-16 unit, the escaping worst case) and the data object at
/// <c>Story:Prompt:MaxDataObjectBytes</c>, because the generator refuses to send a larger one (P6); the output
/// is bounded by the cap and nothing is assumed cached — the whole input is charged a cache write too (D14
/// example C). The bound is the one the generator meters an absent usage report or an unpriced call at.
/// </para>
/// <para>
/// The ceiling is <em>not</em> a configuration key (02 README T56): it is
/// <c>price_versions.limits.ideas.max_output_tokens</c>, read from the version the run itself is costed with
/// (<see cref="RunContext.PriceVersion"/> through the meter), so the cap the request sends and the cap the usage
/// row is written at can never disagree. A missing or unparsable version is a typed failure, never a fallback.
/// </para>
/// </remarks>
/// <param name="prices">The price versions, addressed by the run's own number.</param>
/// <param name="prompts">Supplies the <c>ideas/generate.v1</c> step prompt, whose incoming tokens are the system prefix.</param>
/// <param name="configuration">The <c>OpenAi:Models:Ideas</c> model and the data-object budget.</param>
/// <remarks>
/// It is the production <see cref="IIdeaCostEstimator"/>: the caller's admission checks the platform cap with
/// this bound before it queues the run, and the planner checks it again before the run's second (retry) dispatch,
/// which the admission's reservation does not cover — naming the run so its already-reserved bound stays out of
/// the sum, exactly as the admission-time check does.
/// </remarks>
public sealed class IdeaCostEstimator(
    IPriceCatalog prices,
    IPromptCatalog prompts,
    IConfiguration configuration) : IIdeaCostEstimator
{
    /// <summary>The model of the step when <c>OpenAi:Models:Ideas</c> is absent (02 plan §OpenAI adapter).</summary>
    public const string DefaultModel = "gpt-6-luna";

    /// <summary>The worst-case UTF-8 bytes, JSON escaping included, of one UTF-16 unit.</summary>
    private const long BytesPerChar = 6;

    /// <summary>The bytes per estimated token (the 02.5.3 fact-budget convention).</summary>
    private const long BytesPerEstimatedToken = 4;

    /// <summary>Where the step's model is configured, with the adapter's own spelling.</summary>
    public static string ModelKey => $"OpenAi:Models:{TextStep.Ideas}";

    /// <summary>The worst-case bound of one idea-generation call of <paramref name="priceVersion"/>.</summary>
    /// <param name="priceVersion">The run's <c>price_versions.version</c> as text (<c>RunContext.PriceVersion</c>).</param>
    /// <param name="ct">Cancels the lookup.</param>
    /// <returns>
    /// The bound, or <c>price_catalog_empty</c> when the run names no usable version and <c>prompt_not_shipped</c>
    /// when the release does not carry the step prompt (the call is not dispatched either way).
    /// </returns>
    public async Task<ErrorOr<UsageBound>> WorstCaseAsync(string priceVersion, CancellationToken ct)
    {
        if (!int.TryParse(priceVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            return Error.Failure(
                ErrorCodes.PriceCatalogEmpty,
                "The run names no price version, so the ideas call has neither rates nor its ruled output ceiling.");
        }

        PriceVersion version;
        try
        {
            version = await prices.GetAsync(number, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return Error.Failure(
                ErrorCodes.PriceCatalogEmpty,
                "The run's price version does not exist, so the ideas call has neither rates nor its ruled output ceiling.");
        }

        int promptChars;
        try
        {
            promptChars = prompts.GetStepPrompt(PromptStep.Ideas).Length;
        }
        catch (PromptCatalogException exception)
        {
            return Error.Failure(ErrorCodes.PromptNotShipped, exception.Message);
        }

        var rates = version.ParseRates();
        var limits = version.ParseLimits();

        var maxDataObjectBytes = configuration.GetValue(
            PromptDataBudget.ConfigurationKey,
            PromptDataBudget.DefaultMaxDataObjectBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDataObjectBytes);
        var inputBytes = (BytesPerChar * promptChars) + maxDataObjectBytes;
        var input = (int)((inputBytes + BytesPerEstimatedToken - 1) / BytesPerEstimatedToken);
        var maxOutput = limits.IdeasMaxOutput;
        if (maxOutput <= 0)
        {
            // A stored row with a zero cap would send max_output_tokens = 0: fail closed, never call.
            throw new InvalidOperationException("The price version's 'ideas.max_output_tokens' must be positive.");
        }

        var cost = TextCostCalculator.Compute(
            rates.RatesFor(Model),
            input,
            0,
            input,
            maxOutput,
            rates.SurchargeThresholdTokens)
            ?? throw new InvalidOperationException("The calculator returned no cost for a bounded call.");

        return new UsageBound(cost, input, maxOutput);
    }

    /// <summary>The worst-case bound of one idea-novelty judge call of <paramref name="priceVersion"/>.</summary>
    /// <remarks>
    /// The same shape as <see cref="WorstCaseAsync"/>, for the <c>novelty</c> step of the same run (02.8.1-T2):
    /// the judge's system prompt is the incoming prefix, the data object is bounded by
    /// <c>Story:Prompt:MaxDataObjectBytes</c>, and the output ceiling is the version's own
    /// <c>limits.novelty.max_output_tokens</c>. The model it is priced with is <c>OpenAi:Models:Novelty</c>,
    /// because the judge call's model is the adapter's novel step key, not the generation step's.
    /// </remarks>
    /// <param name="priceVersion">The run's <c>price_versions.version</c> as text (<c>RunContext.PriceVersion</c>).</param>
    /// <param name="ct">Cancels the lookup.</param>
    /// <returns>
    /// The bound, or <c>price_catalog_empty</c> when the run names no usable version and <c>prompt_not_shipped</c>
    /// when the release does not carry the novelty step prompt (the call is not dispatched either way).
    /// </returns>
    public async Task<ErrorOr<UsageBound>> NoveltyWorstCaseAsync(string priceVersion, CancellationToken ct)
    {
        if (!int.TryParse(priceVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            return Error.Failure(
                ErrorCodes.PriceCatalogEmpty,
                "The run names no price version, so the novelty call has neither rates nor its ruled output ceiling.");
        }

        PriceVersion version;
        try
        {
            version = await prices.GetAsync(number, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return Error.Failure(
                ErrorCodes.PriceCatalogEmpty,
                "The run's price version does not exist, so the novelty call has neither rates nor its ruled output ceiling.");
        }

        int promptChars;
        try
        {
            promptChars = prompts.GetStepPrompt(PromptStep.Novelty).Length;
        }
        catch (PromptCatalogException exception)
        {
            return Error.Failure(ErrorCodes.PromptNotShipped, exception.Message);
        }

        var rates = version.ParseRates();
        var limits = version.ParseLimits();

        var maxDataObjectBytes = configuration.GetValue(
            PromptDataBudget.ConfigurationKey,
            PromptDataBudget.DefaultMaxDataObjectBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDataObjectBytes);
        var inputBytes = (BytesPerChar * promptChars) + maxDataObjectBytes;
        var input = (int)((inputBytes + BytesPerEstimatedToken - 1) / BytesPerEstimatedToken);
        var maxOutput = limits.NoveltyMaxOutput;
        if (maxOutput <= 0)
        {
            // A stored row with a zero cap would send max_output_tokens = 0: fail closed, never call.
            throw new InvalidOperationException("The price version's 'novelty.max_output_tokens' must be positive.");
        }

        var cost = TextCostCalculator.Compute(
            rates.RatesFor(NoveltyModel),
            input,
            0,
            input,
            maxOutput,
            rates.SurchargeThresholdTokens)
            ?? throw new InvalidOperationException("The calculator returned no cost for a bounded call.");

        return new UsageBound(cost, input, maxOutput);
    }

    /// <summary>The configured model of the step, with the adapter's absent-key default.</summary>
    public string Model => configuration[ModelKey] is { Length: > 0 } model ? model : DefaultModel;

    /// <summary>Where the novelty step's model is configured, with the adapter's own spelling.</summary>
    public static string NoveltyModelKey => $"OpenAi:Models:{TextStep.Novelty}";

    /// <summary>The configured model of the novelty step, with the adapter's absent-key default.</summary>
    public string NoveltyModel => configuration[NoveltyModelKey] is { Length: > 0 } model ? model : DefaultModel;
}
