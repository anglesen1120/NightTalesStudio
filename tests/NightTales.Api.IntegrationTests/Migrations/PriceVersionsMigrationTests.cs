using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// The <c>_02_PriceVersions</c> migration (02.2.4-T1): the table reaches a fresh database with the agreed
/// columns, the descending <c>active_from</c> index and the unique <c>config_hash</c> index; the model has no
/// pending change; and this class owns the last-migration <c>Down</c> check (the newest migration's test does).
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class PriceVersionsMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Applies_WithTheAgreedShape()
    {
        (await Database.QueryStringsAsync(
            "SELECT column_name || ':' || data_type || ':' || is_nullable FROM information_schema.columns "
            + "WHERE table_schema = 'public' AND table_name = 'price_versions' ORDER BY ordinal_position",
            Ct))
            .ShouldBe(
            [
                "version:integer:NO",
                "active_from:timestamp with time zone:NO",
                "rates:jsonb:NO",
                "limits:jsonb:NO",
                "config_hash:text:NO",
                "created_at:timestamp with time zone:NO",
            ],
            ignoreOrder: false);

        (await Database.QueryStringsAsync(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
            + "AND tablename = 'price_versions' AND indexname = 'ix_price_versions_config_hash'",
            Ct))
            .ShouldHaveSingleItem().ShouldContain("UNIQUE INDEX");
        (await Database.QueryStringsAsync(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
            + "AND tablename = 'price_versions' AND indexname = 'ix_price_versions_active_from'",
            Ct))
            .ShouldHaveSingleItem().ShouldContain("active_from DESC");
    }

    [Fact]
    public async Task UniqueIndex_RefusesADuplicateConfigHash()
    {
        const string Insert =
            "INSERT INTO price_versions (version, active_from, rates, limits, config_hash, created_at) "
            + "VALUES ({0}, now(), '{{}}'::jsonb, '{{}}'::jsonb, 'duplicate-hash', now())";

        SqlProbe.Execute(Database, string.Format(System.Globalization.CultureInfo.InvariantCulture, Insert, 8001));
        SqlProbe.SqlState(Database, string.Format(System.Globalization.CultureInfo.InvariantCulture, Insert, 8002))
            .ShouldBe("23505", "a config hash maps to exactly one version");
    }

    [Fact]
    public async Task ModelHasNoPendingChange()
    {
        await using var context = Database.CreateAppDbContext();
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    /// <summary>
    /// Owns the last-migration <c>Down</c> check: reverting to the previous migration drops the table, and
    /// applying the latest again re-creates it empty.
    /// </summary>
    [Fact]
    public async Task DownMigration_DropsTable()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        migrations[^1].ShouldEndWith("_02_PriceVersions", customMessage: "this test owns the last migration");
        migrations.Count.ShouldBeGreaterThan(1);

        try
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(migrations[^2], Ct);

            SqlProbe.ScalarText(Database, "SELECT coalesce(to_regclass('public.price_versions')::text, 'gone')")
                .ShouldBe("gone");
        }
        finally
        {
            await context.Database.MigrateAsync(CancellationToken.None);
        }

        SqlProbe.Count(Database, "SELECT count(*) FROM price_versions").ShouldBe(0);
    }
}
