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

    /// <summary>
    /// The refresh family the caller's access token belongs to — the <c>sid</c> claim (09 README BD3) —
    /// or <c>null</c> when the caller carries no token: an anonymous request, a system scope and the
    /// enrolment-only token of BD4 all have no family.
    /// </summary>
    /// <remarks>
    /// Declared by 09.3.2-T1, which issues the claim, and consumed by the tasks that need to look at the
    /// live family: 09.3.2-T2's per-request security-stamp/session check and the re-auth gates of
    /// 09.10.4-T1 ([OPEN] D10 extension, 09.3-auth plan §10).
    /// </remarks>
    Guid? SessionId { get; }
}
