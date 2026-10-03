using NightTales.Application.Abstractions.Usage;

namespace NightTales.Application.Abstractions.Text;

/// <summary>Maps a call's outcome to the <see cref="UsageItem"/> the meter writes (02 README D14).</summary>
public static class TextCallResultUsageExtensions
{
    /// <summary>The provider name every text call is metered under.</summary>
    public const string OpenAiProvider = "openai";

    /// <summary>Builds the usage row of one call.</summary>
    /// <typeparam name="T">The output document type.</typeparam>
    /// <param name="result">The call's outcome.</param>
    /// <param name="run">The run of the call.</param>
    /// <param name="operation">The D14 operation, snake_case.</param>
    /// <param name="episodeId">The episode of the call, or <see langword="null"/>.</param>
    /// <param name="bound">The D1 bound of this call. Always supplied when available: required (&gt; 0) when the response
    /// carried no usage, and the meter's fallback when reported usage cannot be priced; ignored otherwise.</param>
    /// <returns>The item to record.</returns>
    public static UsageItem ToUsageItem<T>(
        this TextCallResult<T> result,
        RunContext run,
        string operation,
        Guid? episodeId,
        decimal? bound)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(run);

        var usage = result.Usage;

        return new UsageItem(
            run.UserId,
            OpenAiProvider,
            operation,
            result.Model,
            usage?.InputTokens,
            usage?.CachedInputTokens,
            usage?.CacheWriteTokens,
            usage?.OutputTokens,
            run.Reservation.Value,
            episodeId,
            Estimated: usage is null,
            CostUsd: bound,
            run.PriceVersion,
            result.RequestId,
            run.SeriesId,
            run.GenerationRunId);
    }

    /// <summary>
    /// The row of a call cancelled by its caller after the request may have been sent: no outcome, so no usage;
    /// written estimated at the D1 bound.
    /// </summary>
    /// <param name="run">The run of the call.</param>
    /// <param name="operation">The D14 operation, snake_case.</param>
    /// <param name="episodeId">The episode of the call, or <see langword="null"/>.</param>
    /// <param name="model">The model the request was dispatched to.</param>
    /// <param name="bound">The D1 bound.</param>
    /// <returns>The item to record.</returns>
    public static UsageItem CancelledCallUsageItem(RunContext run, string operation, Guid? episodeId, string model, decimal bound)
    {
        ArgumentNullException.ThrowIfNull(run);

        return new UsageItem(
            run.UserId,
            OpenAiProvider,
            operation,
            model,
            null,
            null,
            null,
            null,
            run.Reservation.Value,
            episodeId,
            Estimated: true,
            CostUsd: bound,
            run.PriceVersion,
            null,
            run.SeriesId,
            run.GenerationRunId);
    }
}
