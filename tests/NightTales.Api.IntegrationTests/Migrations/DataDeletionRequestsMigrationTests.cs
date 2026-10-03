using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Compliance.Deletion;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// The <c>_13_DataDeletionRequests</c> migration (13.3.1-T1, 13 README D4/YR13): the table, its three CHECKs, its four
/// indexes — above all the open-request unique index with <c>user_id</c> first and <c>NULLS NOT DISTINCT</c>, verified
/// here on PostgreSQL 17 — and no foreign key to <c>users</c>. Another migration followed it
/// (<c>_09_AssetsRetentionIndex</c> owns the last-migration <c>Down</c> check), so this class finds its own
/// migration by name, like <c>PriceVersionsMigrationTests</c>.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class DataDeletionRequestsMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string MigrationName = "13_DataDeletionRequests";
    private static readonly Guid User = Guid.Parse("01920000-0000-7000-8000-0000000000a1");
    private static readonly Guid OtherUser = Guid.Parse("01920000-0000-7000-8000-0000000000a2");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Insert(Guid user, string scope, string? target, string reason, bool completed = false) => $"""
        INSERT INTO data_deletion_requests (id, user_id, scope, target_id, reason, status, requested_at, due_at, completed_at)
        VALUES ('{Guid.NewGuid()}', '{user}', '{scope}', {(target is null ? "NULL" : $"'{target}'")}, '{reason}',
                '{(completed ? "completed" : "pending")}', now(), now() + interval '7 days', {(completed ? "now()" : "NULL")})
        """;

    [Fact]
    public async Task Applies_WithTheAgreedShape()
    {
        (await Database.QueryStringsAsync(
            "SELECT column_name || ':' || data_type || ':' || is_nullable FROM information_schema.columns "
            + "WHERE table_schema = 'public' AND table_name = 'data_deletion_requests' ORDER BY ordinal_position",
            Ct))
            .ShouldBe(
            [
                "id:uuid:NO",
                "user_id:uuid:NO",
                "scope:text:NO",
                "target_id:uuid:YES",
                "reason:text:NO",
                "status:text:NO",
                "requested_at:timestamp with time zone:NO",
                "due_at:timestamp with time zone:NO",
                "claimed_at:timestamp with time zone:YES",
                "attempts:integer:NO",
                "last_error_code:text:YES",
                "last_error_at:timestamp with time zone:YES",
                "completed_at:timestamp with time zone:YES",
                "reapplied_at:timestamp with time zone:YES",
            ],
            ignoreOrder: false);

        (await Database.QueryStringsAsync(
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'data_deletion_requests' "
            + "AND indexname LIKE '%deletion_requests%' ORDER BY indexname",
            Ct))
            .ShouldBe(
            [
                "ix_deletion_requests_due",
                "ix_deletion_requests_reapply",
                "ix_deletion_requests_user",
                "pk_data_deletion_requests",
                "ux_deletion_requests_open",
            ]);

        var open = (await Database.QueryStringsAsync(
            "SELECT indexdef FROM pg_indexes WHERE indexname = 'ux_deletion_requests_open'", Ct)).ShouldHaveSingleItem();
        open.ShouldContain("UNIQUE");
        open.ShouldContain("NULLS NOT DISTINCT");
        open.ShouldContain("(user_id, scope, target_id, reason)");
        open.ShouldContain("completed_at IS NULL");
    }

    [Fact]
    public async Task NoForeignKeyToUsers_TheRowOutlivesTheAccount()
    {
        (await Database.QueryStringsAsync(
            "SELECT count(*)::text FROM pg_constraint WHERE conrelid = 'data_deletion_requests'::regclass AND contype = 'f'", Ct))
            .ShouldBe(["0"]);

        // A request for a user that has no users row inserts fine.
        SqlProbe.Execute(Database, Insert(Guid.NewGuid(), "account", null, "account_delete"));
    }

    [Fact]
    public void OpenUniqueIndex_RefusesTwoOpenAccountRowsOfOneUser_ButNotTwoUsers()
    {
        SqlProbe.Execute(Database, Insert(User, "account", null, "account_delete"));

        // NULLS NOT DISTINCT: the NULL target does not make the twin distinct.
        SqlProbe.SqlState(Database, Insert(User, "account", null, "account_delete")).ShouldBe("23505");

        // YR13: user_id is part of the key.
        SqlProbe.Execute(Database, Insert(OtherUser, "account", null, "account_delete"));
    }

    [Fact]
    public void OpenUniqueIndex_AllowsAnotherRowOnceTheFirstIsCompleted()
    {
        var target = Guid.NewGuid().ToString();
        SqlProbe.Execute(Database, Insert(User, "episode", target, "user_request", completed: true));
        SqlProbe.Execute(Database, Insert(User, "episode", target, "user_request"));
        SqlProbe.SqlState(Database, Insert(User, "episode", target, "user_request")).ShouldBe("23505");
    }

    [Theory]
    [InlineData("scope = 'tenant'", "ck_data_deletion_requests_scope")]
    [InlineData("reason = 'because'", "ck_data_deletion_requests_reason")]
    [InlineData("status = 'done'", "ck_data_deletion_requests_status")]
    public void CheckConstraints_ReportTheExactConstraintThatWasBroken(string set, string constraint)
    {
        var id = Guid.NewGuid();
        SqlProbe.Execute(Database, Insert(User, "channel", id.ToString(), "in_app_revoke"));

        var exception = Should.Throw<Npgsql.PostgresException>(
            () => SqlProbe.Execute(Database, $"UPDATE data_deletion_requests SET {set} WHERE target_id = '{id}'"));

        exception.SqlState.ShouldBe("23514");
        exception.ConstraintName.ShouldBe(constraint, set);
    }

    [Fact]
    public async Task Model_MatchesTheMigration()
    {
        await using var context = Database.CreateAppDbContext();
        var entity = context.Model.FindEntityType(typeof(DeletionRequest));
        entity.ShouldNotBeNull();
        entity!.GetTableName().ShouldBe("data_deletion_requests");
        entity.GetDeclaredQueryFilters().ShouldNotBeEmpty("the row is user-owned: the tenant filter applies");

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "an entity change without a migration must fail here (run `dotnet ef migrations add`)");
    }

    [Fact]
    public async Task DownMigration_DropsTable()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        var own = migrations.FindIndex(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        own.ShouldBeGreaterThan(0, "the migration must exist and have a previous one to revert to");

        try
        {
            // Reverting to the migration before this one also reverts every later migration.
            await context.Database.GetService<IMigrator>().MigrateAsync(migrations[own - 1], Ct);

            (await Database.QueryStringsAsync("SELECT coalesce(to_regclass('public.data_deletion_requests')::text, '')", Ct))
                .ShouldBe([string.Empty], ignoreOrder: false, customMessage: "the Down drops the table");

            await context.Database.MigrateAsync(Ct);
        }
        finally
        {
            await context.Database.MigrateAsync(CancellationToken.None);
        }

        (await Database.QueryStringsAsync("SELECT count(*)::text FROM data_deletion_requests", Ct))
            .ShouldBe(["0"], ignoreOrder: false, customMessage: "the re-created table is empty");
    }
}
