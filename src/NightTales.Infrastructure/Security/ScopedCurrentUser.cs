using NightTales.Application.Abstractions;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Telemetry;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The Worker's <see cref="ICurrentUser"/> and its <see cref="ISystemContext"/> in one scoped
/// instance (09 README D10). A scope starts anonymous — every user-owned table reads as empty — and
/// <see cref="For"/> pins it to exactly one user, as <see cref="ActorType.System"/>, for the work the
/// outbox dispatcher or a recurring job runs on that user's behalf.
/// </summary>
/// <remarks>
/// <para>
/// The guards are the D10 ordering rule made operational: <see cref="For"/> rejects
/// <see cref="Guid.Empty"/>, a second call inside one scope, and a call made after the scope already
/// created a <c>DbContext</c> (the contexts report that through <see cref="ITenantScopeObserver"/>).
/// The last one is the trap the rule exists for — a context created before <see cref="For"/> captured
/// <c>null</c> as its user and would silently read nothing, or, worse, a scope that ran one job would
/// keep the previous user.
/// </para>
/// <para>
/// The same instance answers both interfaces (the Worker registers it that way) — and the Api's
/// <c>HttpCurrentUser</c> decorates one — so the guard and the filter always look at one state.
/// </para>
/// </remarks>
public sealed class ScopedCurrentUser : ICurrentUser, ISystemContext, ITenantScopeObserver
{
    private Guid? _userId;
    private bool _system;
    private bool _observed;
    private bool _forCalled;

    /// <inheritdoc />
    public Guid? Id => _userId;

    /// <inheritdoc />
    public bool IsAuthenticated => _userId is not null;

    /// <summary>
    /// A system context runs as a user whose role the Worker does not know; the platform role is only
    /// ever read from a token (the Api's implementation).
    /// </summary>
    public PlatformRole Role => PlatformRole.User;

    /// <inheritdoc />
    public ActorType Actor => _system ? ActorType.System : ActorType.User;

    /// <summary>
    /// Work started in-process runs on a user's behalf, not inside one of that user's sessions: there is no
    /// token and therefore no <c>sid</c> (09.3.2-T1 declares the member).
    /// </summary>
    public Guid? SessionId => null;

    /// <inheritdoc />
    public IDisposable For(Guid userId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(userId, Guid.Empty);

        if (_forCalled)
        {
            throw new InvalidOperationException(
                "ISystemContext.For must not be called twice in one scope (09 README D10).");
        }

        if (_observed)
        {
            throw new InvalidOperationException("ISystemContext.For must run before any DbContext is created in this scope");
        }

        _userId = userId;
        _system = true;
        _forCalled = true;
        AmbientActor.Set(userId);

        return new SystemScope(this);
    }

    /// <inheritdoc />
    public void MarkObserved() => _observed = true;

    /// <summary>
    /// Releases the scope back to anonymous when the work of one user is over. The scope remembers
    /// that <see cref="For"/> already ran (through the never-reset <c>_forCalled</c> flag), so a later
    /// <see cref="For"/> in the same scope fails whether or not this handle was disposed: a scope that
    /// has run a unit of work is never reused for another user.
    /// </summary>
    private sealed class SystemScope(ScopedCurrentUser owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner._userId = null;
            owner._system = false;
            AmbientActor.Clear();
        }
    }
}
