using NightTales.Domain.Common;

namespace NightTales.Application.Abstractions;

/// <summary>
/// Temporary <see cref="ICurrentUser"/> for composition roots that have no authentication yet:
/// every scope is anonymous (<see cref="Id"/> is <c>null</c>), so the tenant filter hides all
/// user-owned rows. 09.2.3-T1 replaces this registration with <c>HttpCurrentUser</c> (Api) and
/// <c>ScopedCurrentUser</c> (Worker).
/// </summary>
public sealed class AnonymousCurrentUser : ICurrentUser
{
    public Guid? Id => null;

    public bool IsAuthenticated => false;

    public PlatformRole Role => PlatformRole.User;

    public ActorType Actor => ActorType.User;

    /// <summary>There is no token in this scope, so there is no session either.</summary>
    public Guid? SessionId => null;
}
