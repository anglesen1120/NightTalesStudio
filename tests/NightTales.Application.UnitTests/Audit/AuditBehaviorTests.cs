using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Audit;
using NightTales.Application.Behaviors;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Audit;

/// <summary>
/// The <c>AuditBehavior</c> of 09.3.6-T1 (09 README BD15, plan §3): one row on success with the values of
/// the command's attribute and the projection's summary only, nothing on an error, and the derived actor
/// type. The fake writer is hand-written (D13: no mocking library).
/// </summary>
public sealed class AuditBehaviorTests
{
    private static readonly Guid ActorId = new("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid SubjectId = new("01900000-0000-7000-8000-0000000000b2");
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AuditedCommand_Success_WritesOneRow()
    {
        var writer = new RecordingAuditWriter();
        var entityId = Guid.CreateVersion7();
        var user = new TestCurrentUser { Id = ActorId };

        var response = await HandleAsync(
            new ProbeAuditedCommand(entityId, SubjectUserId: null),
            writer,
            user,
            new FakeClock(Now),
            Result.Success);

        response.IsError.ShouldBeFalse();
        var entry = writer.Entries.ShouldHaveSingleItem();
        entry.Action.ShouldBe("probe.audited");
        entry.Category.ShouldBe(Category.Episodes);
        entry.Visibility.ShouldBe(Visibility.User);
        entry.EntityType.ShouldBe("probe");
        entry.EntityId.ShouldBe(entityId);
        entry.ActorUserId.ShouldBe(ActorId);
        entry.ActorType.ShouldBe(ActorType.User);
        entry.OccurredAt.ShouldBe(Now);
        entry.ActorLabel.ShouldBeNull();
        entry.DataJson.ShouldBe("""{"probe":"audited"}""", "data holds the projection verbatim and nothing else");
    }

    [Fact]
    public async Task AuditedCommand_Error_WritesNothing()
    {
        var writer = new RecordingAuditWriter();

        var response = await HandleAsync(
            new ProbeAuditedCommand(Guid.CreateVersion7(), SubjectUserId: null),
            writer,
            new TestCurrentUser { Id = ActorId },
            new FakeClock(Now),
            Error.Conflict("probe_conflict", "The probe handler refused."));

        response.IsError.ShouldBeTrue();
        writer.Entries.ShouldBeEmpty("the UnitOfWork rolls the transaction back, so no row may be written");
    }

    [Fact]
    public async Task UnmarkedCommand_WritesNothing()
    {
        var writer = new RecordingAuditWriter();

        var response = await HandleAsync(
            new ProbeUnmarkedCommand(Guid.CreateVersion7()),
            writer,
            new TestCurrentUser { Id = ActorId },
            new FakeClock(Now),
            Result.Success);

        response.IsError.ShouldBeFalse();
        writer.Entries.ShouldBeEmpty("without [Audited] the projection alone writes nothing");
    }

    [Fact]
    public async Task ActorType_UserWhenSubjectIsTheActor()
    {
        var writer = new RecordingAuditWriter();

        await HandleAsync(
            new ProbeAuditedCommand(Guid.CreateVersion7(), SubjectUserId: ActorId),
            writer,
            new TestCurrentUser { Id = ActorId, Role = PlatformRole.User, Actor = ActorType.User },
            new FakeClock(Now),
            Result.Success);

        var entry = writer.Entries.ShouldHaveSingleItem();
        entry.ActorType.ShouldBe(ActorType.User);
        entry.SubjectUserId.ShouldBe(ActorId);
    }

    [Fact]
    public async Task ActorType_AdminWhenSubjectDiffers()
    {
        var writer = new RecordingAuditWriter();
        var admin = new TestCurrentUser { Id = ActorId, Role = PlatformRole.Admin, Actor = ActorType.Admin };

        await HandleAsync(
            new ProbeAuditedCommand(Guid.CreateVersion7(), SubjectUserId: SubjectId),
            writer,
            admin,
            new FakeClock(Now),
            Result.Success);

        var entry = writer.Entries.ShouldHaveSingleItem();
        entry.ActorType.ShouldBe(ActorType.Admin, "a staff action on another account is what the 24-month rule keys on");
        entry.ActorUserId.ShouldBe(ActorId);
        entry.SubjectUserId.ShouldBe(SubjectId);
    }

    [Fact]
    public async Task ActorType_UserWhenAnAdminActsOnTheirOwnAccount()
    {
        var writer = new RecordingAuditWriter();

        // Plan §3's precedence: the caller-is-subject rule outranks the role, so a staff member's row
        // about their own account is `user` and is retained 12 months, not the staff rule's 24.
        await HandleAsync(
            new ProbeAuditedCommand(Guid.CreateVersion7(), SubjectUserId: ActorId),
            writer,
            new TestCurrentUser { Id = ActorId, Role = PlatformRole.Admin, Actor = ActorType.Admin },
            new FakeClock(Now),
            Result.Success);

        var entry = writer.Entries.ShouldHaveSingleItem();
        entry.ActorType.ShouldBe(ActorType.User, "the caller is the subject, so the row is not a staff action");
        entry.SubjectUserId.ShouldBe(ActorId);
    }

    [Fact]
    public async Task ActorType_System_WhenTheScopeRunsAsASystemActor()
    {
        var writer = new RecordingAuditWriter();

        // The Worker's ISystemContext.For(user) scope: the id is the user's, but the actor is the system,
        // so an outbox-driven command must not record that user as the author (plan §3, D10).
        await HandleAsync(
            new ProbeAuditedCommand(Guid.CreateVersion7(), SubjectUserId: ActorId),
            writer,
            new TestCurrentUser { Id = ActorId, Role = PlatformRole.User, Actor = ActorType.System },
            new FakeClock(Now),
            Result.Success);

        writer.Entries.ShouldHaveSingleItem().ActorType.ShouldBe(ActorType.System);
    }

    [Fact]
    public async Task SubjectUser_DefaultsToTheCaller_HasNoCallerAddress()
    {
        var writer = new RecordingAuditWriter();

        await HandleAsync(
            new ProbeAuditedCommand(Guid.CreateVersion7(), SubjectUserId: null),
            writer,
            new TestCurrentUser { Id = ActorId },
            new FakeClock(Now),
            Result.Success);

        var entry = writer.Entries.ShouldHaveSingleItem();
        entry.SubjectUserId.ShouldBe(ActorId);
        entry.Ip.ShouldBeNull("only the auth handlers write a caller address (plan §8)");
        entry.UserAgent.ShouldBeNull();
    }

    private static async ValueTask<ErrorOr<Success>> HandleAsync<TMessage>(
        TMessage command,
        RecordingAuditWriter writer,
        ICurrentUser user,
        IClock clock,
        ErrorOr<Success> response)
        where TMessage : IMessage
    {
        var behavior = new AuditBehavior<TMessage, ErrorOr<Success>>(writer, user, new AuditMetadataCache(), clock);

        return await behavior.Handle(command, (_, _) => ValueTask.FromResult(response), CancellationToken.None);
    }

    /// <summary>Records every entry the behaviour writes and does nothing else — no transaction, no save.</summary>
    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);

            return Task.CompletedTask;
        }
    }
}
