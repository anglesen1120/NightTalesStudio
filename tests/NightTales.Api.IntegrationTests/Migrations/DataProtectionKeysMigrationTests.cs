using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// The <c>_09_DataProtectionKeys</c> migration of 09.9.4-T1: the table the model builds is the table
/// the migration created, and the migration creates exactly that table and its unique index and
/// nothing else (an expand-only step, NFR-09-6).
/// </summary>
/// <remarks>
/// The shape is read from two sides on purpose: the EF model (what the next <c>migrations add</c> would
/// diff against) and the live schema of the class database (what <c>database update</c> actually
/// applied). The second test reads the migration's operation tree, which is what proves "no unrelated
/// change" without a database dump diff.
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class DataProtectionKeysMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task DataProtectionKeys_migration_matches_model()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = Database.CreateAppDbContext();

        var entity = context.Model.FindEntityType(typeof(DataProtectionKey));
        entity.ShouldNotBeNull("the model must map the framework's DataProtectionKey (W1)");
        entity!.GetTableName().ShouldBe("data_protection_keys");

        entity.GetProperties()
            .Select(property => property.GetColumnName())
            .Order(StringComparer.Ordinal)
            .ShouldBe(["friendly_name", "id", "xml"]);

        var columns = await Database.QueryStringsAsync(
            "SELECT column_name || ':' || data_type FROM information_schema.columns "
            + "WHERE table_schema = 'public' AND table_name = 'data_protection_keys' ORDER BY column_name",
            ct);
        columns.ShouldBe(["friendly_name:character varying", "id:integer", "xml:text"]);

        // The unique index is the ring's lookup key (friendly name), and it must be unique: two rows
        // with one name would make the key manager read a different key each time.
        var index = await Database.QueryStringsAsync(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
            + "AND tablename = 'data_protection_keys' AND indexname = 'ix_data_protection_keys_friendly_name'",
            ct);
        index.ShouldHaveSingleItem().ShouldContain("UNIQUE INDEX");

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "an entity change without a migration must fail here (run `dotnet ef migrations add`)");
    }

    [Fact]
    public async Task No_unrelated_schema_change()
    {
        await using var context = Database.CreateAppDbContext();

        var migration = CompiledMigration(context, "09_DataProtectionKeys");
        var operations = migration.UpOperations;

        operations.Count.ShouldBe(2, "the migration is exactly one table and its unique index");

        var table = operations.OfType<CreateTableOperation>().ShouldHaveSingleItem();
        table.Name.ShouldBe("data_protection_keys");
        table.Columns.Select(column => column.Name).ShouldBe(["id", "friendly_name", "xml"]);
        table.Columns.Single(column => column.Name == "friendly_name").MaxLength.ShouldBe(256);
        table.PrimaryKey.ShouldNotBeNull("id is the identity primary key of the spec's SQL");
        table.PrimaryKey!.Columns.ShouldBe(["id"]);

        var index = operations.OfType<CreateIndexOperation>().ShouldHaveSingleItem();
        index.Name.ShouldBe("ix_data_protection_keys_friendly_name");
        index.Table.ShouldBe("data_protection_keys");
        index.Columns.ShouldBe(["friendly_name"]);
        index.IsUnique.ShouldBeTrue();

        // The same scan the migration job runs: nothing here drops or narrows anything.
        MigrationGuard.FindViolations([migration]).ShouldBeEmpty();
    }

    private static Migration CompiledMigration(AppDbContext context, string idFragment)
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        var type = assembly.Migrations
            .Single(pair => pair.Key.Contains(idFragment, StringComparison.Ordinal))
            .Value;

        return (Migration)Activator.CreateInstance(type, nonPublic: true)!;
    }
}
