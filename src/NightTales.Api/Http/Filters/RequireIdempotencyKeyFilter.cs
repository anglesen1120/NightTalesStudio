using ErrorOr;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NightTales.Application.Common.Errors;

namespace NightTales.Api.Http.Filters;

/// <summary>
/// Rejects a paid/platform POST that does not carry a well-formed <c>Idempotency-Key</c> before
/// Mediator runs (09 README D6/D7, 09.1.4-T1 UC4). The key is 1–128 printable ASCII characters
/// (<c>0x21</c>–<c>0x7E</c>), like the <c>idempotency_keys.key</c> column (09 CR-4).
/// </summary>
/// <remarks>
/// The response is 400 <c>idempotency_key_required</c> — the code, not <c>ErrorType</c>, drives it.
/// The description never echoes the received value, because the key is a client secret that must not
/// end up in a log or a response body.
/// </remarks>
public sealed class RequireIdempotencyKeyFilter : IEndpointFilter
{
    /// <summary>The required header name.</summary>
    public const string HeaderName = "Idempotency-Key";

    /// <summary>The maximum accepted key length, matching <c>idempotency_keys.key</c>.</summary>
    public const int MaxLength = 128;

    private const char MinPrintableAscii = (char)0x21;

    private const char MaxPrintableAscii = (char)0x7E;

    /// <summary>The client-safe description of every rejection.</summary>
    internal const string RejectionDescription =
        "The Idempotency-Key header is required and must hold 1 to 128 printable ASCII characters.";

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var header = context.HttpContext.Request.Headers[HeaderName];
        if (header.Count != 1 || !IsValidKey(header.ToString()))
        {
            return ProblemDetailsWriter.ToProblem(
                [Error.Failure(ErrorCodes.IdempotencyKeyRequired, RejectionDescription)],
                context.HttpContext);
        }

        return await next(context).ConfigureAwait(false);
    }

    private static bool IsValidKey(string key)
    {
        if (key.Length is 0 or > MaxLength)
        {
            return false;
        }

        foreach (var character in key)
        {
            if (character < MinPrintableAscii || character > MaxPrintableAscii)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Applies <see cref="RequireIdempotencyKeyFilter"/> to a route.</summary>
public static class RequireIdempotencyKeyExtensions
{
    /// <summary>Requires a well-formed <c>Idempotency-Key</c> header on this route (09 README D6).</summary>
    /// <param name="builder">The route to protect.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static RouteHandlerBuilder RequireIdempotencyKey(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddEndpointFilter<RequireIdempotencyKeyFilter>();
    }
}
