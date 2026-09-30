using NightTales.Api.IntegrationTests.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// The <c>_09_UserConsents</c> migration (09.3.1-T1 Tests table): the table reaches a fresh database
/// with the agreed columns, one <c>ON DELETE RESTRICT</c> foreign key and a CHECK that refuses a
/// document outside <c>tos</c>/<c>privacy</c>.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class UserConsentsMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task Applies_AndCheckConstraintRejectsUnknownDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);

        SqlProbe.SqlState(Database, Insert(user.Id, "marketing")).ShouldBe("23514");

        SqlProbe.Execute(Database, Insert(user.Id, "tos"));
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE user_id = '{user.Id}'").ShouldBe(1);
    }

    [Fact]
    public async Task Applies_WithTheAgreedShape()
    {
        var ct = TestContext.Current.CancellationToken;

        var tables = await Database.QueryStringsAsync(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'user_consents'",
            ct);
        tables.ShouldHaveSingleItem();

        SqlProbe.QueryStrings(
                Database,
                "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'user_consents'")
            .ShouldContain("ix_user_consents_user_id_document");

        SqlProbe.QueryStrings(
                Database,
                "SELECT column_name || ':' || data_type || ':' || is_nullable FROM information_schema.columns "
                + "WHERE table_name = 'user_consents' ORDER BY ordinal_position")
            .ShouldBe(
            [
                "id:uuid:NO",
                "user_id:uuid:NO",
                "document:text:NO",
                "version:character varying:NO",
                "accepted_at:timestamp with time zone:NO",
                "ip:inet:YES",
            ]);

        var check = SqlProbe.QueryStrings(
            Database,
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_user_consents_document'");
        check.ShouldHaveSingleItem().ShouldContain("'tos'");
        check[0].ShouldContain("'privacy'");

        // Exactly one foreign key, and it restricts: a user with acceptances cannot be deleted silently
        // (13's deletion workflow owns that, 13.2.1-T1).
        SqlProbe.QueryStrings(
                Database,
                "SELECT pg_get_constraintdef(oid) FROM pg_constraint "
                + "WHERE contype = 'f' AND conrelid = 'user_consents'::regclass")
            .ShouldHaveSingleItem()
            .ShouldBe("FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE RESTRICT");
    }

    [Fact]
    public async Task Version_Over32Characters_IsRejectedByTheColumn()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);

        // 1–32 is the width the register validator and UserConsent.Create also enforce; 33 is the
        // database's own backstop (SQLSTATE 22001: value too long for character varying(32)).
        SqlProbe.SqlState(Database, Insert(user.Id, "tos", new string('v', 33))).ShouldBe("22001");

        SqlProbe.Execute(Database, Insert(user.Id, "tos", new string('v', 32)));
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE user_id = '{user.Id}'").ShouldBe(1);
    }

    [Fact]
    public async Task DeleteUser_IsRestrictedWhileAConsentExists()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = new UserBuilder(Database, new TestCurrentUser());
        var withConsent = await builder.CreateAsync(ct: ct);
        var withoutConsent = await builder.CreateAsync(ct: ct);

        SqlProbe.Execute(Database, Insert(withConsent.Id, "privacy"));

        // Control: the delete itself is allowed while nothing references the user, so the refusal below
        // is the consent row's FK and not some unrelated constraint.
        SqlProbe.Execute(Database, $"DELETE FROM users WHERE id = '{withoutConsent.Id}'");
        SqlProbe.Count(Database, $"SELECT count(*) FROM users WHERE id = '{withoutConsent.Id}'").ShouldBe(0);

        // ON DELETE RESTRICT: the acceptance history blocks a silent user deletion (13.2.1-T1 owns it).
        SqlProbe.SqlState(Database, $"DELETE FROM users WHERE id = '{withConsent.Id}'").ShouldBe("23503");
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE user_id = '{withConsent.Id}'").ShouldBe(1);
    }

    private static string Insert(Guid userId, string document, string version = "v1") =>
        "INSERT INTO user_consents (id, user_id, document, version, accepted_at, ip) "
        + $"VALUES (gen_random_uuid(), '{userId}', '{document}', '{version}', now(), NULL)";
}
