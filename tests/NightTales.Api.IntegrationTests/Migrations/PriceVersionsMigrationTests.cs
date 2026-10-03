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
/// columns, the descending <c>active_from</c> index and the non-unique <c>config_hash</c> lookup index; the model
/// has no pending change; the new <c>usage_records</c> CHECKs and foreign keys hold; and this class owns the
/// last-migration <c>Down</c> check (the newest migration's test does).
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
            .ShouldHaveSingleItem().ShouldNotContain("UNIQUE");
        (await Database.QueryStringsAsync(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
            + "AND tablename = 'price_versions' AND indexname = 'ix_price_versions_active_from'",
            Ct))
            .ShouldHaveSingleItem().ShouldContain("active_from DESC");
    }

    [Fact]
    public async Task ConfigHashIndex_IsNotUnique_SoARevertedConfigCanInsertAgain()
    {
        const string Insert =
            "INSERT INTO price_versions (version, active_from, rates, limits, config_hash, created_at) "
            + "VALUES ({0}, now(), '{{}}'::jsonb, '{{}}'::jsonb, 'duplicate-hash', now())";

        SqlProbe.Execute(Database, string.Format(System.Globalization.CultureInfo.InvariantCulture, Insert, 8001));
        SqlProbe.Execute(Database, string.Format(System.Globalization.CultureInfo.InvariantCulture, Insert, 8002));
        SqlProbe.Count(Database, "SELECT count(*) FROM price_versions WHERE config_hash = 'duplicate-hash'").ShouldBe(2);
    }

    [Theory]
    [InlineData("-0.01", 0, 0, 0)]
    [InlineData("0.5", -1, 0, 0)]
    [InlineData("0.5", 0, -1, 0)]
    [InlineData("0.5", 0, 0, -1)]
    public async Task UsageRecords_RefuseNegativeCostOrTokens(string cost, int input, int cached, int output)
    {
        var sql =
            "INSERT INTO usage_records (id, user_id, provider, operation, input_tokens, cached_input_tokens, output_tokens, "
            + "image_count, gpu_seconds, estimated, cost_usd, created_at) VALUES (gen_random_uuid(), gen_random_uuid(), 'openai', "
            + string.Create(System.Globalization.CultureInfo.InvariantCulture, $"'story', {input}, {cached}, {output}, 0, 0, false, {cost}, now())");

        SqlProbe.SqlState(Database, sql).ShouldBe("23514", "ck_usage_records_cost / ck_usage_records_tokens");
    }

    [Fact]
    public async Task UsageRecords_RunForeignKey_NullsOnlyTheRunColumn()
    {
        (await Database.QueryStringsAsync(
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint "
            + "WHERE conname = 'fk_usage_records_generation_runs_generation_run_id_user_id'",
            Ct))
            .ShouldHaveSingleItem()
            .ShouldContain("ON DELETE SET NULL (generation_run_id)");
        (await Database.QueryStringsAsync(
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'fk_usage_records_price_versions_price_version'",
            Ct))
            .ShouldHaveSingleItem()
            .ShouldContain("REFERENCES price_versions(version)");
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
