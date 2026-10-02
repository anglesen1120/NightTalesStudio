using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Infrastructure.Security;

namespace NightTales.Api.DependencyInjection;

/// <summary>
/// The authorization surface of 09.10.4-T1 ([RULED — README §B10 WT34]): default and fallback policies
/// reject the enrolment-only token; <c>MfaEnrolment</c> lets it reach the two enrolment routes.
/// </summary>
/// <remarks>
/// <para>
/// BD4's enrolment-only token (<c>scope = mfa_enrol</c>) exists so an owner or an admin who has not
/// enrolled yet can finish enrolling and nothing else. The two halves of that promise are:
/// </para>
/// <list type="number">
/// <item><description>
/// the default and fallback authorization policies reject that scope. The default policy also requires
/// authentication for endpoints with <c>RequireAuthorization()</c>; fallback keeps the Application
/// pipeline's anonymous-response contract for endpoints without authorization metadata.
/// </description></item>
/// <item><description>
/// the <c>MfaEnrolment</c> policy on <c>/me/mfa/setup</c> and <c>/me/mfa/verify</c>, which requires
/// authentication and nothing else, so the enrolment token reaches exactly those two routes.
/// </description></item>
/// </list>
/// <para>
/// A refused request is answered 404 <c>not_found</c> by
/// <see cref="NightTales.Api.Http.PlatformAuthorizationResultHandler"/>, the same answer the platform
/// gives for another user's resource (09 README D10): a caller must not learn that a surface exists from
/// the shape of the refusal.
/// </para>
/// <para>
/// The fallback policy deliberately does <em>not</em> require authentication: the default-deny of the
/// Application pipeline (09 README D4/D10) is what answers 401 for an anonymous caller, and duplicating it
/// here would replace every documented problem body with a bare 401.
/// </para>
/// </remarks>
public static class MfaAuthorizationServiceCollectionExtensions
{
    /// <summary>The policy the two enrolment routes carry (WT34).</summary>
    public const string MfaEnrolmentPolicy = "MfaEnrolment";

    /// <summary>The claim type carrying the token's scope (BD3/BD4).</summary>
    public const string EnrolmentScopeClaim = "scope";

    /// <summary>Registers the two policies and the result handler that answers 404.</summary>
    /// <param name="services">The Api's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddNightTalesMfaAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorization(options =>
        {
            // WT34: the enrolment-only token reaches these two routes and no other.
            options.AddPolicy(MfaEnrolmentPolicy, policy => policy.RequireAuthenticatedUser());

            options.DefaultPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireAssertion(context => !IsEnrolmentToken(context.User))
                .Build();

            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAssertion(context => !IsEnrolmentToken(context.User))
                .Build();
        });

        // A forbidden result is 404 not_found, never 403 (09 README D10/§5.4), and a challenge is the
        // platform's own 401 unauthorized problem instead of an empty body.
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, Http.PlatformAuthorizationResultHandler>();

        return services;
    }

    /// <summary>Whether the principal is the enrolment-only token of BD4.</summary>
    /// <param name="principal">The authenticated principal of the request.</param>
    public static bool IsEnrolmentToken(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return string.Equals(
            principal.FindFirst(EnrolmentScopeClaim)?.Value,
            Es256TokenIssuer.EnrolmentScope,
            StringComparison.Ordinal);
    }
}
