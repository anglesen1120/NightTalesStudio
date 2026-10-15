using ErrorOr;
using Microsoft.Extensions.Configuration;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Prompts;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Bible;

namespace NightTales.Infrastructure.Persistence.Usage;

/// <summary>
/// The worst-case price of one bible-proposal run (02 README D1/D14, 02.6.3-T2 plan §7.1): the run's one paid
/// call — <c>bible_proposal</c> — at its own <c>max_output_tokens</c> from the active price version, at that
/// version's model rates with the long-context surcharge.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately an upper bound, in the shape of <see cref="StoryCostEstimator"/>: input tokens are bounded by
/// UTF-8 <em>bytes</em> (P6), output by the cap, and nothing is assumed cached — the whole input is charged a
/// cache write as well (D14 example C). The bound's parts:
/// </para>
/// <list type="bullet">
/// <item><description>
/// the step prompt, at <c>6</c> bytes per UTF-16 unit — the worst case of any string in this data object, since
/// the writer escapes only <c>"</c>, <c>\</c> and control characters, and a control character costs six;
/// </description></item>
/// <item><description>
/// the approved story body, which the generator truncates to <c>Bible:Proposal:MaxStoryChars</c> <em>from the
/// end</em> (plan §7.1 step 3), so that many characters is the whole of it;
/// </description></item>
/// <item><description>
/// the bible block, bounded by <c>Bible:Limits:MaxContentBytes</c> — every stored document passed the same
/// validator, and the data object carries a strict subset of it (no ids, no <c>schema_version</c>, no
/// structure, snake_case with an encoder that writes non-ASCII readably, so never more bytes than the
/// canonical document it was measured as);
/// </description></item>
/// <item><description>
/// the episode's control values, its title and its D3 structure tags, whose beats are bounded by the widest
/// <c>MaxSegments</c> of the active price version's story cells.
/// </description></item>
/// </list>
/// <para>
/// The ceiling is <em>not</em> a configuration key (README T56): it is
/// <c>price_versions.limits.bible_proposal.max_output_tokens</c>, the same value the generator sends, and this
/// bound reports it so the job and the reservation cannot disagree with the call.
/// </para>
/// <para>
/// Registering this type is what the Api needs: it composes <c>AddUsageMetering</c> but not the text client, so
/// the estimator reads <c>OpenAi:Models:Bible</c> straight from configuration with the adapter's own default —
/// the same value <see cref="OpenAI.OpenAiOptions"/> holds when the key is absent.
/// </para>
/// </remarks>
/// <param name="catalog">The active price version.</param>
/// <param name="prompts">Supplies the <c>bible/propose.v1</c> step prompt, whose incoming tokens are the system prefix.</param>
/// <param name="configuration">The <c>Bible</c> ceilings and the step's model.</param>
/// <param name="clock">The instant the active price version is read at.</param>
public sealed class BibleProposalCostEstimator(
    IPriceCatalog catalog,
    IPromptCatalog prompts,
    IConfiguration configuration,
    IClock clock) : IBibleProposalCostEstimator
{
    /// <summary>The model of the step when <c>OpenAi:Models:Bible</c> is absent (02 plan §OpenAI adapter).</summary>
    public const string DefaultModel = "gpt-6-luna";

    /// <summary>The worst-case UTF-8 bytes, JSON escaping included, of one UTF-16 unit.</summary>
    private const long BytesPerChar = 6;

    /// <summary>The bytes per estimated token (the 02.5.3 fact-budget convention).</summary>
    private const long BytesPerEstimatedToken = 4;

    /// <summary>The characters one structure beat is allowed in the estimate; a beat is a word.</summary>
    private const long BeatChars = 64;

    /// <summary>The characters of the data object outside its three measured blocks: the control values, the episode title and the JSON syntax.</summary>
    private const long EpisodeOverheadChars = 1_024;

    private static readonly BibleProposalOptions ProposalDefaults = new();
    private static readonly BibleLimits LimitDefaults = new();

    /// <inheritdoc />
    public async Task<ErrorOr<UsageBound>> WorstCaseAsync(CancellationToken ct)
    {
        var active = await catalog.GetActiveAsync(clock.UtcNow, ct).ConfigureAwait(false);
        if (active.IsError)
        {
            return active.Errors;
        }

        var version = active.Value;
        var rates = version.ParseRates();
        var limits = version.ParseLimits();

        int promptChars;
        try
        {
            promptChars = prompts.GetStepPrompt(PromptStep.Bible).Length;
        }
        catch (PromptCatalogException exception)
        {
            return Error.Failure(ErrorCodes.PromptNotShipped, exception.Message);
        }

        var maxStoryChars = configuration.GetValue(
            $"{BibleOptions.SectionName}:{nameof(BibleOptions.Proposal)}:{nameof(BibleProposalOptions.MaxStoryChars)}",
            ProposalDefaults.MaxStoryChars);
        var maxContentBytes = configuration.GetValue(
            $"{BibleOptions.SectionName}:{nameof(BibleOptions.Limits)}:{nameof(BibleLimits.MaxContentBytes)}",
            LimitDefaults.MaxContentBytes);

        var inputBytes = (BytesPerChar * ((long)promptChars + maxStoryChars))
            + maxContentBytes
            + (BytesPerChar * ((long)MaxSegments(limits) * BeatChars + EpisodeOverheadChars));
        var input = (int)((inputBytes + BytesPerEstimatedToken - 1) / BytesPerEstimatedToken);
        var maxOutput = limits.BibleProposalMaxOutput;

        var cost = TextCostCalculator.Compute(
            rates.RatesFor(ModelOf(nameof(TextStep.Bible))),
            input,
            0,
            input,
            maxOutput,
            rates.SurchargeThresholdTokens)
            ?? throw new InvalidOperationException("The calculator returned no cost for a bounded call.");

        return new UsageBound(cost, input, maxOutput);
    }

    /// <summary>The widest segment band of the active price version's story cells, so the beats are bounded.</summary>
    private static int MaxSegments(PriceLimits limits)
    {
        var widest = 0;
        foreach (var language in limits.Story.Values)
        {
            foreach (var cell in language.Values)
            {
                widest = Math.Max(widest, cell.MaxSegments);
            }
        }

        return widest;
    }

    private string ModelOf(string step) =>
        configuration[$"OpenAi:Models:{step}"] is { Length: > 0 } model ? model : DefaultModel;
}
