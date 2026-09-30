using Microsoft.EntityFrameworkCore.Migrations;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Migrations;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// CI rejects a migration whose <c>Up</c> drops or narrows something without saying so (09.2.2-T2,
/// NFR-09-6). The scan reads the compiled migrations, so it needs no database and the unit job catches
/// a destructive step as early as a compile error would.
/// </summary>
/// <remarks>
/// The synthetic migrations are the executable form of the task's acceptance check "red for a scratch
/// migration that drops or narrows a column without <c>[ContractMigration]</c>": they are permanent, so
/// the negative path cannot rot away with a scratch branch.
/// </remarks>
[Trait("Category", "Migration")]
public sealed class MigrationSafetyTests
{
    [Fact]
    public void Migrations_ContainNoDestructiveOperations()
    {
        var violations = MigrationGuard.ScanAssembly(typeof(AppDbContext).Assembly);

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void MigrationGuard_FlagsDropAndNarrowing_WithoutTheContractAttribute()
    {
        var violations = MigrationGuard.FindViolations(
            [new DroppingMigration(), new NarrowingMigration(), new NotNullMigration()]);

        violations.Count.ShouldBe(3);
        violations[0].ShouldContain("DroppingMigration drops column series.story_preset");
        violations[1].ShouldContain("NarrowingMigration narrows column series.title (max length 120 -> 60)");
        violations[2].ShouldContain("NotNullMigration narrows column series.title (nullable -> NOT NULL)");
    }

    [Fact]
    public void MigrationGuard_FlagsBoundingAPreviouslyUnboundedColumn_WithoutTheContractAttribute()
    {
        // `text` -> `varchar(120)`: the before side has no maximum length to compare against, so the guard
        // has to read the absence as "unbounded" rather than as "nothing recorded, nothing to say". Rows
        // whose value is longer than 120 characters fit schema N-1 and fail this migration.
        var violations = MigrationGuard.FindViolations([new BoundingMigration()]);

        violations.Count.ShouldBe(1);
        violations[0].ShouldContain(
            "BoundingMigration narrows column story_versions.body (max length unbounded -> 120)");
    }

    [Fact]
    public void MigrationGuard_AcceptsADestructiveOperation_CarryingTheContractAttribute()
    {
        // Same operations, one attribute: the contract step of NFR-09-6 is the reviewed exception, and
        // the reason it carries is what the PR template's checklist asks for.
        MigrationGuard.FindViolations([new ContractDroppingMigration()]).ShouldBeEmpty();
        MigrationGuard.FindViolations([new ContractNarrowingMigration()]).ShouldBeEmpty();
        MigrationGuard.FindViolations([new ContractNotNullMigration()]).ShouldBeEmpty();
        MigrationGuard.FindViolations([new ContractBoundingMigration()]).ShouldBeEmpty();
    }

    /// <summary>A migration that drops a column — the shape the guard must reject.</summary>
    private sealed class DroppingMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.DropColumn(name: "story_preset", table: "series");
    }

    /// <summary>A migration that narrows a column's maximum length.</summary>
    private sealed class NarrowingMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AlterColumn<string>(
            name: "title",
            table: "series",
            maxLength: 60,
            nullable: true,
            oldMaxLength: 120,
            oldNullable: true);
    }

    /// <summary>A migration that makes a nullable column <c>NOT NULL</c>.</summary>
    private sealed class NotNullMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AlterColumn<string>(
            name: "title",
            table: "series",
            maxLength: 120,
            nullable: false,
            oldMaxLength: 120,
            oldNullable: true);
    }

    /// <summary>A migration that bounds a column that had no maximum length (<c>text</c> -> <c>varchar(120)</c>).</summary>
    private sealed class BoundingMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AlterColumn<string>(
            name: "body",
            table: "story_versions",
            maxLength: 120,
            nullable: true,
            oldMaxLength: null,
            oldNullable: true);
    }

    [ContractMigration("Test-only twin of DroppingMigration: the attribute is what the guard looks for.")]
    private sealed class ContractDroppingMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.DropColumn(name: "story_preset", table: "series");
    }

    [ContractMigration("Test-only twin of NarrowingMigration: the attribute is what the guard looks for.")]
    private sealed class ContractNarrowingMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AlterColumn<string>(
            name: "title",
            table: "series",
            maxLength: 60,
            nullable: true,
            oldMaxLength: 120,
            oldNullable: true);
    }

    [ContractMigration("Test-only twin of NotNullMigration: the attribute is what the guard looks for.")]
    private sealed class ContractNotNullMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AlterColumn<string>(
            name: "title",
            table: "series",
            maxLength: 120,
            nullable: false,
            oldMaxLength: 120,
            oldNullable: true);
    }

    [ContractMigration("Test-only twin of BoundingMigration: the attribute is what the guard looks for.")]
    private sealed class ContractBoundingMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AlterColumn<string>(
            name: "body",
            table: "story_versions",
            maxLength: 120,
            nullable: true,
            oldMaxLength: null,
            oldNullable: true);
    }
}
