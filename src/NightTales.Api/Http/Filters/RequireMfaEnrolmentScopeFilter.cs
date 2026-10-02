using ErrorOr;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NightTales.Api.DependencyInjection;
using NightTales.Application.Common.Errors;
using NightTales.Infrastructure.Security;

namespace NightTales.Api.Http.Filters;

/// <summary>
/// The admission half of WT34: <c>/me/mfa/setup</c> and <c>/me/mfa/verify</c> are the two routes the
/// enrolment-only token may reach, so this filter asserts the caller is authenticated and that any scope
/// the token carries is exactly BD4's <c>mfa_enrol</c>.
/// </summary>
/// <remarks>
/// <para>
/// The rejection half — the token reaching <em>nothing else</em> — is the fallback authorization policy of
/// <see cref="MfaAuthorizationServiceCollectionExtensions"/>, which runs before this filter. Together they
/// are the ruling: the fallback refuses the scope everywhere, and these two routes opt into the
/// <c>MfaEnrolment</c> policy and this filter.
/// </para>
/// <para>
/// A scope that is neither absent nor <c>mfa_enrol</c> is not a value this platform issues: it is refused
/// 401 <c>unauthorized</c> rather than honoured, so a future scope can never silently unlock these routes.
/// </para>
/// </remarks>
public sealed class RequireMfaEnrolmentScopeFilter : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var principal = context.HttpContext.User;

        if (principal.Identity?.IsAuthenticated is not true)
        {
            return ProblemDetailsWriter.ToProblem(
                [Error.Unauthorized(ErrorCodes.Unauthorized, "Authentication is required.")],
                context.HttpContext);
        }

        var scope = principal.FindFirst(MfaAuthorizationServiceCollectionExtensions.EnrolmentScopeClaim)?.Value;

        if (scope is not null
            && !string.Equals(scope, Es256TokenIssuer.EnrolmentScope, StringComparison.Ordinal))
        {
            return ProblemDetailsWriter.ToProblem(
                [Error.Unauthorized(ErrorCodes.Unauthorized, "Authentication is required.")],
                context.HttpContext);
        }

        return await next(context).ConfigureAwait(false);
    }
}

/// <summary>Applies <see cref="RequireMfaEnrolmentScopeFilter"/> to a route.</summary>
public static class RequireMfaEnrolmentScopeExtensions
{
    /// <summary>
    /// Marks a route as reachable with BD4's enrolment-only token: it carries the <c>MfaEnrolment</c>
    /// policy (so the fallback policy does not refuse the scope here) and the filter above.
    /// </summary>
    /// <param name="builder">The route to mark.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static RouteHandlerBuilder RequireMfaEnrolmentScope(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Both calls add metadata to this same builder instance, so the returned builder is discarded.
        _ = builder.RequireAuthorization(MfaAuthorizationServiceCollectionExtensions.MfaEnrolmentPolicy);

        return builder.AddEndpointFilter(new RequireMfaEnrolmentScopeFilter());
    }
}
