using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.Stories.Review;
using NightTales.Domain.Stories.Review;

namespace NightTales.Application.Features.Stories.Writing;

public sealed partial class OpenAiStoryWriter
{
    private const string ReviewSchemaName = "review.v1";
    private const string ReviewSchemaFile = "review.v1.json";
    private const string ReviewOperation = "story_review";

    /// <inheritdoc />
    public async ValueTask<TextCallResult<StoryReviewV1>> ReviewAsync(StoryReviewRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var assembled = assembler.Review(req);
        if (assembled.IsError)
        {
            // S4: nothing was sent, so nothing is metered. Same class as the assembler failures WriteAsync lets escape (Guard's
            // ArgumentException, Substitute's InvalidOperationException): the prompt cannot be built from this input, a deploy or
            // configuration defect, not an outcome of a provider call, and TextCallResult has no variant for it.
            throw new InvalidOperationException($"The review prompt cannot be assembled: {assembled.FirstError.Code}.");
        }

        var prompt = assembled.Value;

        var call = new TextCallRequest(
            req.Run,
            TextStep.Review,
            prompt.SystemPrompt,
            prompt.UserDataJson,
            ReviewSchemaName,
            catalog.GetSchema(ReviewSchemaFile),
            req.Length.ReviewMaxOutput,
            prompt.PromptCacheKey,
            prompt.PromptVersion);

        TextCallResult<StoryReviewV1> result;
        try
        {
            result = await client.CallAsync<StoryReviewV1>(call, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (call.DispatchedModel is { } dispatchedModel)
        {
            // Same rule as the writer step (02.2.1-T2 SEC-2): the provider can still bill a request that was sent.
            var cancelledBound = await estimator.ReviewWorstCaseAsync(req.Tier, req.Language, CancellationToken.None).ConfigureAwait(false);
            if (!cancelledBound.IsError)
            {
                await meter.RecordAsync(
                    TextCallResultUsageExtensions.CancelledCallUsageItem(req.Run, ReviewOperation, req.Run.EpisodeId, dispatchedModel, cancelledBound.Value.WorstCaseUsd),
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        if (!result.MayBeBilled)
        {
            return result;
        }

        var worst = await estimator.ReviewWorstCaseAsync(req.Tier, req.Language, CancellationToken.None).ConfigureAwait(false);
        if (worst.IsError && result.Usage is null)
        {
            throw new InvalidOperationException(
                $"The D1 bound of a call without usage is unavailable: {worst.FirstError.Code}.");
        }

        decimal? bound = worst.IsError ? null : worst.Value.WorstCaseUsd;

        await meter.RecordAsync(result.ToUsageItem(req.Run, ReviewOperation, req.Run.EpisodeId, bound), CancellationToken.None).ConfigureAwait(false);

        return result;
    }
}
