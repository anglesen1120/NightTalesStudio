using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using NightTales.Application.Abstractions;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.UnitTests.TestKit;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Persistence;

/// <summary>
/// The 02.5.3-T2 model contract of <c>fact_sheets</c> (plan §3), read from the EF model alone: no database
/// and no migration is needed, so this stays green while the orchestrator generates <c>AddFactSheets</c>.
/// It covers what the live-schema tests of the integration job cover for the other tables: the two jsonb
/// documents, the partial unique approved index, the CHECKs and the three deferred 02 CR-29 fact-sheet
/// foreign keys, plus PF-1's descending index.
/// </summary>
public sealed class FactSheetModelTests
{
    private const string ApprovedIndex = "ux_fact_sheets_episode_approved";
    private const string VersionIndex = "ux_fact_sheets_episode_version";

    /// <summary>PF-1's descending direction per key: <c>created_at DESC, id DESC</c> (02.3.4-T2 hand-over).</summary>
    private static readonly bool[] EpisodeCreatedDescending = [false, true, true];

    [Fact]
    public void FactSheet_IsTheFactSheetsTable_WithTheAlternateKeyTheFactSheetForeignKeysTarget()
    {
        var entity = FactSheetEntity();

        entity.GetTableName().ShouldBe("fact_sheets");
        entity.FindPrimaryKey()!.Properties.Select(property => property.Name).ShouldBe(["Id"]);

        // The composite (id, user_id) key of 02 CR-29: without it the three SET NULL foreign keys below
        // cannot be declared, and the same-owner rule of 09 W3 CR-46 has no target.
        entity.GetKeys().Where(key => !key.IsPrimaryKey())
            .Select(key => string.Join(',', key.Properties.Select(property => property.Name)))
            .ShouldBe(["Id,UserId"]);
    }

    [Fact]
    public void FactSheet_IsTenantFiltered()
    {
        // The filter itself is asserted for every user-owned entity by TenantFilterTests; this only pins
        // that the new table joined that set.
        FactSheetEntity().GetDeclaredQueryFilters()
            .ShouldContain(filter => filter.Key == "tenant");
    }

    [Fact]
    public void FactSheet_DocumentsAreCanonicalJsonScalars()
    {
        var facts = Property(nameof(FactSheet.Facts));
        facts.ClrType.ShouldBe(typeof(IReadOnlyList<FactItem>));
        facts.GetColumnType().ShouldBe("jsonb");
        facts.IsNullable.ShouldBeFalse();
        // The conversion is what keeps the stored bytes equal to FactSheetJson's canonical document, which
        // is the string content_sha256 hashes (plan §3 invariant 3). The property's provider type is the
        // jsonb type mapping's, so the converter is the one that carries the string provider type.
        facts.GetValueConverter().ShouldNotBeNull();
        facts.GetValueConverter()!.ProviderClrType.ShouldBe(typeof(string));

        var aliases = Property(nameof(FactSheet.SourceAliases));
        aliases.ClrType.ShouldBe(typeof(IReadOnlyDictionary<string, Guid>));
        aliases.GetColumnType().ShouldBe("jsonb");
        aliases.IsNullable.ShouldBeFalse();
        aliases.GetValueConverter().ShouldNotBeNull();
        aliases.GetValueConverter()!.ProviderClrType.ShouldBe(typeof(string));
    }

    [Fact]
    public void FactSheet_StatusIsSmallintWithTheClosedSetCheck()
    {
        var status = Property("Status");

        status.ClrType.ShouldBe(typeof(FactSheetStatus));
        status.GetColumnType().ShouldBe("smallint");
        // EF Core 10 folds HasConversion<short>() into the type mapping, so the enum→short storage is the
        // property's provider type, not a separate value converter.
        status.GetProviderClrType().ShouldBe(typeof(short));

        FactSheetDesignEntity().GetCheckConstraints()
            .ShouldContain(constraint => constraint.Name == "ck_fact_sheets_status"
                && constraint.Sql.Contains("IN (0, 1, 2)", StringComparison.Ordinal));
        FactSheetDesignEntity().GetCheckConstraints()
            .ShouldContain(constraint => constraint.Name == "ck_fact_sheets_sha256"
                && constraint.Sql.Contains("^[0-9a-f]{64}$", StringComparison.Ordinal));
    }

    [Fact]
    public void FactSheet_ApprovedIndexIsPartialAndUniquePerEpisode()
    {
        var approved = FactSheetEntity().GetIndexes().Single(index => index.GetDatabaseName() == ApprovedIndex);

        approved.IsUnique.ShouldBeTrue();
        approved.Properties.Select(property => property.Name).ShouldBe(["EpisodeId"]);
        // Plan §3 invariant 4 / T54: at most one approved version per episode, so the approve path can
        // demote the previous one in the same transaction without a race.
        approved.GetFilter().ShouldBe("\"status\" = 1");

        var versioned = FactSheetEntity().GetIndexes().Single(index => index.GetDatabaseName() == VersionIndex);
        versioned.IsUnique.ShouldBeTrue();
        versioned.Properties.Select(property => property.Name).ShouldBe(["EpisodeId", "Version"]);
        versioned.GetFilter().ShouldBeNull();
    }

    [Fact]
    public void FactSheet_EpisodeForeignKeyCascades_AndTheRunForeignKeyIsNullable()
    {
        var entity = FactSheetEntity();

        var episode = entity.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(Episode));
        episode.Properties.Select(property => property.Name).ShouldBe(["EpisodeId", "UserId"]);
        episode.PrincipalKey.Properties.Select(property => property.Name).ShouldBe(["Id", "UserId"]);
        episode.DeleteBehavior.ShouldBe(DeleteBehavior.Cascade);

        var run = entity.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(GenerationRun));
        run.Properties.Select(property => property.Name).ShouldBe(["GenerationRunId", "UserId"]);
        run.PrincipalKey.Properties.Select(property => property.Name).ShouldBe(["Id", "UserId"]);
        // A hand-edited version of 02.5.4-T1 carries no run, and deleting a run must not delete the draft.
        run.DeleteBehavior.ShouldBe(DeleteBehavior.SetNull);
    }

    [Fact]
    public void FactSheetForeignKeys_OfGenerationRunsAndStoryVersions_AreSameOwnerSetNull()
    {
        var sheetKey = new[] { "Id", "UserId" };

        var input = FactSheetForeignKey(typeof(GenerationRun), nameof(GenerationRun.InputFactSheetId));
        input.Properties.Select(property => property.Name).ShouldBe(["InputFactSheetId", "UserId"]);
        input.PrincipalKey.Properties.Select(property => property.Name).ShouldBe(sheetKey);
        input.DeleteBehavior.ShouldBe(DeleteBehavior.SetNull);

        var result = FactSheetForeignKey(typeof(GenerationRun), nameof(GenerationRun.ResultFactSheetId));
        result.Properties.Select(property => property.Name).ShouldBe(["ResultFactSheetId", "UserId"]);
        result.PrincipalKey.Properties.Select(property => property.Name).ShouldBe(sheetKey);
        result.DeleteBehavior.ShouldBe(DeleteBehavior.SetNull);

        var version = FactSheetForeignKey(typeof(StoryVersion), nameof(StoryVersion.FactSheetId));
        version.Properties.Select(property => property.Name).ShouldBe(["FactSheetId", "UserId"]);
        version.PrincipalKey.Properties.Select(property => property.Name).ShouldBe(sheetKey);
        version.DeleteBehavior.ShouldBe(DeleteBehavior.SetNull);
    }

    [Fact]
    public void GenerationRuns_Pf1Index_IsEpisodeCreatedDescending()
    {
        // Sort directions live in the design-time model only: the runtime (read-optimized) model drops them.
        var run = DesignModel().FindEntityType(typeof(GenerationRun))!;
        var pf1 = run.GetIndexes().Single(index => index.GetDatabaseName() == "ix_generation_runs_episode_created");

        pf1.IsUnique.ShouldBeFalse();
        pf1.Properties.Select(property => property.Name).ShouldBe(["EpisodeId", "CreatedAt", "Id"]);
        // PF-1 (02.3.4-T2 hand-over): "the latest run of this episode" is ORDER BY created_at DESC, id DESC.
        pf1.IsDescending.ShouldNotBeNull().ShouldBe(EpisodeCreatedDescending);
    }

    private static IReadOnlyProperty Property(string name) =>
        FactSheetEntity().FindProperty(name) ?? throw new InvalidOperationException($"fact_sheets.{name} is not mapped.");

    private static IReadOnlyForeignKey FactSheetForeignKey(Type dependent, string propertyName)
    {
        var entity = Model().FindEntityType(dependent)!;
        return entity.GetForeignKeys().Single(fk => fk.Properties.Any(property => property.Name == propertyName));
    }

    private static IReadOnlyEntityType FactSheetEntity() => Model().FindEntityType(typeof(FactSheet))!;

    /// <summary>
    /// The fact-sheet entity from the design-time model: CHECK constraints are DDL, so the runtime
    /// (read-optimized) model drops them, exactly as it drops an index's sort directions.
    /// </summary>
    private static IReadOnlyEntityType FactSheetDesignEntity() => DesignModel().FindEntityType(typeof(FactSheet))!;

    /// <summary>
    /// The production write model: the same provider options <c>AddPersistence</c> uses minus the probe
    /// customizer and the interceptors, which change no mapping. Nothing here connects to a database.
    /// </summary>
    private static IModel Model()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ProbeContexts.UnusedConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        using var context = new AppDbContext(options, new AnonymousCurrentUser());
        return context.Model;
    }

    /// <summary>
    /// The same model with the configuration the runtime model drops, such as an index's sort directions.
    /// </summary>
    private static IModel DesignModel()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ProbeContexts.UnusedConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        using var context = new AppDbContext(options, new AnonymousCurrentUser());
        return context.GetService<IDesignTimeModel>().Model;
    }
}
