using NightTales.Application.Abstractions;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Security;

namespace NightTales.Api.Security;

/// <summary>
/// The Api's <see cref="ICurrentUser"/> (09 README D10): the authenticated caller's <c>sub</c> and
/// <c>role</c> claims, decorated with the scoped <see cref="ScopedCurrentUser"/> so that outbox-style
/// work started in-process can also run as one user through <see cref="ISystemContext.For"/>.
/// </summary>
/// <remarks>
/// <para>
/// Claims win for ordinary requests; a system override wins once it is active, because that is the
/// whole point of <c>For</c> — the work runs as the user named by the outbox payload, not as whoever
/// happened to make the request that queued it.
/// </para>
/// <para>
/// The role comes from a closed set of snake_case names, never from
/// <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/>, which would also accept a numeric value
/// (<c>2</c> is <see cref="PlatformRole.Owner"/>) or a comma-joined flag list.
/// </para>
/// </remarks>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor, ScopedCurrentUser system)
    : ICurrentUser, ISystemContext, ITenantScopeObserver
{
    /// <summary>The claim type carrying the user id (09 README D16).</summary>
    public const string SubjectClaimType = "sub";

    /// <summary>The claim type carrying the platform role (09 README D16).</summary>
    public const string RoleClaimType = "role";

    /// <summary>The claim type carrying the refresh family (09 README BD3).</summary>
    public const string SessionClaimType = "sid";

    /// <summary>
    /// The caller's id, or the user the scope was pinned to by <see cref="For"/>; <c>null</c> for an
    /// anonymous request, which therefore sees no user-owned row.
    /// </summary>
    public Guid? Id => system.Id ?? SubjectId();

    /// <summary>An authenticated scope is exactly a scope with an <see cref="Id"/>.</summary>
    public bool IsAuthenticated => Id is not null;

    /// <inheritdoc />
    /// <remarks>
    /// A valid <c>sub</c> is required before the <c>role</c> claim is believed: a request with no
    /// user (no <c>sub</c>, or <see cref="Guid.Empty"/>) is <see cref="PlatformRole.User"/> even if it
    /// carries a role claim, so a token cannot be given an elevated role without naming the user it
    /// is elevated for.
    /// </remarks>
    public PlatformRole Role => system.Id is not null ? system.Role : Id is null ? PlatformRole.User : RoleOf(ClaimRole());

    /// <inheritdoc />
    public ActorType Actor => system.Id is not null ? ActorType.System : ActorOf(Role);

    /// <summary>
    /// The refresh family of the caller's access token (<c>sid</c>), or <c>null</c> when there is none:
    /// an anonymous request, a system override, or the enrolment-only token 09.3.2-T1 issues (BD4).
    /// </summary>
    /// <remarks>
    /// Read like <see cref="Id"/>: an unparseable or empty value is no session rather than an exception, so
    /// a token that was tampered with cannot make a request fail in an unexpected place — the signature
    /// check is what rejects it.
    /// </remarks>
    public Guid? SessionId => system.Id is not null ? null : ClaimGuid(SessionClaimType);

    /// <inheritdoc />
    public IDisposable For(Guid userId) => system.For(userId);

    /// <inheritdoc />
    public void MarkObserved() => system.MarkObserved();

    private string? ClaimRole() => accessor.HttpContext?.User.FindFirst(RoleClaimType)?.Value;

    /// <summary>
    /// A guid claim, or <c>null</c> when it is absent, unparseable or the empty guid: <see cref="Guid.Empty"/>
    /// is not an id, and treating it as one would make every filtered query look for
    /// <c>user_id = 00000000-…</c> instead of owning nothing.
    /// </summary>
    private Guid? ClaimGuid(string claimType) =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirst(claimType)?.Value, out var id) && id != Guid.Empty
            ? id
            : null;

    /// <summary>The <c>sub</c> claim as a user id, or <c>null</c> when there is none.</summary>
    private Guid? SubjectId() => ClaimGuid(SubjectClaimType);

    private static PlatformRole RoleOf(string? value) => value switch
    {
        _ when string.Equals(value, nameof(PlatformRole.Admin), StringComparison.OrdinalIgnoreCase) => PlatformRole.Admin,
        _ when string.Equals(value, nameof(PlatformRole.Owner), StringComparison.OrdinalIgnoreCase) => PlatformRole.Owner,
        _ => PlatformRole.User,
    };

    private static ActorType ActorOf(PlatformRole role) =>
        role is PlatformRole.Admin or PlatformRole.Owner ? ActorType.Admin : ActorType.User;
}
