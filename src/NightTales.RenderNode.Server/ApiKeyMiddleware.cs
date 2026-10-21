using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NightTales.RenderNode.Contract;

namespace NightTales.RenderNode.Server;

/// <summary>
/// The first stage of every request (05.4.1-T2 §1, 05 D15 S2/S3): exactly one <c>x-api-key</c> header,
/// compared in constant time against <c>ApiKey</c> and, while set, <c>ApiKeyNext</c>. A missing, empty,
/// duplicated or wrong key is <c>401 adapter_unauthorized</c>; a source that already failed
/// <c>AuthFailuresPerMinute</c> times in the last minute is <c>429 adapter_auth_throttled</c> with
/// <c>Retry-After: 60</c>.
/// </summary>
/// <remarks>
/// Auth runs before routing and before the body is read, so an unknown path with a bad key answers 401 and an
/// oversized body with a bad key answers 401, never 404/413 (§10 T68). Comparison hashes both sides to a fixed
/// 32 bytes and then uses <see cref="CryptographicOperations.FixedTimeEquals"/>, so neither the content nor
/// the length of a key is observable. Key values are never logged or echoed.
/// </remarks>
public sealed class ApiKeyMiddleware
{
    private const int Sha256Length = 32;
    private const int ThrottleRetryAfterSeconds = 60;

    private readonly RequestDelegate _next;
    private readonly IOptions<RenderNodeServerOptions> _options;
    private readonly AuthFailureThrottle _throttle;

    public ApiKeyMiddleware(
        RequestDelegate next,
        IOptions<RenderNodeServerOptions> options,
        AuthFailureThrottle throttle)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(throttle);

        _next = next;
        _options = options;
        _throttle = throttle;
    }

    /// <summary>Authenticates the request or answers 401/429.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (IsAuthenticated(context.Request, _options.Value))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var source = AuthFailureThrottle.Normalize(context.Connection.RemoteIpAddress);
        if (_throttle.RecordFailedAttempt(source))
        {
            // The source crossed the threshold; this attempt is not counted, so the 429 cannot extend the
            // window and the throttle can expire.
            await RenderNodeEnvelope
                .WriteErrorAsync(context, StatusCodes.Status429TooManyRequests, AdapterErrorCodes.AuthThrottled, retryAfterSeconds: ThrottleRetryAfterSeconds)
                .ConfigureAwait(false);
            return;
        }

        await RenderNodeEnvelope
            .WriteErrorAsync(context, StatusCodes.Status401Unauthorized, AdapterErrorCodes.Unauthorized)
            .ConfigureAwait(false);
    }

    private static bool IsAuthenticated(HttpRequest request, RenderNodeServerOptions options)
    {
        var presented = request.Headers[RenderNodeHeaders.ApiKey];
        // Exactly one value: a missing header (Count 0) and a duplicated one (Count > 1) are both rejected,
        // and an empty value never reaches a comparison (D15 S3).
        if (presented.Count != 1 || string.IsNullOrEmpty(presented[0]))
        {
            return false;
        }

        var key = presented[0]!;
        var next = options.ApiKeyNext;

        // Both comparisons run before the result is combined, so the response time does not reveal which
        // key matched.
        var matchesCurrent = FixedTimeEquals(key, options.ApiKey);
        var matchesNext = !string.IsNullOrEmpty(next) && FixedTimeEquals(key, next!);
        return matchesCurrent | matchesNext;
    }

    private static bool FixedTimeEquals(string presented, string expected)
    {
        Span<byte> presentedHash = stackalloc byte[Sha256Length];
        Span<byte> expectedHash = stackalloc byte[Sha256Length];
        Hash(presented, presentedHash);
        Hash(expected, expectedHash);
        return CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash);
    }

    private static void Hash(string value, Span<byte> destination)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(value.Length));
        try
        {
            var written = Encoding.UTF8.GetBytes(value, buffer);
            SHA256.HashData(buffer.AsSpan(0, written), destination);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
