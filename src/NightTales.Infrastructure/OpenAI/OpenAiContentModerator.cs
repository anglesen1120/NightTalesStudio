using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
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
/// Verdict rules (D12, SEC-01): a result with <c>flagged: true</c> and no category of the omni table
/// (<see cref="ModerationOptions.KnownCategories"/>) gets the <see cref="ModerationOptions.UnclassifiedCategory"/>
/// sentinel, which <see cref="ModerationOptions.Classify"/> treats as blocking; an unknown category name next to a
/// known one stays advisory. The port never lets anything but <see cref="ModerationUnavailableException"/> (or the
/// caller's own cancellation) escape: building the request, parsing the answer and reading its scores all map to it
/// (SEC-02). A lone surrogate in a text is replaced with U+FFFD and the replaced text is still moderated.
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

    /// <summary>The characters two adjacent pieces of a long text share, so a phrase over a cut is whole in one of them (SEC-03).</summary>
    public const int ChunkOverlap = 200;

    /// <inheritdoc />
    public async ValueTask<ModerationResult> CheckAsync(IReadOnlyList<string> texts, ModerationStage stage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var chunks = Chunk(texts, options.MaxCharsPerChunk);
        if (chunks.Count == 0)
        {
            return ModerationResult.Clean;
        }

        try
        {
            var body = BuildBody(options.Model, chunks);
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
            var result = Parse(raw.Content, chunks.Count);
            var requestId = raw.Headers.TryGetValue("x-request-id", out var id) ? id : null;

            LogCall(stage, chunks.Count, result.Flagged, result.Categories.Count, requestId);
            await RecordUsageAsync(requestId, ct).ConfigureAwait(false);

            return result;
        }
        catch (ModerationUnavailableException)
        {
            // An unusable answer (no verdict, wrong result count): fail closed, type only (D19).
            LogFailed(stage, null);

            throw;
        }
        catch (ClientResultException ex)
        {
            LogFailed(stage, ex.Status);

            throw new ModerationUnavailableException(ex.Status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // SEC-02: a timeout, a transport failure, a malformed answer, an unwritable request or an unreadable score
            // all fail closed as the one typed exception. Only the caller's own cancellation passes through. The type
            // only, never the message: a provider message can echo the request text (D19).
            LogFailed(stage, null);

            throw new ModerationUnavailableException(null);
        }
    }

    /// <summary>
    /// Splits every non-blank text into pieces of at most <paramref name="maxChars"/> characters (CR-3, SEC-03).
    /// A piece that has to be cut ends after the last whitespace of the final 20% of its window when there is one,
    /// and adjacent pieces overlap by <see cref="ChunkOverlap"/> characters (never more than <c>maxChars - 1</c>), so
    /// a phrase over a cut is whole in one piece. A piece never starts or ends inside a surrogate pair, and a lone
    /// surrogate is replaced with U+FFFD first (SEC-02), so every piece is well-formed UTF-16.
    /// </summary>
    /// <param name="texts">The texts.</param>
    /// <param name="maxChars">The cap per piece, at least <see cref="ModerationOptions.MinCharsPerChunk"/>.</param>
    /// <returns>The pieces, in order.</returns>
    public static List<string> Chunk(IReadOnlyList<string> texts, int maxChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, ModerationOptions.MinCharsPerChunk);

        var overlap = Math.Min(ChunkOverlap, maxChars - 1);
        var wordWindow = maxChars / 5;
        var chunks = new List<string>();

        foreach (var original in texts)
        {
            if (string.IsNullOrWhiteSpace(original))
            {
                continue;
            }

            var text = ReplaceLoneSurrogates(original);
            var start = 0;
            while (text.Length - start > maxChars)
            {
                var cut = CutPoint(text, start, maxChars, wordWindow);
                chunks.Add(text.Substring(start, cut - start));

                // Step back by the overlap, off the low half of a pair. The cut is at least 80% of the window, so
                // the next piece always starts after this one does.
                start = cut - overlap;
                if (char.IsLowSurrogate(text[start]))
                {
                    start--;
                }
            }

            chunks.Add(start == 0 ? text : text.Substring(start));
        }

        return chunks;
    }

    private static int CutPoint(string text, int start, int maxChars, int wordWindow)
    {
        var end = start + maxChars;
        for (var i = end - 1; i >= end - wordWindow; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return i + 1;
            }
        }

        return char.IsHighSurrogate(text[end - 1]) ? end - 1 : end;
    }

    private static string ReplaceLoneSurrogates(string text)
    {
        StringBuilder? replaced = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                replaced?.Append(c).Append(text[i + 1]);
                i++;

                continue;
            }

            if (char.IsSurrogate(c))
            {
                replaced ??= new StringBuilder(text.Length).Append(text, 0, i);
                replaced.Append('\uFFFD');

                continue;
            }

            replaced?.Append(c);
        }

        return replaced?.ToString() ?? text;
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

    /// <summary>
    /// The union verdict of a <c>results[]</c> body: a flag in any result flags it; the highest score per category wins.
    /// One result per sent chunk with a boolean <c>flagged</c> is required; anything else is not "clean" and fails closed.
    /// </summary>
    private static ModerationResult Parse(BinaryData content, int expectedResults)
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

        if (results.GetArrayLength() != expectedResults)
        {
            // An unchecked chunk must never pass silently.
            throw new ModerationUnavailableException(null);
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

            if (!result.TryGetProperty("flagged", out var f) || f.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new ModerationUnavailableException(null);
            }

            var resultFlagged = f.ValueKind == JsonValueKind.True;
            flagged |= resultFlagged;
            var knownCategory = false;

            if (result.TryGetProperty("categories", out var flags) && flags.ValueKind == JsonValueKind.Object)
            {
                foreach (var category in flags.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.True))
                {
                    categories.Add(category.Name);
                    knownCategory |= ModerationOptions.KnownCategories.Contains(category.Name);
                }
            }

            // SEC-01: flagged with no category the omni table knows (absent, all false, or renamed by the provider)
            // is not "clean" and not merely advisory: the sentinel makes Classify block approval. An unknown name
            // beside a known category stays advisory; a flagged result with only unknown names gets the sentinel too.
            if (resultFlagged && !knownCategory)
            {
                categories.Add(ModerationOptions.UnclassifiedCategory);
            }

            if (result.TryGetProperty("category_scores", out var values) && values.ValueKind == JsonValueKind.Object)
            {
                foreach (var score in values.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number))
                {
                    // SEC-02: a number outside double range is an unusable answer, not an untyped FormatException.
                    if (!score.Value.TryGetDouble(out var value) || !double.IsFinite(value))
                    {
                        throw new InvalidDataException("A moderation score is not a finite number.");
                    }

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
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // CR-1: the check already answered; a lost bookkeeping row must not turn a verdict into a failure. That
            // includes the meter's own settlement timeout, which surfaces as an OperationCanceledException while the
            // caller's token is still live. Only the caller's own cancellation propagates.
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
