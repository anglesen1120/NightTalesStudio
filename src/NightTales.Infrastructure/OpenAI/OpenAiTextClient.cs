// The Responses API of OpenAI 2.14.0 is marked experimental (OPENAI001); 02 README O1 pins this exact version.
#pragma warning disable OPENAI001

using System.ClientModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Flags;
using NightTales.Application.Abstractions.Text;
using OpenAI.Responses;
using Polly.Registry;
using Polly.Timeout;

namespace NightTales.Infrastructure.OpenAI;

/// <summary>
/// Every OpenAI text call (02 README D7) on the Responses API with a strict JSON schema, one retry layer
/// (the per-step Polly pipeline; the SDK's own retry is off) and the D7 outcome table returned as
/// <see cref="TextCallResult{T}"/>. Only the caller's own cancellation escapes as an exception.
/// </summary>
/// <remarks>
/// Logging (D19): one structured line per call with identifiers, token counts, latency and the mapped
/// outcome. No prompt, data object or draft text is ever passed to the logger.
/// </remarks>
/// <param name="client">The Responses client, built with zero SDK retries over the <c>"openai"</c> HttpClient.</param>
/// <param name="options">The step models, efforts and timeouts.</param>
/// <param name="pipelines">The per-step resilience pipelines, keyed <c>openai-&lt;step&gt;</c>.</param>
/// <param name="flags">The scope factory the <c>provider_funds_exhausted</c> gate resolves through (12 CR-20):
/// <see cref="IFeatureFlags"/> is scoped (a real read is a database call), so the singleton client takes the
/// factory and resolves the reader per call — the same per-call scope <c>UsageMeter</c> opens for its row.</param>
/// <param name="log">The call log.</param>
public sealed partial class OpenAiTextClient(
    ResponsesClient client,
    IOptionsMonitor<OpenAiOptions> options,
    ResiliencePipelineProvider<string> pipelines,
    IServiceScopeFactory flags,
    ILogger<OpenAiTextClient> log) : ITextClient
{
    /// <summary>The flag that short-circuits every call to <c>ProviderFundsExhausted</c> (12 CR-20).</summary>
    public const string FundsExhaustedFlag = "provider_funds_exhausted";

    private const string RequestIdHeader = "x-request-id";

    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>The pipeline key of a step.</summary>
    /// <param name="step">The step.</param>
    /// <returns>For example <c>openai-writer</c>.</returns>
    public static string PipelineKey(TextStep step) => "openai-" + step.ToString().ToLowerInvariant();

    /// <inheritdoc />
    public async ValueTask<TextCallResult<T>> CallAsync<T>(TextCallRequest req, CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(req);

        var settings = options.CurrentValue;
        var model = settings.Models[req.Step];
        var watch = Stopwatch.StartNew();

        await using var flagsScope = flags.CreateAsyncScope();
        // Pin the scope to the run's owner before any AppDbContext resolution (09 README D10), exactly as
        // UsageMeter pins the owner of the row it writes; the pin lives until the scope does. ISystemContext
        // exists only on host compositions; unit-test containers without it still resolve the flag reader.
        using var actor = flagsScope.ServiceProvider.GetService<ISystemContext>()?.For(req.Run.UserId);
        if (await flagsScope.ServiceProvider.GetRequiredService<IFeatureFlags>()
                .IsEnabledAsync(FundsExhaustedFlag, ct).ConfigureAwait(false))
        {
            return Finish(req, model, TextCallResult<T>.ProviderFundsExhausted(model, requestId: null), watch, null);
        }

        var request = BuildRequest(req, model, settings.Effort[req.Step]);
        string? requestId = null;

        try
        {
            // The request is marked dispatched by ClientRequestIdHandler right before the transport sends it: a
            // call cancelled before any send must not look billable.
            ClientResult<ResponseResult> response;
            using (TextCallScope.Push(req.Run, req.Step, req, model))
            {
                response = await pipelines.GetPipeline(PipelineKey(req.Step))
                    .ExecuteAsync(token => new ValueTask<ClientResult<ResponseResult>>(client.CreateResponseAsync(request, token)), ct)
                    .ConfigureAwait(false);
            }

            var raw = response.GetRawResponse();
            requestId = TryGetHeader(raw, RequestIdHeader);

            return Finish(req, model, Map<T>(response.Value, raw, model, requestId), watch, null);
        }
        catch (ClientResultException ex)
        {
            requestId = TryGetHeader(ex.GetRawResponse(), RequestIdHeader);

            return Finish(req, model, MapFailure<T>(ex, model, requestId), watch, ex.Status);
        }
        catch (TimeoutRejectedException)
        {
            return Finish(req, model, TextCallResult<T>.ProviderUnavailable(model, requestId), watch, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // An attempt's own deadline, not the caller's: the budget ran out.
            return Finish(req, model, TextCallResult<T>.ProviderUnavailable(model, requestId), watch, null);
        }
        catch (HttpRequestException)
        {
            return Finish(req, model, TextCallResult<T>.ProviderUnavailable(model, requestId), watch, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unmodelled failure must not kill the job: log the type only, never the message (it may echo the body).
            LogUnexpected(ex.GetType().Name, req.Step);

            return Finish(req, model, TextCallResult<T>.ProviderError(model, 0, requestId), watch, 0);
        }
    }

    private static CreateResponseOptions BuildRequest(TextCallRequest req, string model, string effort)
    {
        var request = new CreateResponseOptions
        {
            Model = model,
            MaxOutputTokenCount = req.MaxOutputTokens,
            StoredOutputEnabled = false,
            SafetyIdentifier = Sha256Hex(req.Run.UserId.ToString()),
            PromptCacheKey = req.PromptCacheKey,
            ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = new ResponseReasoningEffortLevel(effort) },
            TextOptions = new ResponseTextOptions
            {
                // OpenAI schema names allow only letters, digits, '_' and '-'; the logical name uses '.'.
                TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(req.SchemaName.Replace('.', '_'), BinaryData.FromBytes(req.Schema), jsonSchemaIsStrict: true),
            },
        };

        // System prompt first, user data last: the O9 cache prefix.
        request.InputItems.Add(ResponseItem.CreateSystemMessageItem(req.SystemPrompt));
        request.InputItems.Add(ResponseItem.CreateUserMessageItem(req.UserDataJson));

        return request;
    }

    private static TextCallResult<T> Map<T>(ResponseResult result, System.ClientModel.Primitives.PipelineResponse raw, string model, string? requestId)
        where T : class
    {
        var usage = MapUsage(result.Usage);

        switch (result.IncompleteStatusDetails?.Reason)
        {
            case { } reason when reason == ResponseIncompleteStatusReason.MaxOutputTokens:
                return TextCallResult<T>.InvalidOutput(model, usage, requestId, [TextCallInvalidOutput<T>.MaxOutputTokensExceeded]);
            case { } reason when reason == ResponseIncompleteStatusReason.ContentFilter:
                return TextCallResult<T>.Moderated(model, usage, requestId, FilteredCategories(raw));
        }

        if (HasRefusal(result))
        {
            return TextCallResult<T>.Moderated(model, usage, requestId, FilteredCategories(raw));
        }

        if (result.Status == ResponseStatus.Failed)
        {
            return TextCallResult<T>.ProviderUnavailable(model, requestId, usage);
        }

        try
        {
            var draft = JsonSerializer.Deserialize<T>(result.GetOutputText() ?? string.Empty, OutputOptions);

            return draft is null
                ? TextCallResult<T>.InvalidOutput(model, usage, requestId, [TextCallInvalidOutput<T>.SchemaInvalid])
                : TextCallResult<T>.Ok(draft, model, usage, requestId);
        }
        catch (JsonException)
        {
            return TextCallResult<T>.InvalidOutput(model, usage, requestId, [TextCallInvalidOutput<T>.SchemaInvalid]);
        }
    }

    private static TextCallResult<T> MapFailure<T>(ClientResultException ex, string model, string? requestId)
        where T : class
    {
        if (ex.Status == 429 && IsFundsExhausted(ex))
        {
            return TextCallResult<T>.ProviderFundsExhausted(model, requestId);
        }

        // 0 is a transport failure; 429 without a spend-limit code is transient and already retried once.
        return ex.Status is 0 or 429 or >= 500
            ? TextCallResult<T>.ProviderUnavailable(model, requestId)
            : TextCallResult<T>.ProviderError(model, ex.Status, requestId);
    }

    /// <summary>Whether a 429 carries a funds code, which no retry can fix.</summary>
    /// <param name="ex">The failure.</param>
    internal static bool IsFundsExhausted(ClientResultException ex)
    {
        try
        {
            var body = ex.GetRawResponse()?.Content.ToString() ?? string.Empty;

            return body.Contains("spend_limit_exceeded", StringComparison.Ordinal)
                || body.Contains("credit_balance_exhausted", StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            // Unbuffered content: no code to read.
            return false;
        }
    }

    private static bool HasRefusal(ResponseResult result)
    {
        foreach (var item in result.OutputItems)
        {
            if (item is MessageResponseItem message && message.Content.Any(part => part.Kind == ResponseContentPartKind.Refusal))
            {
                return true;
            }
        }

        return false;
    }

    private static TokenUsage? MapUsage(ResponseTokenUsage? usage) =>
        usage is null
            ? null
            : new TokenUsage(
                usage.InputTokenCount,
                usage.InputTokenDetails?.CachedTokenCount ?? 0,
                usage.InputTokenDetails?.CacheWriteTokenCount ?? 0,
                usage.OutputTokenCount,
                usage.OutputTokenDetails?.ReasoningTokenCount ?? 0);

    /// <summary>
    /// The categories the provider flagged, from <c>content_filter_results</c> (top level or under
    /// <c>incomplete_details</c>) when present: an object of <c>{category: {filtered}}</c> or an array of
    /// <c>{category, filtered}</c>. Empty when the response names none.
    /// </summary>
    private static string[] FilteredCategories(System.ClientModel.Primitives.PipelineResponse? raw)
    {
        try
        {
            if (raw is null || raw.Content.ToMemory().IsEmpty)
            {
                return [];
            }

            using var doc = JsonDocument.Parse(raw.Content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var results = root.TryGetProperty("content_filter_results", out var top)
                ? top
                : root.TryGetProperty("incomplete_details", out var details)
                    && details.ValueKind == JsonValueKind.Object
                    && details.TryGetProperty("content_filter_results", out var nested)
                        ? nested
                        : default;

            return results.ValueKind switch
            {
                JsonValueKind.Object => [.. results.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("filtered", out var f) && f.ValueKind == JsonValueKind.True)
                    .Select(p => p.Name)],
                JsonValueKind.Array => [.. results.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String
                        && (!e.TryGetProperty("filtered", out var f) || f.ValueKind == JsonValueKind.True))
                    .Select(e => e.GetProperty("category").GetString()!)],
                _ => [],
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    private static string? TryGetHeader(System.ClientModel.Primitives.PipelineResponse? raw, string name) =>
        raw is not null && raw.Headers.TryGetValue(name, out var value) ? value : null;

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private TextCallResult<T> Finish<T>(TextCallRequest req, string model, TextCallResult<T> result, Stopwatch watch, int? status)
        where T : class
    {
        var usage = result.Usage;
        var kind = result.GetType().Name;
        kind = kind[..kind.IndexOf('`', StringComparison.Ordinal)];

        LogCall(
            req.Step,
            req.Run.Attempt,
            model,
            req.Run.GenerationRunId,
            req.PromptVersion,
            req.PromptCacheKey,
            usage?.InputTokens,
            usage?.CachedInputTokens,
            usage?.OutputTokens,
            usage?.ReasoningTokens,
            watch.ElapsedMilliseconds,
            result.RequestId,
            kind,
            status);

        return result;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "OpenAI call {Step} attempt {Attempt} model {Model} run {GenerationRunId} prompt_version {PromptVersion} cache_key {PromptCacheKey} tokens in {InputTokens} cached {CachedInputTokens} out {OutputTokens} reasoning {ReasoningTokens} latency_ms {LatencyMs} request_id {RequestId} outcome {Outcome} status {Status}")]
    private partial void LogCall(
        TextStep step,
        int attempt,
        string model,
        Guid generationRunId,
        string promptVersion,
        string promptCacheKey,
        int? inputTokens,
        int? cachedInputTokens,
        int? outputTokens,
        int? reasoningTokens,
        long latencyMs,
        string? requestId,
        string outcome,
        int? status);

    [LoggerMessage(Level = LogLevel.Error, Message = "OpenAI call {Step} failed with an unmodelled {ExceptionType}")]
    private partial void LogUnexpected(string exceptionType, TextStep step);

    /// <summary>
    /// Parses the culture-invariant seconds of a <c>Retry-After</c> header, clamped to
    /// <see cref="OpenAiServiceCollectionExtensions.MaxRetryAfter"/>; <see langword="null"/> when absent, not seconds,
    /// negative or not finite (<c>Infinity</c>, <c>NaN</c>), which falls back to the computed backoff.
    /// </summary>
    /// <param name="ex">The failure.</param>
    internal static TimeSpan? RetryAfter(ClientResultException ex)
    {
        var raw = ex.GetRawResponse();

        return raw is not null
            && raw.Headers.TryGetValue("Retry-After", out var value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds)
            && seconds >= 0
                ? TimeSpan.FromSeconds(Math.Min(seconds, OpenAiServiceCollectionExtensions.MaxRetryAfter.TotalSeconds))
                : null;
    }
}
