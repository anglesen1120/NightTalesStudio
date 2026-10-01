using NightTales.Application.Abstractions.Audit;
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
/// The <c>_09_AuditLogs</c> migration on a freshly migrated database (09.3.6-T1 Tests table, plan §3):
/// the agreed columns, the three indexes the readers and the retention sweep walk, the four CHECK
/// constraints, and the deliberate absence of a foreign key to <c>users</c>.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class AuditLogsMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Migration_CreatesColumnsIndexesAndChecks()
    {
        SqlProbe.QueryStrings(
                Database,
                "SELECT column_name || ':' || data_type || ':' || is_nullable FROM information_schema.columns "
                + "WHERE table_name = 'audit_logs' ORDER BY ordinal_position")
            .ShouldBe(
            [
                "id:uuid:NO",
                "actor_user_id:uuid:YES",
                "actor_type:text:NO",
                "subject_user_id:uuid:YES",
                "action:text:NO",
                "category:text:NO",
                "visibility:text:NO",
                "entity_type:text:YES",
                "entity_id:uuid:YES",
                "data:jsonb:NO",
                "ip:inet:YES",
                "user_agent:text:YES",
                "created_at:timestamp with time zone:NO",
            ]);

        var indexes = SqlProbe.QueryStrings(
            Database,
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'audit_logs'");
        indexes.ShouldContain("ix_audit_logs_subject_created");
        indexes.ShouldContain("ix_audit_logs_entity");
        indexes.ShouldContain("ix_audit_logs_created_id");

        // The feed reads newest-first; the partial index only carries rows that name an entity.
        SqlProbe.ScalarText(
                Database,
                "SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_audit_logs_subject_created'")
            .ShouldContain("created_at DESC");
        SqlProbe.ScalarText(
                Database,
                "SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_audit_logs_entity'")
            .ShouldContain("WHERE (entity_id IS NOT NULL)");

        var checks = string.Join(
            '\n',
            SqlProbe.QueryStrings(
                Database,
                "SELECT conname || '=' || pg_get_constraintdef(oid) FROM pg_constraint "
                + "WHERE contype = 'c' AND conrelid = 'audit_logs'::regclass"));
        checks.ShouldContain("ck_audit_logs_actor_type");
        checks.ShouldContain("ck_audit_logs_category");
        checks.ShouldContain("ck_audit_logs_visibility");
        checks.ShouldContain("ck_audit_logs_user_visible_has_subject");
        checks.ShouldContain("'account_security'", Case.Sensitive, "the category CHECK lists the snake_case members");
        checks.ShouldContain("'internal'", Case.Sensitive, "the visibility CHECK lists the snake_case members");
        checks.ShouldContain("subject_user_id IS NOT NULL", Case.Sensitive, "a user-visible row must name its subject");

        // PostgreSQL itself refuses an unknown category and a user-visible row without a subject.
        SqlProbe.SqlState(Database, Insert(category: "marketing")).ShouldBe("23514");
        SqlProbe.SqlState(Database, Insert(visibility: "user", subjectUserId: null)).ShouldBe("23514");

        // The payload column's default is the empty object of plan §3.
        SqlProbe.Execute(Database, Insert());
        SqlProbe.ScalarText(Database, "SELECT data::text FROM audit_logs WHERE action = 'probe.migration'")
            .ShouldBe("{}");
    }

    [Fact]
    public async Task NoForeignKeyToUsers()
    {
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct);
        var subject = user.Id;

        await using (var context = Database.CreateAppDbContext(new TestCurrentUser { Id = subject }, new FakeClock(Start)))
        {
            var unitOfWork = new EfUnitOfWork(context);

            await unitOfWork.BeginAsync(Ct);
            await new EfAuditWriter(context).WriteAsync(Entry(subject), Ct);
            (await unitOfWork.CommitAsync(Ct)).IsError.ShouldBeFalse();
        }

        SqlProbe.QueryStrings(
                Database,
                "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE contype = 'f' AND conrelid = 'audit_logs'::regclass")
            .ShouldBeEmpty("a row outlives the account, so the table carries no foreign key");

        SqlProbe.Execute(Database, $"DELETE FROM users WHERE id = '{subject}'");

        SqlProbe.QueryStrings(
                Database,
                "SELECT subject_user_id::text FROM audit_logs WHERE action = 'account.mfa_enabled'")
            .ShouldBe([subject.ToString()], "the row and the id it stored survive the account (13 §5.4)");
    }

    private static string Insert(string category = "support", string visibility = "internal", Guid? subjectUserId = null) =>
        "INSERT INTO audit_logs (id, actor_type, action, category, visibility, subject_user_id, created_at) "
        + "VALUES (gen_random_uuid(), 'system', 'probe.migration', "
        + $"'{category}', '{visibility}', "
        + (subjectUserId is { } subject ? $"'{subject}'" : "NULL")
        + ", now())";

    private static AuditEntry Entry(Guid? subjectUserId) => new(
        ActorUserId: subjectUserId,
        ActorType: ActorType.User,
        SubjectUserId: subjectUserId,
        Action: "account.mfa_enabled",
        Category: Category.AccountSecurity,
        Visibility: Visibility.User,
        EntityType: null,
        EntityId: null,
        DataJson: "{}",
        Ip: null,
        UserAgent: null,
        ActorLabel: null,
        OccurredAt: Start);
}
