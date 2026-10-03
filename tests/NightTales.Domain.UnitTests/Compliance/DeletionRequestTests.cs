using NightTales.Domain.Compliance.Deletion;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Compliance;

/// <summary>The invariants of <see cref="DeletionRequest"/> (13.3.1-T1 plan §3.1): deadlines, the target matrix and the transitions.</summary>
public sealed class DeletionRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid User = Guid.CreateVersion7();

    [Theory]
    [InlineData(DeletionScope.Channel, DeletionReason.GoogleRevoke, 30)]
    [InlineData(DeletionScope.Channel, DeletionReason.InAppRevoke, 7)]
    [InlineData(DeletionScope.Episode, DeletionReason.UserRequest, 7)]
    [InlineData(DeletionScope.Account, DeletionReason.AccountDelete, 7)]
    public void DeletionRequest_Create_SetsDeadlinePerReason(DeletionScope scope, DeletionReason reason, int days)
    {
        var request = DeletionRequest.Create(User, scope, scope == DeletionScope.Account ? null : Guid.CreateVersion7(), reason, Now);

        request.IsError.ShouldBeFalse();
        request.Value.RequestedAt.ShouldBe(Now);
        request.Value.DueAt.ShouldBe(Now.AddDays(days));
        request.Value.Status.ShouldBe(DeletionStatus.Pending);
        request.Value.CompletedAt.ShouldBeNull();
    }

    [Fact]
    public void DeletionRequest_AccountScope_RejectsTargetId() =>
        DeletionRequest.Create(User, DeletionScope.Account, Guid.CreateVersion7(), DeletionReason.AccountDelete, Now)
            .FirstError.Code.ShouldBe("validation_failed");

    [Theory]
    [InlineData(DeletionScope.Channel, DeletionReason.InAppRevoke)]
    [InlineData(DeletionScope.Episode, DeletionReason.UserRequest)]
    public void DeletionRequest_ChannelEpisodeScope_RequiresTargetId(DeletionScope scope, DeletionReason reason)
    {
        DeletionRequest.Create(User, scope, null, reason, Now).IsError.ShouldBeTrue();
        DeletionRequest.Create(User, scope, Guid.Empty, reason, Now).IsError.ShouldBeTrue();
    }

    [Fact]
    public void DeletionRequest_ReasonScopeMatrix_IsEnforced()
    {
        DeletionRequest.Create(User, DeletionScope.Episode, Guid.CreateVersion7(), DeletionReason.AccountDelete, Now).IsError.ShouldBeTrue();
        DeletionRequest.Create(User, DeletionScope.Episode, Guid.CreateVersion7(), DeletionReason.GoogleRevoke, Now).IsError.ShouldBeTrue();
        DeletionRequest.Create(User, DeletionScope.Account, null, DeletionReason.InAppRevoke, Now).IsError.ShouldBeTrue();
        DeletionRequest.Create(Guid.Empty, DeletionScope.Account, null, DeletionReason.AccountDelete, Now).IsError.ShouldBeTrue();
    }

    [Fact]
    public void DeletionRequest_CompletionAndReapplyTransitionsAreExclusive()
    {
        var request = DeletionRequest.Create(User, DeletionScope.Account, null, DeletionReason.AccountDelete, Now).Value;

        request.Complete(Now).IsError.ShouldBeTrue("a pending row cannot complete");
        request.Reapply(Now).IsError.ShouldBeTrue("only a completed row can be re-applied");

        request.Claim(Now).IsError.ShouldBeFalse();
        request.Status.ShouldBe(DeletionStatus.InProgress);
        request.Attempts.ShouldBe(1);
        request.Claim(Now).IsError.ShouldBeTrue("an in-progress row is not claimable by the domain");

        request.Complete(Now.AddMinutes(1)).IsError.ShouldBeFalse();
        request.Status.ShouldBe(DeletionStatus.Completed);
        request.CompletedAt.ShouldBe(Now.AddMinutes(1));
        request.Claim(Now).IsError.ShouldBeTrue("completed leaves only through Reapply");
        request.Fail(Now, "x", 5).IsError.ShouldBeTrue();

        request.Reapply(Now.AddDays(1)).IsError.ShouldBeFalse();
        request.Status.ShouldBe(DeletionStatus.InProgress);
        request.CompletedAt.ShouldBeNull("CompletedAt is set iff the status is Completed");
        request.ReappliedAt.ShouldBe(Now.AddDays(1));
        request.Attempts.ShouldBe(2, "attempts are monotonic");
    }

    [Fact]
    public void DeletionRequest_Fail_KeepsTheCodeAndAllowsRetryWhileAttemptsRemain()
    {
        var request = DeletionRequest.Create(User, DeletionScope.Account, null, DeletionReason.AccountDelete, Now).Value;
        request.Claim(Now).IsError.ShouldBeFalse();

        request.Fail(Now, "service_unavailable", 2).IsError.ShouldBeFalse();

        request.Status.ShouldBe(DeletionStatus.Failed);
        request.LastErrorCode.ShouldBe("service_unavailable");
        request.CompletedAt.ShouldBeNull();
        request.HasAttemptsLeft(2).ShouldBeTrue();
        request.Claim(Now).IsError.ShouldBeFalse("a failed row is retried");
        request.Fail(Now, "service_unavailable", 2).IsError.ShouldBeFalse();
        request.HasAttemptsLeft(2).ShouldBeFalse();
    }

    [Fact]
    public void DeletionRequest_Restore_KeepsIdAndDates_AndRefusesATamperedDeadline()
    {
        var id = Guid.CreateVersion7();
        var target = Guid.CreateVersion7();

        var restored = DeletionRequest.Restore(id, User, DeletionScope.Channel, target, DeletionReason.GoogleRevoke, Now, Now.AddDays(30), Now.AddDays(2));
        var tampered = DeletionRequest.Restore(id, User, DeletionScope.Channel, target, DeletionReason.GoogleRevoke, Now, Now.AddDays(7), Now);

        restored.Value.Id.ShouldBe(id);
        restored.Value.RequestedAt.ShouldBe(Now);
        restored.Value.ReappliedAt.ShouldBe(Now.AddDays(2));
        restored.Value.Status.ShouldBe(DeletionStatus.Pending);
        tampered.IsError.ShouldBeTrue();
    }

    [Fact]
    public void DeletionResult_KeepsObjectsDeletedAndRowsAffectedShape()
    {
        var result = new DeletionResult(ObjectsDeleted: 3, RowsAffected: 5);

        result.ObjectsDeleted.ShouldBe(3);
        result.RowsAffected.ShouldBe(5);
        typeof(DeletionResult).GetConstructors().Single().GetParameters().Select(parameter => parameter.Name)
            .ShouldBe(["ObjectsDeleted", "RowsAffected"]);
    }

    [Fact]
    public void DeletionWire_RoundTripsEveryValue()
    {
        foreach (var scope in Enum.GetValues<DeletionScope>())
        {
            DeletionWire.TryParseScope(scope.ToWire(), out var parsed).ShouldBeTrue();
            parsed.ShouldBe(scope);
        }

        foreach (var reason in Enum.GetValues<DeletionReason>())
        {
            DeletionWire.TryParseReason(reason.ToWire(), out var parsed).ShouldBeTrue();
            parsed.ShouldBe(reason);
        }

        DeletionWire.TryParseScope("tenant", out _).ShouldBeFalse();
    }
}
