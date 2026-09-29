using NightTales.Domain.Common;

namespace NightTales.Application.Abstractions;

/// <summary>
/// The authenticated actor of the current scope (09 README D10). The tenant query filter compares
/// <c>IUserOwned.UserId</c> with <see cref="Id"/>, so an anonymous scope sees no user-owned rows.
/// </summary>
public interface ICurrentUser
{
    Guid? Id { get; }

    bool IsAuthenticated { get; }

    PlatformRole Role { get; }

    ActorType Actor { get; }
}
