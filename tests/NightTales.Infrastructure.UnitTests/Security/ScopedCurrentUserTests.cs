using NightTales.Domain.Common;
using NightTales.Infrastructure.Security;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Security;

/// <summary>
/// The guards of the Worker's actor (09 README D10): a scope starts anonymous, <c>For</c> pins it once
/// to one user as <see cref="ActorType.System"/>, and every way of getting the order wrong throws
/// instead of running a job under the wrong (or no) user.
/// </summary>
public sealed class ScopedCurrentUserTests
{
    [Fact]
    public void SystemContext_For_SetsUserAndSystemActor()
    {
        var user = new ScopedCurrentUser();
        var owner = Guid.CreateVersion7();

        user.Id.ShouldBeNull("a scope that never called For sees no user-owned row");
        user.IsAuthenticated.ShouldBeFalse();
        user.Actor.ShouldBe(ActorType.User);

        using var scope = user.For(owner);

        user.Id.ShouldBe(owner);
        user.IsAuthenticated.ShouldBeTrue();
        user.Actor.ShouldBe(ActorType.System);
        user.Role.ShouldBe(PlatformRole.User, "a Worker scope never learns a platform role: that only comes from a token");
    }

    [Fact]
    public void SystemContext_For_ReleasesTheScopeWhenDisposed()
    {
        var user = new ScopedCurrentUser();

        using (user.For(Guid.CreateVersion7()))
        {
            user.Actor.ShouldBe(ActorType.System);
        }

        user.Id.ShouldBeNull("disposing the handle returns the scope to anonymous");
        user.Actor.ShouldBe(ActorType.User);
    }

    [Fact]
    public void SystemContext_For_AfterDispose_StillThrows()
    {
        var user = new ScopedCurrentUser();

        // Disposing with no DbContext ever created: the release resets the actor but must not reopen
        // the one-For-per-scope rule, or the same scope silently runs a second job as another user.
        user.For(Guid.CreateVersion7()).Dispose();

        var exception = Should.Throw<InvalidOperationException>(() => user.For(Guid.CreateVersion7()));

        exception.Message.ShouldContain("must not be called twice");
    }

    [Fact]
    public void SystemContext_For_Twice_Throws()
    {
        var user = new ScopedCurrentUser();
        using var scope = user.For(Guid.CreateVersion7());

        var exception = Should.Throw<InvalidOperationException>(() => user.For(Guid.CreateVersion7()));

        exception.Message.ShouldContain("must not be called twice");
    }

    [Fact]
    public void SystemContext_For_EmptyGuid_Throws()
    {
        var user = new ScopedCurrentUser();

        Should.Throw<ArgumentOutOfRangeException>(() => user.For(Guid.Empty));
    }

    [Fact]
    public void SystemContext_For_AfterContextCreationWasObserved_Throws()
    {
        var user = new ScopedCurrentUser();

        // Exactly what AppDbContext and ReadDbContext do in their constructors, so the ordering rule
        // fails here without a database.
        user.MarkObserved();

        var exception = Should.Throw<InvalidOperationException>(() => user.For(Guid.CreateVersion7()));

        exception.Message.ShouldBe("ISystemContext.For must run before any DbContext is created in this scope");
    }
}
