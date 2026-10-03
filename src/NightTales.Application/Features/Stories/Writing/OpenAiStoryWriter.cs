using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Domain.Stories.Content;

namespace NightTales.Application.Features.Stories.Writing;

/// <summary>
/// The writer step (02.2.1-T2): assemble the prompt, make the call, meter it, return the outcome as data.
/// Reservation and budget are the command handler's (02.2.2-T2); validation and the invalid-output retry
/// are the job's.
/// </summary>
/// <param name="assembler">Builds the system prompt and the data object (02.2.3-T2).</param>
/// <param name="client">Makes the structured-output call.</param>
/// <param name="catalog">Supplies the <c>story.v1</c> schema.</param>
/// <param name="meter">Records the call in its own transaction (D14).</param>
/// <param name="estimator">Supplies the writer call's D1 bound: the estimate of a call whose response carried no usage, and the meter's fallback when reported usage cannot be priced.</param>
public sealed class OpenAiStoryWriter(
    IPromptAssembler assembler,
    ITextClient client,
    IPromptCatalog catalog,
    IUsageMeter meter,
    IStoryCostEstimator estimator) : IStoryWriter
{
    private const string SchemaName = "story.v1";
    private const string SchemaFile = "story.v1.json";
    private const string StoryOperation = "story";

    /// <inheritdoc />
    public async ValueTask<TextCallResult<StoryContentV1>> WriteAsync(StoryWriteRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var context = new AssemblerContext(
            req.Series.Topic,
            req.Series.Language,
            StoryPresetKeys.For(req.Series.Topic, req.Series.Language),
            req.Length.WriterMaxOutput);
        var prompt = assembler.Assemble(req, context);

        var call = new TextCallRequest(
            req.Run,
            TextStep.Writer,
            prompt.SystemPrompt,
            prompt.UserDataJson,
            SchemaName,
            catalog.GetSchema(SchemaFile),
            req.Length.WriterMaxOutput,
            prompt.PromptCacheKey,
            prompt.PromptVersion);

        TextCallResult<StoryContentV1> result;
        try
        {
            result = await client.CallAsync<StoryContentV1>(call, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (call.DispatchedModel is { } dispatchedModel)
        {
            // The caller gave up after the request may have been sent: the provider can still bill it. Settle an
            // estimated row at the bound on the meter's own token, then let the cancellation through (SEC-2).
            var cancelledBound = await estimator.WriterWorstCaseAsync(req.Episode.LengthTier, req.Series.Language, CancellationToken.None).ConfigureAwait(false);
            if (!cancelledBound.IsError)
            {
                await meter.RecordAsync(
                    TextCallResultUsageExtensions.CancelledCallUsageItem(req.Run, StoryOperation, req.Episode.EpisodeId, dispatchedModel, cancelledBound.Value.WorstCaseUsd),
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        // A call that cannot have been billed (no request sent, or a definitive pre-generation rejection) is not
        // metered (SEC-1). Every other outcome is, failures included: a failed generation keeps its cost (D14).
        if (!result.MayBeBilled)
        {
            return result;
        }

        // A result exists, so a call that may have been billed is settled whatever the caller does now: the bound
        // lookup and the row ignore the caller's token (the meter settles on its own deadline), and the completed
        // outcome is still returned.
        // The writer-step bound is the estimate when the response carried no usage and the meter's fallback when
        // the reported usage cannot be priced.
        var worst = await estimator.WriterWorstCaseAsync(req.Episode.LengthTier, req.Series.Language, CancellationToken.None).ConfigureAwait(false);
        if (worst.IsError && result.Usage is null)
        {
            throw new InvalidOperationException(
                $"The D1 bound of a call without usage is unavailable: {worst.FirstError.Code}.");
        }

        decimal? bound = worst.IsError ? null : worst.Value.WorstCaseUsd;

        await meter.RecordAsync(result.ToUsageItem(req.Run, StoryOperation, req.Episode.EpisodeId, bound), CancellationToken.None).ConfigureAwait(false);

        return result;
    }
}
