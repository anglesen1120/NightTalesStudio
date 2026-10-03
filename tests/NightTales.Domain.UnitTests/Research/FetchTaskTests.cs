using NightTales.Domain.Research;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Research;

/// <summary>The state machine of <see cref="FetchTask"/> (02 D10, 02.5.1 plan §3.1).</summary>
public sealed class FetchTaskTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(90);

    private static FetchTask Queued() => FetchTask.CreateFetchSource(
        Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "https://example.com/a", "example.com", Now);

    [Fact]
    public void Create_IsQueuedWithNoAttempts()
    {
        var task = Queued();

        task.Status.ShouldBe(FetchTaskStatus.Queued);
        task.Attempts.ShouldBe(0);
        task.NotBefore.ShouldBe(Now);
        task.Kind.ShouldBe(FetchTaskKind.FetchSource);
    }

    [Fact]
    public void Lease_IncrementsAttempts()
    {
        var task = Queued();

        task.Lease("fetcher-1", Now, Lease);

        task.Status.ShouldBe(FetchTaskStatus.Leased);
        task.Attempts.ShouldBe(1);
        task.LeasedBy.ShouldBe("fetcher-1");
        task.LeaseUntil.ShouldBe(Now + Lease);
        Should.Throw<InvalidOperationException>(() => task.Lease("fetcher-2", Now, Lease));
    }

    [Fact]
    public void Heartbeat_WhenNotLeased_Fails()
    {
        Should.Throw<InvalidOperationException>(() => Queued().Heartbeat(Now, Lease));

        var task = Queued();
        task.Lease("f", Now, Lease);
        task.Heartbeat(Now + TimeSpan.FromSeconds(60), Lease);
        task.LeaseUntil.ShouldBe(Now + TimeSpan.FromSeconds(60) + Lease);
    }

    [Fact]
    public void Requeue_WhenLeaseActive_Fails()
    {
        var task = Queued();
        task.Lease("f", Now, Lease);

        Should.Throw<InvalidOperationException>(() => task.Requeue(Now + TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Requeue_WhenExpired_BecomesQueued()
    {
        var task = Queued();
        task.Lease("f", Now, Lease);
        var later = Now + Lease + TimeSpan.FromSeconds(1);

        task.IsLeaseExpired(later).ShouldBeTrue();
        task.Requeue(later, TimeSpan.FromSeconds(30));

        task.Status.ShouldBe(FetchTaskStatus.Queued);
        task.LeasedBy.ShouldBeNull();
        task.NotBefore.ShouldBe(later + TimeSpan.FromSeconds(30));
        task.Attempts.ShouldBe(1, "the attempt stays counted");
    }

    [Fact]
    public void CompleteAndFail_RequireALease()
    {
        Should.Throw<InvalidOperationException>(() => Queued().Complete(Now));
        Should.Throw<InvalidOperationException>(() => Queued().Fail("source_unreachable", Now));

        var done = Queued();
        done.Lease("f", Now, Lease);
        done.Complete(Now);
        done.Status.ShouldBe(FetchTaskStatus.Done);

        var failed = Queued();
        failed.Lease("f", Now, Lease);
        failed.Fail("source_unreachable", Now);
        failed.Status.ShouldBe(FetchTaskStatus.Failed);
        failed.ErrorCode.ShouldBe("source_unreachable");
    }

    [Fact]
    public void Cancel_OnlyWhileQueuedOrLeased()
    {
        var task = Queued();
        task.Cancel(Now);
        task.Status.ShouldBe(FetchTaskStatus.Cancelled);
        Should.Throw<InvalidOperationException>(() => task.Cancel(Now));
    }
}
