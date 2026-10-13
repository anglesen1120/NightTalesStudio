using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.Stories.Review;
using NightTales.Domain.Stories.Content;

namespace NightTales.Application.Features.Stories.Writing;

public sealed partial class OpenAiStoryWriter
{
    private const string RevisionSchemaName = "story.v1";
    private const string RevisionSchemaFile = "story.v1.json";
    private const string RevisionOperation = "story_revision";

    /// <inheritdoc />
    public async ValueTask<TextCallResult<StoryContentV1>> ReviseAsync(StoryRevisionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var assembled = assembler.Revise(req);
        if (assembled.IsError)
        {
            // S4: nothing was sent, so nothing is metered. Same class as the assembler failures WriteAsync lets escape:
            // the prompt cannot be built from this input, a deploy or configuration defect, not an outcome of a provider call.
            throw new InvalidOperationException($"The revision prompt cannot be assembled: {assembled.FirstError.Code}.");
        }

        var prompt = assembled.Value;

        var call = new TextCallRequest(
            req.Run,
            TextStep.Revision,
            prompt.SystemPrompt,
            prompt.UserDataJson,
            RevisionSchemaName,
            catalog.GetSchema(RevisionSchemaFile),
            req.Length.RevisionMaxOutput,
            prompt.PromptCacheKey,
            prompt.PromptVersion);

        TextCallResult<StoryContentV1> result;
        try
        {
            result = await client.CallAsync<StoryContentV1>(call, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (call.DispatchedModel is { } dispatchedModel)
        {
            // Same rule as the writer and review steps (02.2.1-T2 SEC-2): the provider can still bill a request that was sent.
            var cancelledBound = await estimator.RevisionWorstCaseAsync(req.Tier, req.Language, CancellationToken.None).ConfigureAwait(false);
            if (!cancelledBound.IsError)
            {
                await meter.RecordAsync(
                    TextCallResultUsageExtensions.CancelledCallUsageItem(req.Run, RevisionOperation, req.Run.EpisodeId, dispatchedModel, cancelledBound.Value.WorstCaseUsd),
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        if (!result.MayBeBilled)
        {
            return result;
        }

        var worst = await estimator.RevisionWorstCaseAsync(req.Tier, req.Language, CancellationToken.None).ConfigureAwait(false);
        if (worst.IsError && result.Usage is null)
        {
            throw new InvalidOperationException(
                $"The D1 bound of a call without usage is unavailable: {worst.FirstError.Code}.");
        }

        decimal? bound = worst.IsError ? null : worst.Value.WorstCaseUsd;

        await meter.RecordAsync(result.ToUsageItem(req.Run, RevisionOperation, req.Run.EpisodeId, bound), CancellationToken.None).ConfigureAwait(false);

        return result;
    }
}
