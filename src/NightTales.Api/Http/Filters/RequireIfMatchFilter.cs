using ErrorOr;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NightTales.Application.Common.Errors;

namespace NightTales.Api.Http.Filters;

/// <summary>
/// Rejects a D9/D15 route called without <c>If-Match</c>, before the handler runs (09 README D7/D15,
/// 09.1.4-T1 UC5). The header's <em>value</em> is not judged here: the handler compares it against the
/// row it just read through <c>ETags.Matches</c>, so a stale tag answers 412 with the resource's own
/// conflict code while a missing tag answers 428 <c>if_match_required</c>.
/// </summary>
/// <remarks>
/// The binder of the route is <c>[FromHeader(Name = "If-Match")] string? ifMatch</c>, which is how the
/// value reaches the command.
/// </remarks>
public sealed class RequireIfMatchFilter : IEndpointFilter
{
    /// <summary>The required header name.</summary>
    public const string HeaderName = "If-Match";

    /// <summary>The client-safe description of the rejection.</summary>
    internal const string RejectionDescription =
        "The If-Match header is required on this route; send the ETag you read for the resource.";

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var header = context.HttpContext.Request.Headers[HeaderName];
        if (header.Count == 0 || string.IsNullOrWhiteSpace(header.ToString()))
        {
            return ProblemDetailsWriter.ToProblem(
                [Error.Failure(ErrorCodes.IfMatchRequired, RejectionDescription)],
                context.HttpContext);
        }

        return await next(context).ConfigureAwait(false);
    }
}

/// <summary>Applies <see cref="RequireIfMatchFilter"/> to a route.</summary>
public static class RequireIfMatchExtensions
{
    /// <summary>Requires an <c>If-Match</c> header on this route (09 README D9/D15).</summary>
    /// <param name="builder">The route to protect.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static RouteHandlerBuilder RequireIfMatch(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddEndpointFilter<RequireIfMatchFilter>();
    }
}
