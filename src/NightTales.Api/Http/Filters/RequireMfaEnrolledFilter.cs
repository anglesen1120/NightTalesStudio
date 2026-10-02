using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;

namespace NightTales.Api.Http.Filters;

/// <summary>
/// Keeps <c>/admin/*</c> invisible until the acting owner or admin has an authenticator enrolled (BD4):
/// the answer is 404 <c>not_found</c>, never 403, because the surface must not confirm that it exists.
/// </summary>
/// <remarks>
/// <para>
/// The gate reads <c>users.two_factor_enabled</c> through <see cref="ITotpSecretStore.IsEnrolledAsync"/>,
/// so it agrees with the flag the enrolment flow sets and with the login flow that answers
/// <c>mfa_enrolment_required</c>.
/// </para>
/// <para>
/// It fails <em>closed</em>: an unauthenticated caller, a missing account and a store that cannot answer
/// all get the same 404. An admin surface must never open because a lookup failed.
/// </para>
/// </remarks>
/// <param name="secrets">The store that answers whether the caller is enrolled.</param>
/// <param name="currentUser">The caller whose enrolment is checked.</param>
public sealed class RequireMfaEnrolledFilter(
    ITotpSecretStore secrets,
    ICurrentUser currentUser) : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (currentUser.Id is { } userId)
        {
            var enrolled = await secrets
                .IsEnrolledAsync(userId, context.HttpContext.RequestAborted)
                .ConfigureAwait(false);

            if (!enrolled.IsError && enrolled.Value)
            {
                return await next(context).ConfigureAwait(false);
            }
        }

        return ProblemDetailsWriter.ToProblem([Errors.NotFound()], context.HttpContext);
    }
}

/// <summary>Applies <see cref="RequireMfaEnrolledFilter"/> to a route.</summary>
public static class RequireMfaEnrolledExtensions
{
    /// <summary>
    /// Hides this route until the caller has enrolled an authenticator (BD4): attach it to the
    /// <c>/admin/*</c> routes.
    /// </summary>
    /// <param name="builder">The route to protect.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static RouteHandlerBuilder RequireMfaEnrolled(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddEndpointFilter<RequireMfaEnrolledFilter>();
    }
}
