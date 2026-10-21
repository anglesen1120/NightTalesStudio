using NightTales.Application.Abstractions;
using NightTales.Domain.Common;

namespace NightTales.TestKit.Auth;

/// <summary>
/// Settable <see cref="ICurrentUser"/> for tests (09 README D10, D13). The production implementations
/// are 09.2.3-T1 (<c>HttpCurrentUser</c> for the Api, <c>ScopedCurrentUser</c> for the Worker), so a
/// test that needs a different actor than <see cref="AnonymousCurrentUser"/> sets this fake instead.
/// </summary>
public sealed class TestCurrentUser : ICurrentUser
{
    public Guid? Id { get; set; }

    public PlatformRole Role { get; set; } = PlatformRole.User;

    public ActorType Actor { get; set; } = ActorType.User;

    /// <summary>The <c>sid</c> of the fake caller's token, when a test needs a session-scoped actor.</summary>
    public Guid? SessionId { get; set; }

    /// <summary>An authenticated scope is exactly a scope with an <see cref="Id"/>.</summary>
    public bool IsAuthenticated => Id is not null;
}
