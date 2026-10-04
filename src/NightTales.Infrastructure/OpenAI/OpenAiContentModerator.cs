using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Usage;
using Polly.Registry;
using Polly.Timeout;
using ModerationClient = OpenAI.Moderations.ModerationClient;

namespace NightTales.Infrastructure.OpenAI;

/// <summary>
/// The moderation port over <c>POST /v1/moderations</c> with <c>omni-moderation-latest</c> (02 README D12, O7;
/// the call is free). Every text is split to at most <see cref="ModerationOptions.MaxCharsPerChunk"/> characters,
/// all chunks travel in one request, and a flag in any chunk flags the result (the union of the categories).
/// </summary>
/// <remarks>
/// <para>
/// The request is sent through the protocol method and the response read as JSON: the typed SDK result has no
/// <c>illicit/violent</c>, which is a D12 blocking category. The call has the same resilience shape as the text
/// calls (<see cref="OpenAiServiceCollectionExtensions.ConfigurePipeline"/>) with the 15 s moderation timeout.
/// Any failure after the retry budget throws <see cref="ModerationUnavailableException"/>: fail closed, never a
/// silent pass. One <c>usage_records</c> row (<c>operation = moderation</c>, cost 0, estimated) is written per
/// answered call when a <see cref="ModerationScope"/> names the run.
/// </para>
/// <para>
/// Logging (D19): identifiers, counts and the outcome only. No checked text, category-bearing body or provider
/// message is ever passed to the logger. Public, like the text client, because this solution has no
/// <c>InternalsVisibleTo</c>.
/// </para>
/// </remarks>
/// <param name="client">The moderation client over the <c>"openai"</c> HttpClient, zero SDK retries.</param>
/// <param name="meter">The usage meter that writes the free-call row.</param>
/// <param name="options">The model, chunk cap and timeout.</param>
/// <param name="pipelines">The resilience pipelines; the moderation one is keyed <see cref="PipelineKey"/>.</param>
/// <param name="log">The call log.</param>
public sealed partial class OpenAiContentModerator(
    ModerationClient client,
    IUsageMeter meter,
    ModerationOptions options,
    ResiliencePipelineProvider<string> pipelines,
    ILogger<OpenAiContentModerator> log) : IContentModerator
{
    /// <summary>The resilience pipeline key of the moderation call.</summary>
    public const string PipelineKey = "openai-moderation";

    /// <summary>The provider the free call is metered under.</summary>
    public const string Provider = "openai";

    /// <summary>The D14 operation of the free call.</summary>
    public const string Operation = "moderation";

    /// <inheritdoc />
    public async ValueTask<ModerationResult> CheckAsync(IReadOnlyList<string> texts, ModerationStage stage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var chunks = Chunk(texts, options.MaxCharsPerChunk);
        if (chunks.Count == 0)
        {
            return ModerationResult.Clean;
        }

        var body = BuildBody(options.Model, chunks);

        try
        {
            var response = await pipelines.GetPipeline(PipelineKey)
                .ExecuteAsync(
                    async token =>
                    {
                        using var content = BinaryContent.Create(BinaryData.FromBytes(body));

                        return await client.ClassifyTextAsync(content, new RequestOptions { CancellationToken = token }).ConfigureAwait(false);
                    },
                    ct)
                .ConfigureAwait(false);

            var raw = response.GetRawResponse();
            var result = Parse(raw.Content);
            var requestId = raw.Headers.TryGetValue("x-request-id", out var id) ? id : null;

            LogCall(stage, chunks.Count, result.Flagged, result.Categories.Count, requestId);
            await RecordUsageAsync(requestId, ct).ConfigureAwait(false);

            return result;
        }
        catch (ClientResultException ex)
        {
            LogFailed(stage, ex.Status);

            throw new ModerationUnavailableException(ex.Status);
        }
        catch (Exception ex) when (ex is TimeoutRejectedException or HttpRequestException or JsonException or InvalidDataException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // The type only, never the message: a provider message can echo the request text (D19).
            LogFailed(stage, null);

            throw new ModerationUnavailableException(null);
        }
    }

    /// <summary>
    /// Splits every non-blank text into pieces of at most <paramref name="maxChars"/> characters, never between the
    /// two halves of a surrogate pair.
    /// </summary>
    /// <param name="texts">The texts.</param>
    /// <param name="maxChars">The cap per piece.</param>
    /// <returns>The pieces, in order.</returns>
    public static List<string> Chunk(IReadOnlyList<string> texts, int maxChars)
    {
        var chunks = new List<string>();

        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var start = 0;
            while (start < text.Length)
            {
                var length = Math.Min(maxChars, text.Length - start);
                if (length < text.Length - start && length > 1 && char.IsHighSurrogate(text[start + length - 1]))
                {
                    length--;
                }

                chunks.Add(text.Substring(start, length));
                start += length;
            }
        }

        return chunks;
    }

    private static byte[] BuildBody(string model, List<string> chunks)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteStartArray("input");
            foreach (var chunk in chunks)
            {
                writer.WriteStringValue(chunk);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>The union verdict of a <c>results[]</c> body: a flag in any result flags it; the highest score per category wins.</summary>
    private static ModerationResult Parse(BinaryData content)
    {
        using var doc = JsonDocument.Parse(content);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
        {
            // Nothing usable is not "clean": fail closed.
            throw new InvalidDataException("The moderation response carries no results.");
        }

        var flagged = false;
        var categories = new SortedSet<string>(StringComparer.Ordinal);
        var scores = new SortedDictionary<string, double>(StringComparer.Ordinal);

        foreach (var result in results.EnumerateArray())
        {
            if (result.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("A moderation result is not an object.");
            }

            flagged |= result.TryGetProperty("flagged", out var f) && f.ValueKind == JsonValueKind.True;

            if (result.TryGetProperty("categories", out var flags) && flags.ValueKind == JsonValueKind.Object)
            {
                foreach (var category in flags.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.True))
                {
                    categories.Add(category.Name);
                }
            }

            if (result.TryGetProperty("category_scores", out var values) && values.ValueKind == JsonValueKind.Object)
            {
                foreach (var score in values.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number))
                {
                    var value = score.Value.GetDouble();
                    scores[score.Name] = scores.TryGetValue(score.Name, out var best) ? Math.Max(best, value) : value;
                }
            }
        }

        // A category flagged without the flag set still flags: a chunked flag is a flag.
        return new ModerationResult(flagged || categories.Count > 0, [.. categories], scores);
    }

    private async Task RecordUsageAsync(string? requestId, CancellationToken ct)
    {
        if (ModerationScope.Current is not { } run)
        {
            LogUnmetered();

            return;
        }

        try
        {
            // The endpoint is free and returns no usage: estimated, cost 0 (D12, D14).
            await meter.RecordAsync(
                new UsageItem(
                    run.UserId,
                    Provider,
                    Operation,
                    options.Model,
                    InputTokens: null,
                    CachedInputTokens: null,
                    CacheWriteTokens: null,
                    OutputTokens: null,
                    run.Reservation.Value,
                    run.EpisodeId,
                    Estimated: true,
                    CostUsd: 0m,
                    run.PriceVersion,
                    requestId,
                    run.SeriesId,
                    run.GenerationRunId),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The check already answered; a lost bookkeeping row must not turn a verdict into a failure.
            LogMeterFailed(ex.GetType().Name);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "OpenAI moderation stage {Stage} chunks {Chunks} flagged {Flagged} categories {Categories} request_id {RequestId}")]
    private partial void LogCall(ModerationStage stage, int chunks, bool flagged, int categories, string? requestId);

    [LoggerMessage(Level = LogLevel.Error, Message = "OpenAI moderation stage {Stage} failed closed with status {Status}")]
    private partial void LogFailed(ModerationStage stage, int? status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OpenAI moderation call outside a ModerationScope: not metered")]
    private partial void LogUnmetered();

    [LoggerMessage(Level = LogLevel.Error, Message = "OpenAI moderation usage row not written: {ExceptionType}")]
    private partial void LogMeterFailed(string exceptionType);
}
