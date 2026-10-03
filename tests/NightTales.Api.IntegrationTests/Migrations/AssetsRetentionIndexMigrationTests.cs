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
/// The <c>_09_AssetsRetentionIndex</c> migration (09.7.3-T1, index only): the partial due-assets index reaches a
/// fresh database, the audit keyset index of 09.3.6-T1 is in place for the sweep's scan, the model has no pending
/// change, and the <c>Down</c> drops the new index. This class owns the last-migration check
/// (<c>migrations[^1]</c>); older migration tests find their own migration by name.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class AssetsRetentionIndexMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string MigrationName = "09_AssetsRetentionIndex";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RetentionSweep_Migration_CreatesPartialIndex()
    {
        var due = (await Database.QueryStringsAsync(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
            + "AND tablename = 'assets' AND indexname = 'ix_assets_retention_due'",
            Ct)).ShouldHaveSingleItem();
        due.ShouldContain("(retention_until)");
        due.ShouldContain("deleted_at IS NULL");
        due.ShouldContain("retention_until IS NOT NULL");

        (await Database.QueryStringsAsync(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
            + "AND tablename = 'audit_logs' AND indexname = 'ix_audit_logs_created_id'",
            Ct)).ShouldHaveSingleItem().ShouldContain("(created_at, id)");
    }

    [Fact]
    public async Task Migration_IsTheLastOne_AndModelHasNoPendingChange()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        migrations[^1].ShouldEndWith("_" + MigrationName, customMessage: "this test owns the last migration");

        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    [Fact]
    public async Task DownMigration_DropsTheIndex_AndUpRecreatesIt()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        migrations[^1].ShouldEndWith("_" + MigrationName, customMessage: "this test owns the last migration");
        migrations.Count.ShouldBeGreaterThan(1);

        const string Probe = "SELECT count(*)::text FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'ix_assets_retention_due'";
        try
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(migrations[^2], Ct);

            (await Database.QueryStringsAsync(Probe, Ct)).ShouldBe(["0"], ignoreOrder: false);

            await context.Database.GetService<IMigrator>().MigrateAsync(migrations[^1], Ct);

            (await Database.QueryStringsAsync(Probe, Ct)).ShouldBe(["1"], ignoreOrder: false);
        }
        finally
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(migrations[^1], Ct);
        }
    }
}
