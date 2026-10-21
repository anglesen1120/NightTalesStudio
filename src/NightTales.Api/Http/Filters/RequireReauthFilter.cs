using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Infrastructure.Security;

namespace NightTales.Api.Http.Filters;

/// <summary>
/// The route filter behind <c>RequireReauth(window)</c> (09 README BD3/BD4): it refuses a sensitive route
/// until the caller's own refresh family has a re-authentication no older than the window.
/// </summary>
/// <remarks>
/// <para>
/// The filter is a thin caller of <see cref="IReauthGuard"/> — the decision, and the 401 it produces
/// (<c>reauth_required</c>, or <c>session_revoked</c> for a dead family), are the guard's. Later plans
/// attach it to their own sensitive routes (settings, role change, MFA reset, grants) and may shorten the
/// window; the two enrolment routes of this task pass <paramref name="unlessEnrolmentToken"/> instead,
/// because BD4's enrolment-only token has no session to re-authenticate and is admitted by its scope
/// ([RULED — README §B10 WT41]).
/// </para>
/// <para>
/// The services come from the request's own scope rather than the constructor, because the window is a
/// per-route argument: a filter with an argument cannot be resolved from the container, and reaching into
/// <c>RequestServices</c> is how the minimal-API pipeline supports exactly this shape.
/// </para>
/// </remarks>
public sealed class RequireReauthFilter : IEndpointFilter
{
    private readonly TimeSpan? _window;
    private readonly bool _unlessEnrolmentToken;

    /// <summary>Creates the filter of one route.</summary>
    /// <param name="window">
    /// The window this route requires, or null to use the configured <c>Auth:Reauth:WindowMinutes</c>
    /// (BD4: 5 minutes).
    /// </param>
    /// <param name="unlessEnrolmentToken">
    /// Whether BD4's enrolment-only token is admitted without a re-authentication (the two enrolment
    /// routes).
    /// </param>
    public RequireReauthFilter(TimeSpan? window = null, bool unlessEnrolmentToken = false)
    {
        _window = window;
        _unlessEnrolmentToken = unlessEnrolmentToken;
    }

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;

        if (_unlessEnrolmentToken
            && NightTales.Api.DependencyInjection.MfaAuthorizationServiceCollectionExtensions
                .IsEnrolmentToken(http.User))
        {
            return await next(context).ConfigureAwait(false);
        }

        // An anonymous caller is answered the platform's 401 unauthorized (09 README D4/D10), never
        // reauth_required: the guard's answer is about a session that exists and is not fresh enough, and
        // telling an unauthenticated caller to re-authenticate would describe a state they are not in.
        var currentUser = http.RequestServices.GetRequiredService<ICurrentUser>();

        if (!currentUser.IsAuthenticated)
        {
            return ProblemDetailsWriter.ToProblem(
                [MfaErrors.Unauthenticated()],
                http);
        }

        var window = _window
            ?? http.RequestServices.GetRequiredService<IOptions<MfaOptions>>().Value.ReauthWindow;

        var guard = http.RequestServices.GetRequiredService<IReauthGuard>();

        var recent = await guard.RequireRecentAsync(window, http.RequestAborted).ConfigureAwait(false);

        return recent.IsError
            ? ProblemDetailsWriter.ToProblem(recent.Errors, http)
            : await next(context).ConfigureAwait(false);
    }
}

/// <summary>Applies <see cref="RequireReauthFilter"/> to a route.</summary>
public static class RequireReauthExtensions
{
    /// <summary>
    /// Requires a recent re-authentication on this route (09 README BD3/BD4).
    /// </summary>
    /// <param name="builder">The route to protect.</param>
    /// <param name="window">The window, or null for the configured one (BD4: 5 minutes).</param>
    /// <param name="unlessEnrolmentToken">
    /// Whether the enrolment-only token is admitted without one (the two enrolment routes, WT41).
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    public static RouteHandlerBuilder RequireReauth(
        this RouteHandlerBuilder builder,
        TimeSpan? window = null,
        bool unlessEnrolmentToken = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddEndpointFilter(new RequireReauthFilter(window, unlessEnrolmentToken));
    }
}
