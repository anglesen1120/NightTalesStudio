using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Entities;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// The <c>_09_AuthChallenges</c> migration (09.3.2-T3 Tests table): the table reaches a fresh database
/// with the agreed columns, the unique <c>token_hash</c> index, the <c>(user_id, expires_at)</c> index,
/// the partial <c>(user_id) WHERE used_at IS NULL</c> index the supersede statement needs (PERF-1), the
/// <c>attempts</c> CHECK that refuses a sixth attempt, and a <c>ON DELETE CASCADE</c> foreign key
/// to <c>users</c> (BD4, D9).
/// </summary>
/// <remarks>
/// The shape is read from both sides, like the other per-table migration tests: the live schema the
/// migration applied to the class database, and the migration's own operation tree — which is what
/// proves "one table and its three indexes, nothing else" (NFR-09-6, expand-only).
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class AuthChallengesMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task Applies_WithTheAgreedShape()
    {
        var ct = TestContext.Current.CancellationToken;

        SqlProbe.QueryStrings(
                Database,
                "SELECT table_name FROM information_schema.tables "
                + "WHERE table_schema = 'public' AND table_name = 'auth_challenges'")
            .ShouldHaveSingleItem();

        SqlProbe.QueryStrings(
                Database,
                "SELECT column_name || ':' || data_type || ':' || is_nullable FROM information_schema.columns "
                + "WHERE table_name = 'auth_challenges' ORDER BY ordinal_position")
            .ShouldBe(
            [
                "id:uuid:NO",
                "user_id:uuid:NO",
                "token_hash:text:NO",
                "preauth_cookie_hash:text:NO",
                "user_agent:text:YES",
                "ip:inet:YES",
                "attempts:smallint:NO",
                "expires_at:timestamp with time zone:NO",
                "used_at:timestamp with time zone:YES",
            ]);

        // The attempt counter's database default is the store's zero (BD4: a fresh challenge has none).
        SqlProbe.ScalarText(
            Database,
            "SELECT column_default FROM information_schema.columns "
            + "WHERE table_name = 'auth_challenges' AND column_name = 'attempts'")
            .ShouldBe("0");

        // The unique token_hash is how the second step resolves exactly one row (BD4), and
        // (user_id, expires_at) serves the supersede statement of BeginAsync.
        SqlProbe.QueryStrings(
                Database,
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
                + "AND tablename = 'auth_challenges' AND indexname = 'ux_auth_challenges_token_hash'")
            .ShouldHaveSingleItem().ShouldContain("UNIQUE INDEX");
        SqlProbe.QueryStrings(
                Database,
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
                + "AND tablename = 'auth_challenges' AND indexname = 'ix_auth_challenges_user_id_expires_at'")
            .ShouldHaveSingleItem().ShouldContain("(user_id, expires_at)");

        // PERF-1: the supersede statement looks only at the user's unused rows, so that index carries the
        // predicate; the composite index above is kept for the account's cascade delete.
        var unused = SqlProbe.QueryStrings(
                Database,
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
                + "AND tablename = 'auth_challenges' AND indexname = 'ix_auth_challenges_user_id_unused'")
            .ShouldHaveSingleItem();
        unused.ShouldContain("(user_id)");
        unused.ShouldContain("used_at IS NULL");
        unused.ShouldNotContain("UNIQUE");

        // BD4: the fifth wrong code is the last one; the column itself refuses a sixth.
        var check = SqlProbe.QueryStrings(
            Database,
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_auth_challenges_attempts'");
        check.ShouldHaveSingleItem().ShouldContain("attempts >= 0");
        check[0].ShouldContain("attempts <= 5");

        // D9: deleting an account takes its outstanding challenges with it.
        SqlProbe.QueryStrings(
                Database,
                "SELECT pg_get_constraintdef(oid) FROM pg_constraint "
                + "WHERE contype = 'f' AND conrelid = 'auth_challenges'::regclass")
            .ShouldHaveSingleItem().ShouldBe("FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE");
    }

    [Fact]
    public async Task Model_MatchesTheMigration()
    {
        await using var context = Database.CreateAppDbContext();

        var entity = context.Model.FindEntityType(typeof(AuthChallenge));
        entity.ShouldNotBeNull("the model must map the auth_challenges row (BD1)");
        entity!.GetTableName().ShouldBe("auth_challenges");

        entity.GetProperties()
            .Select(property => property.GetColumnName())
            .Order(StringComparer.Ordinal)
            .ShouldBe(
            [
                "attempts",
                "expires_at",
                "id",
                "ip",
                "preauth_cookie_hash",
                "token_hash",
                "used_at",
                "user_agent",
                "user_id",
            ]);

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "an entity change without a migration must fail here (run `dotnet ef migrations add`)");
    }

    [Fact]
    public async Task CheckConstraint_RejectsAnAttemptOutsideTheRange()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);

        // The ceiling itself is storable: the fifth wrong code sets attempts = 5, which the CHECK allows.
        SqlProbe.Execute(Database, Insert(user.Id, "hash-at-ceiling", attempts: 5));

        // The sixth attempt — and anything below zero — cannot be persisted at all.
        SqlProbe.SqlState(Database, Insert(user.Id, "hash-sixth", attempts: 6)).ShouldBe("23514");
        SqlProbe.SqlState(Database, Insert(user.Id, "hash-negative", attempts: -1)).ShouldBe("23514");

        SqlProbe.Count(Database, $"SELECT count(*) FROM auth_challenges WHERE user_id = '{user.Id}'").ShouldBe(1);
    }

    [Fact]
    public async Task TokenHash_IsUnique()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);

        SqlProbe.Execute(Database, Insert(user.Id, "duplicate-token-hash"));

        // Two rows with the same hash would make the second step resolve the wrong challenge (BD4).
        SqlProbe.SqlState(Database, Insert(user.Id, "duplicate-token-hash")).ShouldBe("23505");

        // Control: a different hash inserts, so the refusal above is the unique index and not the FK.
        SqlProbe.Execute(Database, Insert(user.Id, "another-token-hash"));
        SqlProbe.Count(Database, $"SELECT count(*) FROM auth_challenges WHERE user_id = '{user.Id}'").ShouldBe(2);
    }

    [Fact]
    public async Task DeleteUser_CascadesToChallenges()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = new UserBuilder(Database, new TestCurrentUser());
        var withChallenge = await builder.CreateAsync(ct: ct);
        var withoutChallenge = await builder.CreateAsync(ct: ct);

        SqlProbe.Execute(Database, Insert(withChallenge.Id, "cascade-token-hash"));
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM auth_challenges WHERE user_id = '{withChallenge.Id}'")
            .ShouldBe(1);

        // Control: the delete itself is allowed while nothing references the user.
        SqlProbe.Execute(Database, $"DELETE FROM users WHERE id = '{withoutChallenge.Id}'");
        SqlProbe.Count(Database, $"SELECT count(*) FROM users WHERE id = '{withoutChallenge.Id}'").ShouldBe(0);

        // ON DELETE CASCADE: the challenge cannot outlive its account.
        SqlProbe.Execute(Database, $"DELETE FROM users WHERE id = '{withChallenge.Id}'");
        SqlProbe.Count(Database, $"SELECT count(*) FROM users WHERE id = '{withChallenge.Id}'").ShouldBe(0);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM auth_challenges WHERE user_id = '{withChallenge.Id}'")
            .ShouldBe(0);
    }

    [Fact]
    public async Task No_unrelated_schema_change()
    {
        await using var context = Database.CreateAppDbContext();

        var migration = CompiledMigration(context, "09_AuthChallenges");
        var operations = migration.UpOperations;

        operations.Count.ShouldBe(4, "the migration is exactly one table and its three indexes");

        var table = operations.OfType<CreateTableOperation>().ShouldHaveSingleItem();
        table.Name.ShouldBe("auth_challenges");
        table.Columns.Select(column => column.Name).ShouldBe(
        [
            "id",
            "user_id",
            "token_hash",
            "preauth_cookie_hash",
            "user_agent",
            "ip",
            "attempts",
            "expires_at",
            "used_at",
        ]);
        table.PrimaryKey.ShouldNotBeNull("id is the primary key of the spec's SQL");
        table.PrimaryKey!.Columns.ShouldBe(["id"]);

        var check = table.CheckConstraints.ShouldHaveSingleItem();
        check.Name.ShouldBe("ck_auth_challenges_attempts");

        var foreignKey = table.ForeignKeys.ShouldHaveSingleItem();
        foreignKey.Columns.ShouldBe(["user_id"]);
        foreignKey.PrincipalTable.ShouldBe("users");
        foreignKey.OnDelete.ShouldBe(ReferentialAction.Cascade);

        var indexes = operations.OfType<CreateIndexOperation>().ToList();
        indexes.Count.ShouldBe(3);

        var unique = indexes.Single(index => index.Name == "ux_auth_challenges_token_hash");
        unique.Table.ShouldBe("auth_challenges");
        unique.Columns.ShouldBe(["token_hash"]);
        unique.IsUnique.ShouldBeTrue();

        var byUser = indexes.Single(index => index.Name == "ix_auth_challenges_user_id_expires_at");
        byUser.Table.ShouldBe("auth_challenges");
        byUser.Columns.ShouldBe(["user_id", "expires_at"]);
        byUser.IsUnique.ShouldBeFalse();

        // PERF-1: the supersede statement's index is partial — it holds only the account's unused rows.
        var unused = indexes.Single(index => index.Name == "ix_auth_challenges_user_id_unused");
        unused.Table.ShouldBe("auth_challenges");
        unused.Columns.ShouldBe(["user_id"]);
        unused.IsUnique.ShouldBeFalse();
        unused.Filter.ShouldBe("used_at IS NULL");

        // The same scan the migration job runs: nothing here drops or narrows anything.
        MigrationGuard.FindViolations([migration]).ShouldBeEmpty();
    }

    private static string Insert(Guid userId, string tokenHash, int attempts = 0) =>
        "INSERT INTO auth_challenges (id, user_id, token_hash, preauth_cookie_hash, user_agent, ip, attempts, "
        + "expires_at, used_at) VALUES (gen_random_uuid(), "
        + $"'{userId}', '{tokenHash}', 'seed-preauth-cookie-hash', 'seed-agent', '203.0.113.9', {attempts}, "
        + "now() + interval '5 minutes', NULL)";

    private static Migration CompiledMigration(AppDbContext context, string idFragment)
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        var type = assembly.Migrations
            .Single(pair => pair.Key.Contains(idFragment, StringComparison.Ordinal))
            .Value;

        return (Migration)Activator.CreateInstance(type, nonPublic: true)!;
    }
}
