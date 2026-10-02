using ErrorOr;
using Microsoft.AspNetCore.Http;
using NightTales.RenderNode.Contract;

namespace NightTales.RenderNode.Server;

/// <summary>
/// The HTTP status and the retry delay of every <c>adapter_*</c> error code (05.4.1-T2 §1). The code catalogue
/// is the contract; this map is the one place a code becomes a status, so a seam error and a core error answer
/// the same way and the two <c>Retry-After</c> values of the table (<c>60</c> for auth, <c>30</c> for capacity)
/// cannot drift apart.
/// </summary>
internal static class AdapterErrorStatus
{
    private const int AuthRetryAfterSeconds = 60;
    private const int CapacityRetryAfterSeconds = 30;

    internal static int For(Error error) => For(error.Code);

    internal static int For(string code) => code switch
    {
        AdapterErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        AdapterErrorCodes.AuthThrottled => StatusCodes.Status429TooManyRequests,
        AdapterErrorCodes.BodyTooLarge => StatusCodes.Status413PayloadTooLarge,
        AdapterErrorCodes.RouteNotFound => StatusCodes.Status404NotFound,
        AdapterErrorCodes.TaskUnknown => StatusCodes.Status404NotFound,
        AdapterErrorCodes.OutputAbsent => StatusCodes.Status404NotFound,
        AdapterErrorCodes.ContractViolation => StatusCodes.Status400BadRequest,
        AdapterErrorCodes.IdempotencyConflict => StatusCodes.Status409Conflict,
        AdapterErrorCodes.OutputNotReady => StatusCodes.Status409Conflict,
        AdapterErrorCodes.TaskBusy => StatusCodes.Status409Conflict,
        AdapterErrorCodes.CapacityFull => StatusCodes.Status429TooManyRequests,
        AdapterErrorCodes.ProbeThrottled => StatusCodes.Status429TooManyRequests,
        AdapterErrorCodes.NodeUnhealthy => StatusCodes.Status503ServiceUnavailable,
        AdapterErrorCodes.ProbeUnavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>The documented delay per throttling code: 60 s for auth, 30 s for capacity (D15 S2, F07/C10).</summary>
    internal static int? RetryAfterFor(string code) => code switch
    {
        AdapterErrorCodes.AuthThrottled => AuthRetryAfterSeconds,
        AdapterErrorCodes.CapacityFull => CapacityRetryAfterSeconds,
        AdapterErrorCodes.ProbeThrottled => AuthRetryAfterSeconds,
        _ => null,
    };

    /// <summary>
    /// Answers with the envelope of an error code: the fixed sentence of that code, never the error's own
    /// description (which is not safe text, §1/D15 S4), plus the code's documented retry delay.
    /// </summary>
    internal static Task WriteAsync(HttpContext context, string code, int? retryAfterSeconds = null) =>
        RenderNodeEnvelope.WriteErrorAsync(
            context,
            For(code),
            code,
            retryAfterSeconds: retryAfterSeconds ?? RetryAfterFor(code));

    /// <summary>Answers with the envelope of a seam error.</summary>
    internal static Task WriteAsync(HttpContext context, Error error, int? retryAfterSeconds = null) =>
        WriteAsync(context, error.Code, retryAfterSeconds);
}
