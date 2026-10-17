using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
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
/// The idea-generation step (02 README D6/D7/D21, 02.8.1-T1 plan §7.1): build the one data object, take the
/// <c>ideas/generate.v1</c> step prompt and the <c>ideas.v1</c> schema from the catalog, make the call and return
/// the outcome as data.
/// </summary>
/// <remarks>
/// <para>
/// The step is <see cref="TextStep.Ideas"/> — whose <c>OpenAi:Models:Ideas</c> / <c>OpenAi:Effort:Ideas</c> /
/// <c>OpenAi:RequestTimeout:Ideas</c> keys the Worker's configuration carries — and its D7 correlation id is
/// therefore <c>&lt;generation_run_id&gt;:ideas:&lt;attempt&gt;</c>, set by <see cref="ClientRequestIdHandler"/>
/// from the ambient scope. The provider-side switches the plan names (<c>store = false</c>,
/// <c>safety_identifier = sha256(userId)</c>) are derived by <see cref="OpenAiTextClient"/> for every call, so
/// this class never restates them.
/// </para>
/// <para>
/// Nothing here catches a provider outcome: every one is a <see cref="TextCallResult{T}"/> value returned
/// untouched, so the planner maps it (D7). The one exception is a cancellation: an
/// <see cref="OperationCanceledException"/> raised after the request was dispatched is metered first — the
/// provider can still bill a request that was sent — and then rethrown, exactly as <c>OpenAiBibleProposalGenerator</c>
/// and <c>OpenAiFactExtractor</c> do (SEC-2).
/// </para>
/// <para>
/// The bound is resolved <em>before</em> the call, once: it carries the step's ruled output ceiling
/// (<c>price_versions.limits.ideas.max_output_tokens</c> — never a configuration fallback, 02 README T56) and the
/// same bound then prices the usage row (D1/D14), so the cap the reservation was taken at and the cap the
/// request sends can never disagree. A missing price version, an unshipped prompt or a data object over
/// <c>Story:Prompt:MaxDataObjectBytes</c> is an ordering/invariant error at this boundary: the call is not
/// dispatched and the failure is thrown rather than sent with unbounded or uncosted content.
/// </para>
/// <para>
/// D19: the log line the client writes carries identifiers, token counts and the mapped outcome; no data object,
/// bible text or note is ever passed to a logger here either.
/// </para>
/// </remarks>
/// <param name="client">Makes the structured-output call.</param>
/// <param name="catalog">Supplies the <c>ideas/generate.v1</c> step prompt and the <c>ideas.v1</c> schema.</param>
/// <param name="ideaOptions">The <c>Autopilot:Ideas</c> section: the candidate count and its ceiling.</param>
/// <param name="configuration">The data-object budget (<c>Story:Prompt:MaxDataObjectBytes</c>).</param>
/// <param name="meter">Records the D14 usage row of the call.</param>
/// <param name="estimator">The D1 bound, and the only source of the call's output ceiling.</param>
/// <param name="metrics">Records the duration of the dispatched call (plan §7.1).</param>
public sealed class OpenAiIdeaGenerator(
    ITextClient client,
    IPromptCatalog catalog,
    IOptions<IdeaOptions> ideaOptions,
    IConfiguration configuration,
    IUsageMeter meter,
    IdeaCostEstimator estimator,
    OpenAiIdeaMetrics metrics) : IIdeaGenerator
{
    /// <inheritdoc />
    public async ValueTask<TextCallResult<IdeasV1>> GenerateAsync(IdeaGenerationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = ideaOptions.Value;
        if (request.Count < 1 || request.Count > settings.MaxCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Count,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"An ideas call asks for 1..{settings.MaxCount} candidates ('{IdeaOptions.SectionName}:MaxCount')."));
        }

        var bound = await estimator.WorstCaseAsync(request.Run.PriceVersion, ct).ConfigureAwait(false);
        if (bound.IsError)
        {
            throw new InvalidOperationException(
                $"An ideas call cannot be dispatched without its D1 bound and its ruled max_output_tokens: {bound.FirstError.Code}.");
        }

        var json = IdeaDataObjectJson.Build(request);
        var maxBytes = configuration.GetValue(
            PromptDataBudget.ConfigurationKey,
            PromptDataBudget.DefaultMaxDataObjectBytes);
        var bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > maxBytes)
        {
            // Fail closed instead of truncating: a cut bible block would silently change what the model avoids.
            throw new IdeaDataObjectTooLargeException(bytes, maxBytes);
        }

        var call = new TextCallRequest(
            request.Run,
            TextStep.Ideas,
            Localise(catalog.GetStepPrompt(PromptStep.Ideas), LanguageName(request.Language)),
            json,
            IdeaPrompts.SchemaName,
            catalog.GetSchema(IdeaPrompts.SchemaFile),
            bound.Value.MaxOutputTokens,
            IdeaPrompts.CacheKey,
            // D5: the composite version names the files this release shipped for the step. The step prompt stands
            // in for the preset component (there is no preset here); the schema component distinguishes it.
            catalog.BuildPromptVersion(new PromptVersionParts(
                IdeaPrompts.SystemKey,
                IdeaPrompts.SystemKey,
                IdeaPrompts.SchemaKey)));

        var watch = Stopwatch.StartNew();
        TextCallResult<IdeasV1> result;
        try
        {
            result = await client.CallAsync<IdeasV1>(call, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (call.DispatchedModel is { } dispatchedModel)
        {
            // SEC-2: the caller gave up after the request may have been sent, and the provider can still bill it.
            // Record one estimated row at the D1 bound — on the meter's own token, so the settlement survives the
            // caller's cancellation and a shutdown — then let the cancellation through: the planner still decides
            // the run's outcome.
            watch.Stop();
            metrics.GenerationDuration(watch.Elapsed.TotalSeconds);

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
        finally
        {
            if (watch.IsRunning)
            {
                watch.Stop();
                metrics.GenerationDuration(watch.Elapsed.TotalSeconds);
            }
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
    /// Fills the one placeholder the idea prompt carries. Any other <c>{{…}}</c> left behind is a programming
    /// error (02 README D5: the placeholder set is closed), so it throws instead of reaching the model.
    /// </summary>
    private static string Localise(string template, string languageName)
    {
        var text = template.Replace("{{language_name}}", languageName, StringComparison.Ordinal);
        if (text.Contains("{{", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The idea prompt carries a placeholder outside the closed list (02 README D5).");
        }

        return text;
    }
}

/// <summary>
/// The idea data object exceeds <c>Story:Prompt:MaxDataObjectBytes</c>: the call is not sent (02.8.1-T1 plan §7.1).
/// </summary>
/// <remarks>
/// Carries the two byte counts and nothing of the content (D19), and derives from
/// <see cref="InvalidOperationException"/> because <c>OpenAiBibleProposalGenerator</c> states its own broken
/// invariants the same way: the planner must let it propagate (the run then fails without a paid call), never
/// catch it as a provider outcome.
/// </remarks>
public sealed class IdeaDataObjectTooLargeException : InvalidOperationException
{
    /// <summary>Creates the failure of one oversized data object.</summary>
    /// <param name="byteCount">The UTF-8 bytes of the serialised data object.</param>
    /// <param name="maxBytes">The configured budget the object exceeded.</param>
    public IdeaDataObjectTooLargeException(int byteCount, int maxBytes)
        : base(string.Create(
            CultureInfo.InvariantCulture,
            $"The idea data object is {byteCount} UTF-8 bytes, over Story:Prompt:MaxDataObjectBytes ({maxBytes}); the call is not sent."))
    {
        ByteCount = byteCount;
        MaxBytes = maxBytes;
    }

    /// <summary>The UTF-8 bytes of the serialised data object.</summary>
    public int ByteCount { get; }

    /// <summary>The configured budget the object exceeded.</summary>
    public int MaxBytes { get; }
}
