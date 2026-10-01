using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Common.Errors;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence.Audit;
using NightTales.Infrastructure.Persistence.UnitOfWork;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Audit;

/// <summary>
/// The write path of <c>audit_logs</c> (09.3.6-T1 Tests table): the pipeline's row commits inside the
/// command's transaction and vanishes when the command fails, the non-Mediator <c>IAuditWriter</c>
/// records the operator tuple of 11 T5, and the feed's predicate can only ever return the caller's own
/// user-visible rows. The keyed-replay leg lives in <see cref="AuditBehaviorTests"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuditWriteTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AuditedCommand_RowCommitsInTheSameTransaction()
    {
        var userId = await CreateUserAsync();

        var response = await Runner(userId).SendAsync(new AuditProbeCommand("success"), Ct);

        response.IsError.ShouldBeFalse();
        Rows("success").ShouldBe(1, "the row commits with the change, inside the UnitOfWork transaction");

        // The handler leaves a row the table's own CHECK refuses in the same context, so the commit's
        // SaveChanges refuses it and the audit row the behaviour added afterwards rolls back with it
        // (09.3.6-T1 failure handling: the refused audit insert is 503 and nothing commits).
        var refused = await Runner(userId)
            .SendAsync(new AuditProbeCommand("invalid", Mode: AuditProbeMode.InvalidInsert), Ct);

        refused.IsError.ShouldBeTrue();
        refused.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
        Rows("invalid").ShouldBe(0, "a change is never recorded without it, and it is never recorded without a change");
    }

    [Fact]
    public async Task HandlerError_RollsBackBoth()
    {
        var userId = await CreateUserAsync();

        var response = await Runner(userId)
            .SendAsync(new AuditProbeCommand("conflict", Mode: AuditProbeMode.Conflict), Ct);

        response.IsError.ShouldBeTrue();
        response.FirstError.Code.ShouldBe("probe_conflict");
        Rows("conflict").ShouldBe(0, "the UnitOfWork rolls the transaction back on an error result");
    }

    [Fact]
    public async Task IAuditWriter_OperatorVerb_RecordsSystemOperatorLabel()
    {
        var userId = await CreateUserAsync();
        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = userId }, new FakeClock(Start));
        var unitOfWork = new EfUnitOfWork(context);

        await unitOfWork.BeginAsync(Ct);
        await new EfAuditWriter(context).WriteAsync(Entry("render_node.state_changed", null, Visibility.Internal, Category.Support, actorLabel: "system:operator"), Ct);
        var committed = await unitOfWork.CommitAsync(Ct);

        committed.IsError.ShouldBeFalse();
        SqlProbe.QueryStrings(
                Database,
                """
                SELECT actor_type || '|' || category || '|' || visibility || '|' || (data->>'actor')
                    || '|' || coalesce(actor_user_id::text, '-') || '|' || coalesce(subject_user_id::text, '-')
                FROM audit_logs WHERE action = 'render_node.state_changed'
                """)
            .ShouldBe(["system|support|internal|system:operator|-|-"], "the 11 T5 operator tuple (BD13)");
    }

    [Fact]
    public async Task SubjectPredicate_ReturnsOnlyTheCallersVisibleRows()
    {
        var mine = Guid.CreateVersion7();
        var theirs = Guid.CreateVersion7();
        await using var context = Database.CreateAppDbContext(new TestCurrentUser(), new FakeClock(Start));
        var unitOfWork = new EfUnitOfWork(context);
        var writer = new EfAuditWriter(context);

        await unitOfWork.BeginAsync(Ct);
        await writer.WriteAsync(Entry("account.mfa_enabled", mine, Visibility.User, Category.AccountSecurity), Ct);
        await writer.WriteAsync(Entry("account.mfa_enabled", mine, Visibility.Internal, Category.AccountSecurity), Ct);
        await writer.WriteAsync(Entry("account.mfa_enabled", theirs, Visibility.User, Category.AccountSecurity), Ct);
        (await unitOfWork.CommitAsync(Ct)).IsError.ShouldBeFalse();

        // The predicate the 09.3.6-T2 feed uses: subject_user_id = @me AND visibility = 'user'.
        SqlProbe.QueryStrings(
                Database,
                $"SELECT subject_user_id::text FROM audit_logs WHERE subject_user_id = '{mine}' AND visibility = 'user'")
            .ShouldBe([mine.ToString()], "the internal row and the other account's row are outside this predicate");

        // The table forbids the one row that could widen it: user-visible without a subject.
        SqlProbe.SqlState(Database, "INSERT INTO audit_logs (id, actor_type, action, category, visibility, data, created_at) "
            + "VALUES (gen_random_uuid(), 'system', 'probe.migration', 'account_security', 'user', '{}'::jsonb, now())")
            .ShouldBe("23514");
    }

    /// <summary>
    /// The row's <c>created_at</c> is the scope clock's instant, not the database's <c>now()</c>
    /// (09 README D8, 09 W3 CR-38): the factory already received <c>now</c>, so the timestamp interceptor
    /// must leave the non-default value alone — the retention sweep compares against this column.
    /// </summary>
    [Fact]
    public async Task AuditRow_CarriesTheScopeClockInstant()
    {
        var userId = await CreateUserAsync();

        (await Runner(userId).SendAsync(new AuditProbeCommand("clocked"), Ct)).IsError.ShouldBeFalse();

        SqlProbe.ScalarText(
                Database,
                "SELECT to_char(created_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS') FROM audit_logs "
                + "WHERE action = 'probe.audited' AND data->>'probe' = 'clocked'")
            .ShouldBe("2026-06-01T08:00:00", "the scope clock is the source of the row's instant");
    }

    /// <summary>
    /// The failure-handling table of 09.3.6-T1: when the audit insert itself is refused, the whole
    /// command rolls back and the caller gets the mapped 503 — never a committed change without its row
    /// and never a raw 500. An anonymous scope makes the behaviour project a <c>user</c>-visible row with
    /// no subject, which the table's own CHECK refuses.
    /// </summary>
    [Fact]
    public async Task AuditInsertFailure_RollsBackTheChange()
    {
        var runner = new AuditPipelineProbeRunner(Database, new TestCurrentUser { Id = null }, new FakeClock(Start));

        var refused = await runner.SendAsync(new AuditProbeCommand("anonymous"), Ct);

        refused.IsError.ShouldBeTrue();
        refused.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
        Rows("anonymous").ShouldBe(0, "the refused audit row takes the command's whole transaction with it");
    }

    /// <summary>
    /// An operator label is merged into a JSON object payload; a caller that set a label but projected
    /// something else is a programming error that must fail where it happens, not surface as a provider
    /// jsonb error (09 README BD13, 11 T5).
    /// </summary>
    [Fact]
    public async Task IAuditWriter_LabelOnNonObjectPayload_Throws()
    {
        var userId = await CreateUserAsync();
        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = userId }, new FakeClock(Start));
        var unitOfWork = new EfUnitOfWork(context);

        await unitOfWork.BeginAsync(Ct);
        var entry = Entry("probe.migration", userId, Visibility.User, Category.Episodes, actorLabel: "system:operator");

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => new EfAuditWriter(context).WriteAsync(entry with { DataJson = "[]" }, Ct));

        exception.Message.ShouldContain("JSON object");
        await unitOfWork.RollbackAsync(Ct);
    }

    private async Task<Guid> CreateUserAsync()
        => (await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct)).Id;

    private AuditPipelineProbeRunner Runner(Guid userId)
        => new(Database, new TestCurrentUser { Id = userId }, new FakeClock(Start));

    /// <summary>The rows the probe wrote for one probe body (<c>data.probe</c>).</summary>
    private long Rows(string body)
        => SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM audit_logs WHERE action = 'probe.audited' AND data->>'probe' = '{body}'");

    private static AuditEntry Entry(
        string action,
        Guid? subjectUserId,
        Visibility visibility,
        Category category,
        string? actorLabel = null) => new(
        ActorUserId: subjectUserId,
        ActorType: subjectUserId is null ? ActorType.System : ActorType.User,
        SubjectUserId: subjectUserId,
        Action: action,
        Category: category,
        Visibility: visibility,
        EntityType: null,
        EntityId: null,
        DataJson: "{}",
        Ip: null,
        UserAgent: null,
        ActorLabel: actorLabel,
        OccurredAt: Start);
}
