using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Messaging;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;

namespace NightTales.Application.Behaviors;

/// <summary>
/// Denies by default and honours <c>IRequireRole</c> / <c>[AllowAnonymousRequest]</c> (09 README D4).
/// An unauthenticated caller gets 401 <c>unauthorized</c>; an authenticated caller below the role the
/// message requires gets 404 <c>not_found</c>, the same answer a missing row gets, so the admin and
/// owner surfaces never confirm that they exist (never 403, 09 §5.4).
/// </summary>
/// <remarks>
/// The <c>system</c> actor passes role checks for <see cref="PlatformRole.User"/> requirements only
/// [DECISION]: a background job never performs an admin action.
/// </remarks>
public sealed class AuthorizationBehavior<TMessage, TResponse>(ICurrentUser currentUser)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
    where TResponse : IErrorOr
{
    private const string UnauthenticatedDescription = "Authentication is required.";

    /// <summary>
    /// Whether the message opted out of the check. Resolved once per closed message type: the attribute
    /// is compile-time metadata (<c>Inherited = false</c>), so a per-request <c>IsDefined</c> would only
    /// repeat the same reflection call.
    /// </summary>
    private static readonly bool AllowsAnonymous =
        typeof(TMessage).IsDefined(typeof(AllowAnonymousRequestAttribute), inherit: false);

    /// <inheritdoc />
    public ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        if (AllowsAnonymous)
        {
            return next(message, cancellationToken);
        }

        if (!currentUser.IsAuthenticated)
        {
            return Reject(Error.Unauthorized(ErrorCodes.Unauthorized, UnauthenticatedDescription));
        }

        if (message is IRequireRole requiresRole && !Satisfies(requiresRole.RequiredRole))
        {
            // 09 README D10/§5.4: a role below the requirement is answered exactly like a row of
            // another user — not_found, never forbidden.
            return Reject(Errors.NotFound());
        }

        return next(message, cancellationToken);
    }

    /// <summary>
    /// Holds at least the required role. The roles are ordered User &lt; Admin &lt; Owner, and the
    /// <c>system</c> actor passes only a <see cref="PlatformRole.User"/> requirement.
    /// </summary>
    private bool Satisfies(PlatformRole required) =>
        currentUser.Actor is ActorType.System
            ? required is PlatformRole.User
            : currentUser.Role >= required;

    private static ValueTask<TResponse> Reject(Error error) =>
        ValueTask.FromResult(ErrorOrFactory<TResponse>.From([error]));
}
