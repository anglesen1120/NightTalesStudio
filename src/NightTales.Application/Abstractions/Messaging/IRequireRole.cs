using NightTales.Domain.Common;

namespace NightTales.Application.Abstractions.Messaging;

/// <summary>
/// Declares the platform role a message requires. <c>AuthorizationBehavior</c> denies by default and
/// <see cref="AllowAnonymousRequestAttribute"/> is the explicit opt-out (09 README D4).
/// </summary>
public interface IRequireRole
{
    /// <summary>The minimum role the authenticated caller must hold.</summary>
    PlatformRole RequiredRole { get; }
}
