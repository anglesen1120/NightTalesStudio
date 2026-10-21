using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.UnitTests.TestKit;
using NightTales.Infrastructure.UnitTests.TestKit.Probes;
using NightTales.TestKit.Auth;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Persistence;

/// <summary>
/// The outbox write side over a real PostgreSQL 17 (09 README D11, 09.2.4 plan §3 and its tests table):
/// a domain event becomes an <c>outbox_messages</c> row inside the same <c>SaveChanges</c> as its
/// aggregate — same transaction, same rollback — and refuses to be stored when it has no user to run as.
/// </summary>
/// <remarks>
/// The aggregate and the event it raises are test-only (<see cref="ProbeAggregate"/>,
/// <see cref="ProbeDomainEvent"/>), because the platform has no domain event yet; everything else — the
/// context, the model, the interceptor, the registry — is the production one. The read side is
/// <c>OutboxDispatcherTests</c>, which drives the Worker's dispatcher against the rows this class writes.
/// </remarks>
// 09.1.5-T1: creates its own database on the NTS_TEST_PG server, so the unit job (no NTS_TEST_PG)
// excludes it with Category=Integration and the CI integration job runs it.
[Trait("Category", "Integration")]
public sealed class OutboxSaveChangesInterceptorTests(PostgresTestFixture fixture) : IClassFixture<PostgresTestFixture>
{
    private static readonly DateTimeOffset OccurredAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The moved <c>UnitOfWork_Success_CommitsEffectsAndOutboxTogether</c> (09.1.3-T2): the aggregate row
    /// and the outbox row are written by one <c>SaveChanges</c> and are both there after the commit, with
    /// the payload, the user, the due time and the command's trace the dispatcher needs.
    /// </summary>
    [Fact]
    public async Task Outbox_EventCommittedWithAggregate_InOneTransaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = Guid.CreateVersion7();
        var aggregate = ProbeAggregate.Create(userId, OccurredAt, ProbeStatus.Draft, "before");
        aggregate.RenameWithEvent("after", OccurredAt);

        string? commandTrace;
        await using (var context = ContextOf(userId))
        {
            context.Add(aggregate);

            using (var command = new Activity("probe.command"))
            {
                command.Start();
                await context.SaveChangesAsync(ct);
                commandTrace = command.Id;
            }
        }

        await using (var context = ContextOf(userId))
        {
            var stored = await context.Set<ProbeAggregate>().SingleAsync(row => row.Id == aggregate.Id, ct);
            stored.Name.ShouldBe("after", "the aggregate row and its event were saved by the same call");

            var message = await context.OutboxMessages.SingleAsync(row => row.UserId == userId, ct);
            message.Type.ShouldBe("probe_domain_event.v1", "the name comes from [OutboxEvent]");
            message.OccurredAt.ShouldBe(OccurredAt);
            message.NextAttemptAt.ShouldBe(OccurredAt, "a new event is due at once");
            message.Attempts.ShouldBe(0);
            message.ProcessedAt.ShouldBeNull();
            message.FailedAt.ShouldBeNull();
            message.Error.ShouldBeNull();
            message.TraceParent.ShouldBe(commandTrace, "the event keeps the trace of the command that raised it (NFR-09-4)");

            using var payload = JsonDocument.Parse(message.Payload);
            payload.RootElement.GetProperty("aggregateId").GetGuid().ShouldBe(aggregate.Id);
            payload.RootElement.GetProperty("name").GetString().ShouldBe(
                "after",
                "the payload is the event's own record, camelCased (09 plan §5)");
        }

        aggregate.DomainEvents.ShouldBeEmpty("the interceptor cleared the events it stored");
    }

    /// <summary>
    /// The rollback half of the D5 contract: a command that fails writes neither the aggregate nor its
    /// outbox row, because the interceptor adds the row to the same transaction the command rolls back.
    /// </summary>
    [Fact]
    public async Task Outbox_CommandRollback_LeavesNoRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = Guid.CreateVersion7();
        var aggregate = ProbeAggregate.Create(userId, OccurredAt, ProbeStatus.Draft, "before");
        aggregate.RenameWithEvent("after", OccurredAt);

        await using (var context = ContextOf(userId))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            context.Add(aggregate);
            await context.SaveChangesAsync(ct);

            // The handler returned an error result: UnitOfWorkBehavior rolls the command's transaction back.
            await transaction.RollbackAsync(ct);
        }

        await using (var context = ContextOf(userId))
        {
            (await context.Set<ProbeAggregate>().AnyAsync(row => row.Id == aggregate.Id, ct))
                .ShouldBeFalse("the aggregate row rolled back");
            (await context.OutboxMessages.CountAsync(row => row.UserId == userId, ct))
                .ShouldBe(0, "and so did the outbox row: one SaveChanges means one transaction (09 README D5)");
        }
    }

    /// <summary>
    /// A non-platform event raised by an aggregate that has no user fails the command: a handler must never
    /// run unfiltered, and the bug belongs to the command that raised it (09 README D10/D11, UC6).
    /// </summary>
    [Fact]
    public async Task Outbox_NonPlatformEventWithoutUser_FailsTheCommand()
    {
        var ct = TestContext.Current.CancellationToken;
        var aggregate = ProbePlatformAggregate.Create("no-user-probe");
        aggregate.RaiseUnattributedEvent(OccurredAt);

        await using (var context = fixture.CreateAppDbContext())
        {
            context.Add(aggregate);

            var exception = await Should.ThrowAsync<InvalidOperationException>(() => context.SaveChangesAsync(ct));
            exception.Message.ShouldContain("PlatformEvent");
        }

        await using (var context = fixture.CreateAppDbContext())
        {
            (await context.Set<ProbePlatformAggregate>().AnyAsync(row => row.Id == aggregate.Id, ct))
                .ShouldBeFalse("the command failed before anything was written");
            (await context.OutboxMessages.CountAsync(row => row.Type == "probe_unattributed_event.v1", ct))
                .ShouldBe(0, "no row is stored for an event that has no user to run as");
        }
    }

    /// <summary>
    /// The other half of that rule: the same aggregate may raise an event marked
    /// <see cref="NightTales.Domain.Common.PlatformEventAttribute"/>, and the row is stored with a
    /// <c>null</c> user (09 README D11).
    /// </summary>
    [Fact]
    public async Task Outbox_PlatformEventWithoutUser_IsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var aggregate = ProbePlatformAggregate.Create("platform-probe");
        aggregate.RaisePlatformEvent(OccurredAt);

        await using (var context = fixture.CreateAppDbContext())
        {
            context.Add(aggregate);
            await context.SaveChangesAsync(ct);
        }

        await using (var context = fixture.CreateAppDbContext())
        {
            var message = await context.OutboxMessages.SingleAsync(
                row => row.Type == "probe_platform_event.v1",
                ct);

            message.UserId.ShouldBeNull("a platform event carries no user");
            message.NextAttemptAt.ShouldBe(OccurredAt);
        }
    }

    /// <summary>A write context of the class database, as <paramref name="userId"/>.</summary>
    /// <param name="userId">The user the context's tenant filter is pinned to.</param>
    private AppDbContext ContextOf(Guid userId) =>
        new(ProbeContexts.AppOptions(fixture.ConnectionString), new TestCurrentUser { Id = userId });
}
