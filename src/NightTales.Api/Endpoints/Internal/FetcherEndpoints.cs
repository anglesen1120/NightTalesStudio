using System.Text.Json;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using NightTales.Api.Http;
using NightTales.Api.Internal.Fetcher;
using NightTales.Api.Security;
using NightTales.Application.Abstractions.Fetching;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research.Fetcher;
using NightTales.Application.Features.Research.Fetcher.Commands.HeartbeatFetchTask;
using NightTales.Application.Features.Research.Fetcher.Commands.LeaseFetchTasks;

namespace NightTales.Api.Endpoints.Internal;

/// <summary>
/// The three routes of the fetcher's internal API (02.5.2-T2, 02 D10, plan §5.2), served only by the internal listener's
/// own pipeline: <c>POST /internal/fetcher/v1/tasks/lease</c>, <c>.../{taskId}/heartbeat</c> and
/// <c>.../{taskId}/result</c>.
/// </summary>
/// <remarks>
/// <para>
/// These are not endpoint-routing endpoints on purpose. Routes registered with endpoint routing share one route table
/// across every listener of the host, so a public-port request could match them; here the public pipeline has no
/// knowledge of them at all and a request to the public port gets the ordinary <c>404</c>. The pipeline order is the
/// security order: token authentication first (a bare <c>401</c>, even for an unknown path), then the route, then a
/// bounded body read, then the per-worker rate limit, then one message through the D4 pipeline. The global in-flight
/// and per-minute limit runs before the body read.
/// </para>
/// <para>
/// The JSON is read by hand with strict number handling (a quoted number is not a number) and the wire contract's
/// shapes: a malformed body, a number where a string belongs, a string where a number belongs or a numeric enum is a
/// <c>400</c>, never a framework exception that the exception handler would turn into a <c>500</c>. Members the
/// contract does not name are ignored, not rejected. Nothing from the body is logged (D19).
/// </para>
/// </remarks>
public static class FetcherEndpoints
{
    /// <summary>The prefix of every route.</summary>
    public const string RoutePrefix = "/internal/fetcher/v1/tasks";

    private static readonly Action<ILogger, string, Exception?> UnhandledLog = LoggerMessage.Define<string>(
        LogLevel.Error,
        new EventId(2510, "fetcher.internal.unhandled"),
        "fetcher.internal.unhandled exceptionType={ExceptionType}");

    private const string LeaseSegment = "lease";

    private const string HeartbeatSegment = "heartbeat";

    private const string ResultSegment = "result";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        NumberHandling = JsonNumberHandling.Strict,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The terminal delegate of the internal listener's pipeline.</summary>
    /// <param name="http">The request.</param>
    public static async Task HandleAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        try
        {
            await DispatchAsync(http).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // The fetcher went away; there is nobody to answer. Any work it started rolls back with its transaction.
        }
        catch (OperationCanceledException)
        {
            // A deadline of ours (a database or storage timeout), not the caller's: the documented 503.
            await WriteAsync(
                http,
                ProblemDetailsWriter.ToProblem(
                    [Error.Failure(ErrorCodes.ServiceUnavailable, "The service is temporarily unavailable; retry.")],
                    http)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // By type only (09 README D17): the message may hold a URL or a statement.
            UnhandledLog(
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("NightTales.Api.FetcherInternal"),
                exception.GetType().Name,
                null);

            await WriteAsync(
                http,
                ProblemDetailsWriter.ToProblem([Error.Failure(ErrorCodes.InternalError, "Internal error.")], http)).ConfigureAwait(false);
        }
    }

    private static async Task DispatchAsync(HttpContext http)
    {
        var authentication = await http.AuthenticateAsync(FetcherTokenAuthenticationHandler.SchemeName).ConfigureAwait(false);
        if (!authentication.Succeeded)
        {
            await http.ChallengeAsync(FetcherTokenAuthenticationHandler.SchemeName).ConfigureAwait(false);

            return;
        }

        if (!TryMatch(http.Request.Path, out var route, out var taskId))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;

            return;
        }

        if (!HttpMethods.IsPost(http.Request.Method))
        {
            http.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            http.Response.Headers.Allow = HttpMethods.Post;

            return;
        }

        // The global in-flight and per-minute ceiling comes first: a refused request costs no body read and no parse.
        var limiter = http.RequestServices.GetRequiredService<FetcherRateLimiter>();
        if (!limiter.TryEnter(out var slot, out var retryAfterSeconds))
        {
            await WriteAsync(http, TooManyRequests(http, retryAfterSeconds)).ConfigureAwait(false);

            return;
        }

        using var held = slot;

        var options = http.RequestServices.GetRequiredService<FetcherInternalOptions>();
        var raw = await ReadBodyAsync(http, options.MaxBodyBytes).ConfigureAwait(false);
        if (raw is not { } body)
        {
            http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;

            return;
        }

        switch (route)
        {
            case Route.Lease:
                await LeaseAsync(http, body).ConfigureAwait(false);
                break;
            case Route.Heartbeat:
                await HeartbeatAsync(http, taskId, body).ConfigureAwait(false);
                break;
            default:
                await ResultAsync(http, taskId, body).ConfigureAwait(false);
                break;
        }
    }

    private static async Task LeaseAsync(HttpContext http, ReadOnlyMemory<byte> raw)
    {
        if (!TryParse<LeaseBody>(raw, out var body, out var invalid))
        {
            await WriteAsync(http, invalid!).ConfigureAwait(false);

            return;
        }

        if (RateLimited(http, body.WorkerId) is { } limited)
        {
            await WriteAsync(http, limited).ConfigureAwait(false);

            return;
        }

        var sender = http.RequestServices.GetRequiredService<ISender>();
        var result = await sender
            .Send(new LeaseFetchTasksCommand(body.WorkerId, body.Max), http.RequestAborted)
            .ConfigureAwait(false);

        await WriteAsync(http, result.IsError ? FetcherProblem.From(result.Errors, http) : Results.Json(result.Value, Json)).ConfigureAwait(false);
    }

    private static async Task HeartbeatAsync(HttpContext http, Guid taskId, ReadOnlyMemory<byte> raw)
    {
        if (!TryParse<WorkerBody>(raw, out var body, out var invalid))
        {
            await WriteAsync(http, invalid!).ConfigureAwait(false);

            return;
        }

        if (RateLimited(http, body.WorkerId) is { } limited)
        {
            await WriteAsync(http, limited).ConfigureAwait(false);

            return;
        }

        var sender = http.RequestServices.GetRequiredService<ISender>();
        var result = await sender
            .Send(new HeartbeatFetchTaskCommand(taskId, body.WorkerId), http.RequestAborted)
            .ConfigureAwait(false);

        await WriteAsync(
            http,
            result.IsError ? FetcherProblem.From(result.Errors, http) : Results.Json(new HeartbeatAck(result.Value), Json)).ConfigureAwait(false);
    }

    private static async Task ResultAsync(HttpContext http, Guid taskId, ReadOnlyMemory<byte> raw)
    {
        if (!TryParse<FetchResultRequest>(raw, out var body, out var invalid))
        {
            await WriteAsync(http, invalid!).ConfigureAwait(false);

            return;
        }

        if (RateLimited(http, body.WorkerId) is { } limited)
        {
            await WriteAsync(http, limited).ConfigureAwait(false);

            return;
        }

        var runner = http.RequestServices.GetRequiredService<FetcherResultRunner>();
        var result = await runner.CompleteAsync(taskId, body, http.RequestAborted).ConfigureAwait(false);

        await WriteAsync(http, result.IsError ? FetcherProblem.From(result.Errors, http) : Results.Json(result.Value, Json)).ConfigureAwait(false);
    }

    /// <summary>The 429 of a worker over its budget, or <see langword="null"/> when it may proceed.</summary>
    private static IResult? RateLimited(HttpContext http, string? workerId)
    {
        var limiter = http.RequestServices.GetRequiredService<FetcherRateLimiter>();

        return limiter.TryAcquire(workerId, out var retryAfterSeconds) ? null : TooManyRequests(http, retryAfterSeconds);
    }

    private static IResult TooManyRequests(HttpContext http, int retryAfterSeconds) =>
        ProblemDetailsWriter.ToProblem(
            [Error.Failure(
                ErrorCodes.RateLimited,
                "Too many requests.",
                new Dictionary<string, object> { [ErrorMetadata.RetryAfterSeconds] = retryAfterSeconds })],
            http);

    private static bool TryMatch(PathString path, out Route route, out Guid taskId)
    {
        route = default;
        taskId = Guid.Empty;

        if (!path.StartsWithSegments(RoutePrefix, StringComparison.Ordinal, out var rest))
        {
            return false;
        }

        var segments = rest.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (segments.Length == 1 && segments[0] == LeaseSegment)
        {
            route = Route.Lease;

            return true;
        }

        if (segments.Length == 2 && Guid.TryParseExact(segments[0], "D", out taskId) && taskId != Guid.Empty)
        {
            switch (segments[1])
            {
                case HeartbeatSegment:
                    route = Route.Heartbeat;

                    return true;
                case ResultSegment:
                    route = Route.Result;

                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the whole body or answers <see langword="null"/> when it exceeds <paramref name="limit"/> bytes. Kestrel's
    /// own limit for this request is set to the same cap (its global default is 1 MiB, whatever <c>MaxBodyBytes</c> says),
    /// and the 413 it throws on a chunked body is the same answer. The buffer is sized from the declared length, so a
    /// normal result costs one array and no copy.
    /// </summary>
    private static async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(HttpContext http, int limit)
    {
        var declared = http.Request.ContentLength;
        if (declared > limit)
        {
            return null;
        }

        var feature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = limit;
        }

        // One byte of slack so that a body of exactly `limit` bytes is distinguishable from one that overflows it.
        var buffer = new byte[declared is { } length ? (int)length + 1 : Math.Min(4 * 1_024, limit + 1)];
        var total = 0;
        try
        {
            while (true)
            {
                if (total == buffer.Length)
                {
                    Array.Resize(ref buffer, (int)Math.Min(buffer.Length * 2L, limit + 1L));
                }

                var read = await http.Request.Body.ReadAsync(buffer.AsMemory(total), http.RequestAborted).ConfigureAwait(false);
                if (read == 0)
                {
                    return buffer.AsMemory(0, total);
                }

                total += read;
                if (total > limit)
                {
                    return null;
                }
            }
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }
    }

    private static bool TryParse<T>(ReadOnlyMemory<byte> raw, out T body, out IResult? problem)
        where T : class
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<T>(raw.Span, Json);
            if (parsed is not null)
            {
                body = parsed;
                problem = null;

                return true;
            }
        }
        catch (JsonException)
        {
            // Fall through to the 400 below; the exception text may quote the body.
        }
        catch (NotSupportedException)
        {
            // Same.
        }
        catch (InvalidOperationException)
        {
            // A string with a lone surrogate escape cannot be transcoded; the body is corrupt, not our bug.
        }

        body = null!;
        problem = FetcherProblem.Invalid("body", "The request body must be a JSON object of the documented shape.");

        return false;
    }

    private static Task WriteAsync(HttpContext http, IResult result) => result.ExecuteAsync(http);

    private enum Route
    {
        Lease,
        Heartbeat,
        Result,
    }

    private sealed record LeaseBody(string? WorkerId, int Max);

    private sealed record WorkerBody(string? WorkerId);

    private sealed record HeartbeatAck(DateTimeOffset LeaseUntil);
}
