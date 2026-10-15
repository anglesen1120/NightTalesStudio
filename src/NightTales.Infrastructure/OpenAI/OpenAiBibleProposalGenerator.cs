using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.SeriesBible;
using NightTales.Application.Features.SeriesBible.Abstractions;
using NightTales.Domain.Stories.Prompts;
using NightTales.Infrastructure.Bible;

namespace NightTales.Infrastructure.OpenAI;

/// <summary>
/// The bible-proposal step (02 README D7/D21, 02.6.3-T2 plan §7.1 steps 3–4): build the one D6 data object, take
/// the <c>bible/propose.v1</c> step prompt and the <c>bible-proposal.v1</c> schema from the catalog, make the
/// call and return the outcome as data.
/// </summary>
/// <remarks>
/// <para>
/// The step is <see cref="TextStep.Bible"/> — the enum member 02.2.1-T2 declared for it, whose
/// <c>OpenAi:Models:Bible</c> / <c>OpenAi:Effort:Bible</c> / <c>OpenAi:RequestTimeout:Bible</c> keys the Worker's
/// configuration already carries — and its R14 correlation id is therefore
/// <c>&lt;generation_run_id&gt;:bible:&lt;attempt&gt;</c> (D7's <c>X-Client-Request-Id</c>, set by
/// <see cref="ClientRequestIdHandler"/> from the ambient scope).
/// </para>
/// <para>
/// Nothing here catches a provider outcome: every one is a <see cref="TextCallResult{T}"/> value returned
/// untouched, so the job maps it (D7). The one exception is a cancellation: an
/// <see cref="OperationCanceledException"/> raised after the request was dispatched is metered first — the
/// provider can still bill a request that was sent — and then rethrown, exactly as <c>OpenAiStoryWriter</c> and
/// <c>OpenAiFactExtractor</c> do (SEC-2).
/// </para>
/// <para>
/// Unlike the story steps, the bound is resolved <em>before</em> the call, once: it carries the step's ruled
/// output ceiling (<c>price_versions.limits.bible_proposal.max_output_tokens</c> — never a configuration
/// fallback, README T56) and the same bound then prices the usage row (D1/D14), so the cap the reservation was
/// taken at and the cap the request sends can never disagree. A missing price version or an unshipped prompt is
/// a programming error at this boundary: the job checks the budget with the same estimator before it enqueues,
/// so it must throw rather than silently call with an unbounded or uncosted request.
/// </para>
/// <para>
/// D19: the log line the client writes carries identifiers, token counts and the mapped outcome; no data object,
/// bible text or story text is ever passed to a logger here either.
/// </para>
/// </remarks>
/// <param name="client">Makes the structured-output call.</param>
/// <param name="catalog">Supplies the <c>bible/propose.v1</c> step prompt and the <c>bible-proposal.v1</c> schema.</param>
/// <param name="bibleOptions">The <c>Bible</c> section: the summary window and the story-body ceiling.</param>
/// <param name="meter">Records the D14 usage row of the call.</param>
/// <param name="estimator">The D1 bound, and the only source of the call's output ceiling.</param>
/// <param name="metrics">Counts a body cut to the prompt ceiling (plan §7.1 step 3).</param>
public sealed class OpenAiBibleProposalGenerator(
    ITextClient client,
    IPromptCatalog catalog,
    IOptions<BibleOptions> bibleOptions,
    IUsageMeter meter,
    IBibleProposalCostEstimator estimator,
    OpenAiBibleProposalMetrics metrics) : IBibleProposalGenerator
{
    /// <inheritdoc />
    public async ValueTask<TextCallResult<BibleProposalV1>> GenerateAsync(BibleProposalRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = bibleOptions.Value;

        var bound = await estimator.WorstCaseAsync(ct).ConfigureAwait(false);
        if (bound.IsError)
        {
            throw new InvalidOperationException(
                $"A bible-proposal call cannot be dispatched without its D1 bound and its ruled max_output_tokens: {bound.FirstError.Code}.");
        }

        var payload = BibleProposalDataObjectJson.Build(request, settings.Prompt.MaxSummaries, settings.Proposal.MaxStoryChars);
        if (payload.BodyTruncated)
        {
            metrics.BodyTruncated();
        }

        var call = new TextCallRequest(
            request.Run,
            TextStep.Bible,
            Localise(catalog.GetStepPrompt(PromptStep.Bible), LanguageName(request.Language)),
            payload.Json,
            BibleProposalPrompts.SchemaName,
            catalog.GetSchema(BibleProposalPrompts.SchemaFile),
            bound.Value.MaxOutputTokens,
            BibleProposalPrompts.CacheKey,
            // D5: the composite version names the files this release shipped for the step. The proposal is not
            // preset-based, so its own step prompt stands in for the preset component too; the schema component
            // is what distinguishes it.
            catalog.BuildPromptVersion(new PromptVersionParts(
                BibleProposalPrompts.SystemKey,
                BibleProposalPrompts.SystemKey,
                BibleProposalPrompts.SchemaKey)));

        TextCallResult<BibleProposalV1> result;
        try
        {
            result = await client.CallAsync<BibleProposalV1>(call, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (call.DispatchedModel is { } dispatchedModel)
        {
            // SEC-2: the caller gave up after the request may have been sent, and the provider can still bill it.
            // Record one estimated row at the D1 bound — on the meter's own token, so the settlement survives the
            // caller's cancellation and a shutdown — then let the cancellation through: the job still decides the
            // run's outcome.
            await meter
                .RecordAsync(
                    TextCallResultUsageExtensions.CancelledCallUsageItem(
                        request.Run,
                        BibleProposalOperations.BibleProposal,
                        request.Run.EpisodeId,
                        dispatchedModel,
                        bound.Value.WorstCaseUsd),
                    CancellationToken.None)
                .ConfigureAwait(false);

            throw;
        }

        // A call that cannot have been billed (no request sent, or a definitive pre-generation rejection) is not
        // metered (SEC-1). Every other outcome is, failures included: a failed generation keeps its cost (D14).
        if (!result.MayBeBilled)
        {
            return result;
        }

        await meter
            .RecordAsync(
                result.ToUsageItem(
                    request.Run,
                    BibleProposalOperations.BibleProposal,
                    request.Run.EpisodeId,
                    bound.Value.WorstCaseUsd),
                CancellationToken.None)
            .ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// The series' language as the prompt names it (<c>{{language_name}}</c>, same spelling as
    /// <c>PromptAssembler</c>). The request carries the snake_case control value, which is always <c>vi</c> or <c>en</c>.
    /// </summary>
    private static string LanguageName(string language) => language switch
    {
        "vi" => "Vietnamese",
        "en" => "English",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Unknown content language."),
    };

    /// <summary>
    /// Fills the one placeholder the proposal prompt carries. Any other <c>{{…}}</c> left behind is a
    /// programming error (02 README D5: the placeholder set is closed), so it throws instead of reaching the model.
    /// </summary>
    private static string Localise(string template, string languageName)
    {
        var text = template.Replace("{{language_name}}", languageName, StringComparison.Ordinal);
        if (text.Contains("{{", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The bible proposal prompt carries a placeholder outside the closed list (02 README D5).");
        }

        return text;
    }
}
