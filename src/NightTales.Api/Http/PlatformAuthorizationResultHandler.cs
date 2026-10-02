using ErrorOr;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using NightTales.Application.Common.Errors;

namespace NightTales.Api.Http;

/// <summary>
/// Turns a failed authorization decision into the platform's problem body: a challenge is 401
/// <c>unauthorized</c>, a forbidden result is 404 <c>not_found</c>.
/// </summary>
/// <remarks>
/// <para>
/// The 404 is not an accident of this handler: 09 README D10 and §5.4 answer another user's resource and
/// an insufficient role the same way, so that no surface ever confirms it exists to a caller who may not
/// use it. The enrolment-only token of BD4 is refused by the fallback policy (WT34) and gets exactly that
/// answer.
/// </para>
/// <para>
/// A policy that names its own authentication schemes owns its challenge, so this handler steps aside for
/// it: the hub's <c>HubTicket</c> scheme answers 401 <c>hub_ticket_invalid</c> from its own
/// <c>HandleChallengeAsync</c> (BD11), and replacing that body with the platform's generic 401 would
/// break a contract the realtime surface already publishes. The two policies this task adds name no
/// scheme, so the enrolment routes and the fallback keep the platform's problem shape.
/// </para>
/// <para>
/// The challenge path exists so an unauthenticated call to a route that carries its own policy still gets
/// the documented <c>unauthorized</c> problem instead of the framework's empty 401.
/// </para>
/// </remarks>
public sealed class PlatformAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    /// <summary>The framework's handler, used for the policies that own their challenge.</summary>
    private readonly AuthorizationMiddlewareResultHandler _schemeOwned = new();

    /// <inheritdoc />
    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(context);

        if (policy.AuthenticationSchemes is { Count: > 0 })
        {
            await _schemeOwned.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);

            return;
        }

        if (authorizeResult.Forbidden)
        {
            await ProblemDetailsWriter
                .ToProblem([Errors.NotFound()], context)
                .ExecuteAsync(context)
                .ConfigureAwait(false);

            return;
        }

        if (authorizeResult.Challenged)
        {
            await ProblemDetailsWriter
                .ToProblem(
                    [Error.Unauthorized(ErrorCodes.Unauthorized, "Authentication is required.")],
                    context)
                .ExecuteAsync(context)
                .ConfigureAwait(false);

            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
