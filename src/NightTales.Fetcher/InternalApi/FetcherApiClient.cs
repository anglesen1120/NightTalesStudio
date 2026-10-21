using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Domain.Research;

namespace NightTales.Fetcher.InternalApi;

/// <summary>
/// The typed client of the Api's internal lease API (02.5.2-T2 frozen hand-off). It sends exactly one
/// <c>Authorization: Bearer</c> header per request, serialises with <see cref="JsonSerializerDefaults.Web"/> and
/// relaxed escaping (the Api's 1 MiB body cap counts bytes), and turns every failure into a typed
/// <see cref="Error"/>. Nothing it throws or logs carries the token, a URL or a response body: only the operation, the
/// HTTP status and an exception type name.
/// </summary>
/// <param name="http">The client (<see cref="HttpClient.BaseAddress"/> set, 15 s timeout).</param>
/// <param name="options">The base URL and token.</param>
/// <param name="time">The time source of the retry back-off.</param>
/// <param name="metrics">The instruments.</param>
/// <param name="log">The log.</param>
public sealed partial class FetcherApiClient(
    HttpClient http,
    IOptions<FetcherApiOptions> options,
    TimeProvider time,
    FetcherMetrics metrics,
    ILogger<FetcherApiClient> log) : IFetcherApi
{
    /// <summary>The Api's request body cap, in bytes (<c>FetcherInternalOptions.MaxBodyBytes</c>).</summary>
    public const int MaxBodyBytes = 1_048_576;

    /// <summary>The attempts of one result POST, the first included.</summary>
    public const int ResultAttempts = 3;

    /// <summary>The back-off before the second attempt; it doubles, then gets ±50 % jitter.</summary>
    public static readonly TimeSpan ResultBackoffBase = TimeSpan.FromMilliseconds(500);

    private const string BasePath = "/internal/fetcher/v1/tasks";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _token = options.Value.Token;

    /// <inheritdoc />
    public async ValueTask<ErrorOr<IReadOnlyList<LeasedTask>>> LeaseAsync(string workerId, int max, CancellationToken ct)
    {
        var sent = await SendAsync("lease", $"{BasePath}/lease", new { workerId, max }, ct).ConfigureAwait(false);
        if (sent.IsError)
        {
            return sent.Errors;
        }

        using var response = sent.Value;
        try
        {
            var leased = await response.Content.ReadFromJsonAsync<List<LeasedTask>>(Json, ct).ConfigureAwait(false);
            if (leased is null || leased.Count > max)
            {
                // A null root is not an empty lease, and the Api never answers more than the requested max.
                LogMalformed("lease", leased is null ? "NullRoot" : "TooManyTasks");
                return FetcherApiErrors.Unavailable(retryable: false);
            }

            return leased.ToArray();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
        {
            LogMalformed("lease", ex.GetType().Name);
            return FetcherApiErrors.Unavailable(retryable: false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> HeartbeatAsync(Guid taskId, string workerId, CancellationToken ct)
    {
        var sent = await SendAsync("heartbeat", $"{BasePath}/{taskId:D}/heartbeat", new { workerId }, ct).ConfigureAwait(false);
        if (sent.IsError)
        {
            return sent.Errors;
        }

        sent.Value.Dispose();
        return Result.Success;
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<FetchResultBody>> PostResultAsync(Guid taskId, FetchResultBody body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        var payload = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        if (payload.Length > MaxBodyBytes)
        {
            // Never send an over-cap body (the Api would answer 413 and the lease would expire). A done body whose JSON is
            // larger than the text-char cap implied (escaped characters) ends as the D8 failure it is, and the caller is told
            // so (it returns the body actually posted) for its log and metric.
            if (body.Status != "done")
            {
                return FetcherApiErrors.ValidationFailed("source_too_large");
            }

            body = ResultBodies.Failed(body.WorkerId, SourceErrors.SourceTooLargeCode, body.HttpStatus);
            payload = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        }

        ErrorOr<HttpResponseMessage> sent = FetcherApiErrors.Unavailable();
        for (var attempt = 1; attempt <= ResultAttempts; attempt++)
        {
            sent = await SendBytesAsync("result", $"{BasePath}/{taskId:D}/result", payload, ct).ConfigureAwait(false);
            if (!sent.IsError)
            {
                sent.Value.Dispose();
                return body;
            }

            if (!FetcherApiErrors.IsRetryable(sent.FirstError) || attempt == ResultAttempts)
            {
                break;
            }

            var delay = Backoff(attempt);
            LogRetry(attempt, (int)delay.TotalMilliseconds);
            await Task.Delay(delay, time, ct).ConfigureAwait(false);
        }

        return sent.Errors;
    }

    private static TimeSpan Backoff(int failedAttempt)
    {
        var nominal = ResultBackoffBase.TotalMilliseconds * Math.Pow(2, failedAttempt - 1);
        return TimeSpan.FromMilliseconds(nominal * (0.5 + Random.Shared.NextDouble()));
    }

    private ValueTask<ErrorOr<HttpResponseMessage>> SendAsync<T>(string operation, string path, T body, CancellationToken ct) =>
        SendBytesAsync(operation, path, JsonSerializer.SerializeToUtf8Bytes(body, Json), ct);

    private async ValueTask<ErrorOr<HttpResponseMessage>> SendBytesAsync(string operation, string path, byte[] payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            // Only the type: the message of a transport exception can name the host and must not be trusted.
            metrics.RecordApiError(operation, "network");
            LogTransport(operation, ex.GetType().Name);
            return FetcherApiErrors.Unavailable();
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = (int)response.StatusCode;
        try
        {
            return await MapFailureAsync(operation, response, status, ct).ConfigureAwait(false);
        }
        finally
        {
            response.Dispose();
        }
    }

    private async ValueTask<ErrorOr<HttpResponseMessage>> MapFailureAsync(string operation, HttpResponseMessage response, int status, CancellationToken ct)
    {
        metrics.RecordApiError(operation, status >= 500 ? "5xx" : "4xx");
        LogStatus(operation, status);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                return FetcherApiErrors.Unauthorized;
            case HttpStatusCode.Conflict:
                return FetcherApiErrors.LeaseLost;
            case HttpStatusCode.NotFound:
                return FetcherApiErrors.TaskNotFound;
            case HttpStatusCode.BadRequest:
                return FetcherApiErrors.ValidationFailed(await ReadProblemCodeAsync(response, ct).ConfigureAwait(false));
            default:
                // 5xx is transient; 429, 413 and the rest are not worth an immediate retry of the same body.
                return FetcherApiErrors.Unavailable(retryable: status >= 500);
        }
    }

    private static async ValueTask<string> ReadProblemCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        const string fallback = "validation_failed";
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && code.GetString() is { Length: > 0 and <= 64 } value
                && value.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                return value;
            }
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException or InvalidOperationException)
        {
            // An unreadable problem body is still a 400.
        }

        return fallback;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "internal api {Operation} answered {HttpStatus}")]
    private partial void LogStatus(string operation, int httpStatus);

    [LoggerMessage(Level = LogLevel.Warning, Message = "internal api {Operation} failed in transport: {ExceptionType}")]
    private partial void LogTransport(string operation, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "internal api {Operation} answered a body that does not parse: {ExceptionType}")]
    private partial void LogMalformed(string operation, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "result POST attempt {Attempt} failed transiently; retrying in {DelayMs} ms")]
    private partial void LogRetry(int attempt, int delayMs);
}
