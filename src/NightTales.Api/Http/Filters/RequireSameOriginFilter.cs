using ErrorOr;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NightTales.Application.Common.Errors;

namespace NightTales.Api.Http.Filters;

/// <summary>
/// Refuses a cookie-authenticated POST whose <c>Origin</c> is absent or is not
/// <see cref="AppOptions.PublicOrigin"/> (09 README BD3/BD16): the CSRF gate of <c>/auth/refresh</c> and
/// <c>/auth/logout</c>.
/// </summary>
/// <remarks>
/// <para>
/// The refresh cookie is <c>SameSite=Strict</c>, which already stops a cross-site form from carrying it.
/// This filter is the belt to that suspenders: a browser that ignores the attribute, an old client and a
/// hand-written request all have to name the right origin, and a request without one is refused rather
/// than assumed to be same-origin.
/// </para>
/// <para>
/// It runs before the handler, so a forbidden request opens no transaction, rotates no token and reveals
/// nothing about the cookie it carried: the answer is 403 <c>forbidden</c> either way.
/// </para>
/// <para>
/// 403 <c>forbidden</c> is the documented status (09 §5.4) and not 401: the request was understood and the
/// caller may well have a valid session — it simply did not come from the application's own origin.
/// </para>
/// </remarks>
/// <param name="options">The host's <c>App:PublicOrigin</c>.</param>
public sealed class RequireSameOriginFilter(IOptions<AppOptions> options) : IEndpointFilter
{
    /// <summary>The request header the browser sends on every cross-origin and same-origin POST.</summary>
    public const string OriginHeader = "Origin";

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var request = context.HttpContext.Request;
        var origins = request.Headers[OriginHeader];

        // Exactly one value: a duplicated Origin header is malformed, and "the first one wins" is the kind
        // of leniency a CSRF gate must not have (SEC 09.2.1-T2's rule for the test headers).
        if (origins.Count != 1 || !IsSameOrigin(origins.ToString(), options.Value.PublicOrigin))
        {
            return ProblemDetailsWriter.ToProblem(
                [Error.Forbidden(ErrorCodes.Forbidden, "The request must come from the application's own origin.")],
                context.HttpContext);
        }

        return await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the presented origin names the same scheme, host and port as the configured one. The
    /// comparison is on the authority, never on the string, so a differently-cased host or a redundant
    /// default port still matches while <c>https://app.example.com.evil.test</c> does not.
    /// </summary>
    private static bool IsSameOrigin(string origin, string publicOrigin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var presented)
            || !Uri.TryCreate(publicOrigin, UriKind.Absolute, out var expected))
        {
            return false;
        }

        return string.Equals(presented.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(presented.Authority, expected.Authority, StringComparison.OrdinalIgnoreCase);
    }
}
