using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Publishing;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Npgsql;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// The <c>_06_PlatformQuotaUsage</c> migration (06.3.1-T1): the <c>platform_quota_usage</c> counter table, one row per
/// platform and Pacific quota day. The composite primary key <c>(platform, quota_day)</c> is its only index, and the
/// three CHECKs reject a negative count, a non-positive limit and an unknown platform. Later migrations appended
/// after this one (02.7.2-T2, 06.1.2-T1), so this class no longer owns the last-migration check (the newest
/// migration's own test does); it finds its own migration by name, because its timestamp is generated when the
/// migration is added.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class AddPlatformQuotaUsageMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string MigrationName = "06_PlatformQuotaUsage";

    /// <summary>The columns in <c>name|type|length|nullable</c> form. There is no <c>id</c> and no row version.</summary>
    private static readonly string[] ExpectedColumnSpecs =
    [
        "created_at|timestamp with time zone||NO",
        "platform|text||NO",
        "quota_day|date||NO",
        "units_limit|integer||NO",
        "units_used|integer||NO",
        "updated_at|timestamp with time zone||NO",
    ];

    private static readonly string[] ExpectedCheckConstraints =
    [
        "ck_platform_quota_usage_platform",
        "ck_platform_quota_usage_units_limit",
        "ck_platform_quota_usage_units_used",
    ];

    private static readonly string[] ExpectedIndexes = ["pk_platform_quota_usage"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_IsPresent_AndModelHasNoPendingChange()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        // 02.7.2-T2 appended a migration after this one, so this class no longer owns the last-migration
        // check (the newest migration's own test does); it still proves its own migration is compiled in.
        migrations.ShouldContain(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "an entity change without a migration must fail here (run `dotnet ef migrations add`)");
        context.Model.FindEntityType(typeof(PlatformQuotaUsage))!.GetTableName().ShouldBe("platform_quota_usage");
    }

    [Fact]
    public void Applies_WithTheColumnsKeyChecksAndNoOtherIndex()
    {
        SqlProbe.QueryStrings(
                Database,
                "SELECT column_name || '|' || data_type || '|' || coalesce(character_maximum_length::text, '') "
                + "|| '|' || is_nullable FROM information_schema.columns WHERE table_schema = 'public' "
                + "AND table_name = 'platform_quota_usage' ORDER BY column_name")
            .ShouldBe(ExpectedColumnSpecs);

        SqlProbe.QueryStrings(
                Database,
                "SELECT conname || '|' || pg_get_constraintdef(oid) FROM pg_constraint "
                + "WHERE conrelid = 'platform_quota_usage'::regclass AND contype = 'p'")
            .ShouldBe(["pk_platform_quota_usage|PRIMARY KEY (platform, quota_day)"]);

        SqlProbe.QueryStrings(
                Database,
                "SELECT conname FROM pg_constraint WHERE conrelid = 'platform_quota_usage'::regclass "
                + "AND contype = 'c' ORDER BY conname")
            .ShouldBe(ExpectedCheckConstraints);

        SqlProbe.QueryStrings(
                Database,
                "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' "
                + "AND tablename = 'platform_quota_usage' ORDER BY indexname")
            .ShouldBe(ExpectedIndexes);
    }

    [Fact]
    public void QuotaLedger_Schema_RejectsNegativeUnitsAndNonPositiveLimit()
    {
        var negativeUnits = Should.Throw<PostgresException>(
            () => SqlProbe.Execute(Database, InsertSql("youtube", "DATE '2026-10-15'", -1, 10000)));
        negativeUnits.SqlState.ShouldBe("23514");
        negativeUnits.ConstraintName.ShouldBe("ck_platform_quota_usage_units_used");

        var zeroLimit = Should.Throw<PostgresException>(
            () => SqlProbe.Execute(Database, InsertSql("youtube", "DATE '2026-10-16'", 0, 0)));
        zeroLimit.SqlState.ShouldBe("23514");
        zeroLimit.ConstraintName.ShouldBe("ck_platform_quota_usage_units_limit");

        var unknownPlatform = Should.Throw<PostgresException>(
            () => SqlProbe.Execute(Database, InsertSql("other", "DATE '2026-10-17'", 0, 10000)));
        unknownPlatform.SqlState.ShouldBe("23514");
        unknownPlatform.ConstraintName.ShouldBe("ck_platform_quota_usage_platform");

        SqlProbe.Execute(Database, InsertSql("youtube", "DATE '2026-10-18'", 0, 10000));
        SqlProbe.Count(Database, "SELECT count(*) FROM platform_quota_usage").ShouldBe(1);
    }

    [Fact]
    public async Task Migration_HasOnlyCreateOperations()
    {
        await using var context = Database.CreateAppDbContext();
        var migration = CompiledMigration(context, MigrationName);

        MigrationGuard.FindViolations([migration]).ShouldBeEmpty();
    }

    [Fact]
    public async Task Down_DropsOnlyThePlatformQuotaTable_AndReApplies()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        var own = migrations.FindIndex(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        own.ShouldBeGreaterThan(0, "the migration must exist and have a previous one to revert to");
        var migrator = context.Database.GetService<IMigrator>();

        try
        {
            await migrator.MigrateAsync(migrations[own - 1], Ct);
            SqlProbe.ScalarText(Database, "SELECT coalesce(to_regclass('public.platform_quota_usage')::text, 'gone')")
                .ShouldBe("gone");
            SqlProbe.ScalarText(Database, "SELECT coalesce(to_regclass('public.render_jobs')::text, 'gone')")
                .ShouldNotBe("gone", "the Down of this migration must not touch the previous tables");
        }
        finally
        {
            await migrator.MigrateAsync(migrations[^1], Ct);
        }

        SqlProbe.ScalarText(Database, "SELECT coalesce(to_regclass('public.platform_quota_usage')::text, 'gone')")
            .ShouldNotBe("gone");
        SqlProbe.Count(Database, "SELECT count(*) FROM platform_quota_usage").ShouldBe(0, "the recreated table is empty");
    }

    private static string InsertSql(string platform, string day, int unitsUsed, int unitsLimit) =>
        "INSERT INTO platform_quota_usage (platform, quota_day, units_used, units_limit, created_at, updated_at) "
        + $"VALUES ('{platform}', {day}, {unitsUsed}, {unitsLimit}, now(), now())";

    private static Migration CompiledMigration(AppDbContext context, string idFragment)
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        var type = assembly.Migrations
            .Single(pair => pair.Key.Contains(idFragment, StringComparison.Ordinal))
            .Value;

        return (Migration)Activator.CreateInstance(type, nonPublic: true)!;
    }
}
