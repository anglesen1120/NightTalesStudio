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
/// <param name="estimator">Supplies the D1 bound for a call whose response carried no usage.</param>
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

        var result = await client.CallAsync<StoryContentV1>(call, ct).ConfigureAwait(false);

        // Every outcome is metered, failures included: a failed generation keeps its cost (D14). A response
        // without usage is written estimated at the D1 bound, so the bound is only looked up then.
        decimal? bound = null;
        if (result.Usage is null)
        {
            var worst = await estimator.WorstCaseAsync(req.Episode.LengthTier, req.Series.Language, ct).ConfigureAwait(false);
            if (worst.IsError)
            {
                throw new InvalidOperationException(
                    $"The D1 bound of a call without usage is unavailable: {worst.FirstError.Code}.");
            }

            bound = worst.Value.WorstCaseUsd;
        }

        await meter.RecordAsync(result.ToUsageItem(req.Run, StoryOperation, req.Episode.EpisodeId, bound), ct).ConfigureAwait(false);

        return result;
    }
}
