using System.Text;
using Microsoft.Extensions.Configuration;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Stories.Prompts;
using NightTales.Infrastructure.Autopilot;

namespace NightTales.Infrastructure.OpenAI;

/// <summary>
/// The idea-novelty judge step (02 README D6/D7/D21, 02.8.1-T2 task Design): build the one data object, take the
/// <c>ideas/novelty.v1</c> step prompt and the <c>novelty.v1</c> schema from the catalog, make the call and return
/// the outcome as data.
/// </summary>
/// <remarks>
/// <para>
/// The step is <see cref="TextStep.Novelty"/> — whose <c>OpenAi:Models:Novelty</c> / <c>OpenAi:Effort:Novelty</c> /
/// <c>OpenAi:RequestTimeout:Novelty</c> keys the Worker's configuration carries — and its D7 correlation id is
/// therefore <c>&lt;generation_run_id&gt;:novelty:&lt;attempt&gt;</c>, set by <see cref="ClientRequestIdHandler"/>
/// from the ambient scope. The provider-side switches the plan names (<c>store = false</c>,
/// <c>safety_identifier = sha256(userId)</c>) are derived by <see cref="OpenAiTextClient"/> for every call, so
/// this class never restates them.
/// </para>
/// <para>
/// <b>One attempt.</b> The judge makes exactly one paid call; the D7 retry for an invalid answer lives in the
/// Application checker (<c>IdeaNoveltyChecker</c>), which calls this again with <c>attempt = 2</c>. Nothing here
/// catches a provider outcome: every one is a <see cref="TextCallResult{T}"/> value returned untouched, so the
/// checker maps it (novelty failure degrades the run rather than failing it). The one exception is a
/// cancellation: an <see cref="OperationCanceledException"/> raised after the request was dispatched is metered
/// first — the provider can still bill a request that was sent — and then rethrown, exactly as
/// <c>OpenAiIdeaGenerator</c> and <c>OpenAiFactExtractor</c> do (SEC-2). A definitive pre-generation rejection
/// (<c>funds_exhausted</c>, no request sent) is never metered (SEC-1, D7).
/// </para>
/// <para>
/// The bound is resolved <em>before</em> the call, once: it carries the step's ruled output ceiling
/// (<c>price_versions.limits.novelty.max_output_tokens</c> — never a configuration fallback, 02 README T56) and the
/// same bound then prices the usage row (D1/D14), so the cap the reservation was taken at and the cap the
/// request sends can never disagree. A missing price version, an unshipped prompt or a data object over
/// <c>Story:Prompt:MaxDataObjectBytes</c> is an ordering/invariant error at this boundary: the call is not
/// dispatched and the failure is thrown rather than sent with unbounded or uncosted content.
/// </para>
/// <para>
/// D19: this class holds no logger — the client's log line carries identifiers, token counts and the mapped
/// outcome, and no candidate text, judge reason or episode summary is ever passed to a metric or a log here.
/// </para>
/// </remarks>
/// <param name="client">Makes the structured-output call.</param>
/// <param name="catalog">Supplies the <c>ideas/novelty.v1</c> step prompt and the <c>novelty.v1</c> schema.</param>
/// <param name="configuration">The data-object budget (<c>Story:Prompt:MaxDataObjectBytes</c>).</param>
/// <param name="meter">Records the D14 usage row of the call (<c>operation = idea</c>).</param>
/// <param name="estimator">The D1 bound, and the only source of the call's output ceiling.</param>
public sealed class OpenAiIdeaNoveltyJudge(
    ITextClient client,
    IPromptCatalog catalog,
    IConfiguration configuration,
    IUsageMeter meter,
    IdeaCostEstimator estimator) : IIdeaNoveltyJudge
{
    /// <inheritdoc />
    public async ValueTask<TextCallResult<NoveltyV1>> JudgeAsync(NoveltyJudgeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var bound = await estimator.NoveltyWorstCaseAsync(request.Run.PriceVersion, ct).ConfigureAwait(false);
        if (bound.IsError)
        {
            throw new InvalidOperationException(
                $"A novelty call cannot be dispatched without its D1 bound and its ruled max_output_tokens: {bound.FirstError.Code}.");
        }

        var json = NoveltyDataObjectJson.Build(request);
        var maxBytes = configuration.GetValue(
            PromptDataBudget.ConfigurationKey,
            PromptDataBudget.DefaultMaxDataObjectBytes);
        var bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > maxBytes)
        {
            // Fail closed instead of truncating: a cut corpus would silently change what the judge compares against.
            throw new IdeaDataObjectTooLargeException(bytes, maxBytes);
        }

        var call = new TextCallRequest(
            request.Run,
            TextStep.Novelty,
            Localise(catalog.GetStepPrompt(PromptStep.Novelty), LanguageName(request.Language)),
            json,
            NoveltyPrompts.SchemaName,
            catalog.GetSchema(NoveltyPrompts.SchemaFile),
            bound.Value.MaxOutputTokens,
            NoveltyPrompts.CacheKey,
            // D5: the composite version names the files this release shipped for the step. The step prompt stands
            // in for the preset component (there is no preset here); the schema component distinguishes it.
            catalog.BuildPromptVersion(new PromptVersionParts(
                NoveltyPrompts.SystemKey,
                NoveltyPrompts.SystemKey,
                NoveltyPrompts.SchemaKey)));

        TextCallResult<NoveltyV1> result;
        try
        {
            result = await client.CallAsync<NoveltyV1>(call, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (call.DispatchedModel is { } dispatchedModel)
        {
            // SEC-2: the caller gave up after the request may have been sent, and the provider can still bill it.
            // Record one estimated row at the D1 bound — on the meter's own token, so the settlement survives the
            // caller's cancellation and a shutdown — then let the cancellation through: the checker still decides
            // the run's outcome.
            await meter
                .RecordAsync(
                    TextCallResultUsageExtensions.CancelledCallUsageItem(
                        request.Run,
                        IdeaOperations.Idea,
                        episodeId: null,
                        dispatchedModel,
                        bound.Value.WorstCaseUsd),
                    CancellationToken.None)
                .ConfigureAwait(false);

            throw;
        }

        // A call that cannot have been billed (no request sent, or a definitive pre-generation rejection) is not
        // metered (SEC-1). Every other outcome is, failures included: a failed judge keeps its cost (D14).
        if (!result.MayBeBilled)
        {
            return result;
        }

        await meter
            .RecordAsync(
                result.ToUsageItem(
                    request.Run,
                    IdeaOperations.Idea,
                    episodeId: null,
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
    /// Fills the one placeholder the novelty prompt carries. Any other <c>{{…}}</c> left behind is a programming
    /// error (02 README D5: the placeholder set is closed), so it throws instead of reaching the model.
    /// </summary>
    private static string Localise(string template, string languageName)
    {
        var text = template.Replace("{{language_name}}", languageName, StringComparison.Ordinal);
        if (text.Contains("{{", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The novelty prompt carries a placeholder outside the closed list (02 README D5).");
        }

        return text;
    }
}
