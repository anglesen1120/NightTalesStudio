using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Infrastructure.Persistence.Entities;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// The <c>_09_UserActionTokens</c> migration (09.3.3-T1): the table reaches a fresh database with the agreed
/// columns, the unique <c>token_hash</c> index, the <c>(user_id, kind)</c> index, the <c>kind</c> CHECK and a
/// <c>ON DELETE CASCADE</c> foreign key to <c>users</c>; the model has no pending change; and the
/// <c>Down</c> drops the table and the <c>Up</c> re-creates it empty.
/// </summary>
/// <remarks>
/// The shape is read from both sides, like the other per-table migration tests: the EF model (what the next
/// <c>dotnet ef migrations add</c> would diff against) and the live schema of the class database (what the
/// migration applied). This class owns the last-migration <c>Down</c> check while
/// <c>_09_UserActionTokens</c> is the newest migration; when a newer migration lands, its own test takes
/// that check over and this one reverts to its own predecessor by name (see
/// <c>WorkerNodesMigrationTests.DownMigration_DropsTable</c>).
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class UserActionTokensMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string MigrationName = "09_UserActionTokens";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Applies_WithTheAgreedShape()
    {
        (await Database.QueryStringsAsync(
            "SELECT column_name || ':' || data_type || ':' || is_nullable FROM information_schema.columns "
            + "WHERE table_schema = 'public' AND table_name = 'user_action_tokens' ORDER BY ordinal_position",
            Ct))
            .ShouldBe(
            [
                "id:uuid:NO",
                "user_id:uuid:NO",
                "kind:text:NO",
                "subject_id:uuid:YES",
                "token_hash:text:NO",
                "created_at:timestamp with time zone:NO",
                "expires_at:timestamp with time zone:NO",
                "used_at:timestamp with time zone:YES",
            ],
            ignoreOrder: false);

        // The unique hash is how the consume resolves exactly one row; (user_id, kind) serves the
        // invalidating issue of a user's older tokens.
        (await Database.QueryStringsAsync(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
            + "AND tablename = 'user_action_tokens' AND indexname = 'ux_user_action_tokens_token_hash'",
            Ct))
            .ShouldHaveSingleItem().ShouldContain("UNIQUE INDEX");
        (await Database.QueryStringsAsync(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
            + "AND tablename = 'user_action_tokens' AND indexname = 'ix_user_action_tokens_user_id_kind'",
            Ct))
            .ShouldHaveSingleItem().ShouldContain("(user_id, kind)");

        // Deleting an account takes its outstanding tokens with it.
        (await Database.QueryStringsAsync(
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint "
            + "WHERE contype = 'f' AND conrelid = 'user_action_tokens'::regclass",
            Ct))
            .ShouldHaveSingleItem().ShouldBe("FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE");
    }

    [Fact]
    public async Task CheckConstraint_RefusesAnUnknownKind()
    {
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);

        SqlProbe.Execute(Database, InsertToken(user.Id, "'password_reset'", "'hash-known-kind'"));
        SqlProbe.SqlState(Database, InsertToken(user.Id, "'login_link'", "'hash-unknown-kind'"))
            .ShouldBe("23514", "an unknown kind violates ck_user_action_tokens_kind");
    }

    [Fact]
    public async Task UniqueIndex_RefusesADuplicateTokenHash()
    {
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);

        SqlProbe.Execute(Database, InsertToken(user.Id, "'password_reset'", "'hash-duplicate'"));
        SqlProbe.SqlState(Database, InsertToken(user.Id, "'email_verify'", "'hash-duplicate'"))
            .ShouldBe("23505", "ux_user_action_tokens_token_hash is unique across kinds");
    }

    [Fact]
    public async Task Model_MatchesTheMigration()
    {
        await using var context = Database.CreateAppDbContext();

        var entity = context.Model.FindEntityType(typeof(UserActionToken));
        entity.ShouldNotBeNull("the model must map the user_action_tokens row (BD3)");
        entity!.GetTableName().ShouldBe("user_action_tokens");

        entity.GetProperties()
            .Select(property => property.GetColumnName())
            .Order(StringComparer.Ordinal)
            .ShouldBe(
            [
                "created_at",
                "expires_at",
                "id",
                "kind",
                "subject_id",
                "token_hash",
                "used_at",
                "user_id",
            ]);

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "an entity change without a migration must fail here (run `dotnet ef migrations add`)");
    }

    [Fact]
    public async Task DownMigration_DropsTable()
    {
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);
        SqlProbe.Execute(Database, InsertToken(user.Id, "'password_reset'", "'hash-down-test'"));

        await using var context = Database.CreateAppDbContext();

        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        migrations[^1].ShouldContain(MigrationName, customMessage: "this test owns the last migration");
        migrations.Count.ShouldBeGreaterThan(1, "there must be a previous migration to revert to");

        try
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(migrations[^2], Ct);

            (await Database.QueryStringsAsync("SELECT to_regclass('public.user_action_tokens')::text", Ct))
                .ShouldBe([string.Empty], ignoreOrder: false, customMessage: "the Down drops the table");
        }
        finally
        {
            // Always come back to the latest migration, so a failed assertion does not leave the class
            // database on the previous schema for its sibling tests.
            await context.Database.MigrateAsync(CancellationToken.None);
        }

        (await Database.QueryStringsAsync("SELECT to_regclass('public.user_action_tokens')::text", Ct))
            .ShouldHaveSingleItem().ShouldBe("user_action_tokens", "the Up re-creates the table");
        SqlProbe.Count(Database, "SELECT count(*) FROM user_action_tokens")
            .ShouldBe(0, "the re-created table is empty: the Down dropped the seeded row with it");
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    private static string InsertToken(Guid userId, string kind, string tokenHash) =>
        "INSERT INTO user_action_tokens (id, user_id, kind, token_hash, created_at, expires_at) "
        + $"VALUES (gen_random_uuid(), '{userId}', {kind}, {tokenHash}, now(), now() + interval '1 hour')";
}
