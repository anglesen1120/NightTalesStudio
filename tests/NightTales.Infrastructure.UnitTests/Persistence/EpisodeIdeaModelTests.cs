using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using NightTales.Application.Abstractions;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.UnitTests.TestKit;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Persistence;

/// <summary>
/// The 02.8.1-T1 model contract of <c>episode_ideas</c> (plan §3.1, CR-4), read from the EF model alone: no
/// database and no migration is needed, so this stays green while the orchestrator generates
/// <c>AddEpisodeIdeas</c>. It pins the table's columns, its snake_case status CHECK, the two jsonb documents,
/// the three plan indexes (including the partial unique "one idea per episode") and the four same-owner
/// foreign keys of 09 W3 CR-46.
/// </summary>
public sealed class EpisodeIdeaModelTests
{
    private const string SeriesStatusCreatedIndex = "IX_episode_ideas_series_status_created";
    private const string EpisodeIndex = "UX_episode_ideas_episode";
    private const string GenerationRunIndex = "IX_episode_ideas_generation_run";

    /// <summary>The series-status-created index' direction per key: <c>created_at DESC</c> (plan §3.1).</summary>
    private static readonly bool[] SeriesStatusCreatedDescending = [false, false, true];

    /// <summary>The plan §3.1 column list, compared as a set.</summary>
    private static readonly string[] Columns =
    [
        "id",
        "user_id",
        "series_id",
        "generation_run_id",
        "title",
        "logline",
        "hook",
        "setting",
        "structure",
        "novelty",
        "score",
        "similarity",
        "status",
        "rejected_reason",
        "episode_id",
        "created_at",
        "updated_at",
    ];

    [Fact]
    public void EpisodeIdea_IsTheEpisodeIdeasTable_WithThePlansColumns()
    {
        var entity = EpisodeIdeaEntity();

        entity.GetTableName().ShouldBe("episode_ideas");
        entity.FindPrimaryKey()!.Properties.Select(property => property.Name).ShouldBe(["Id"]);

        // Snake_case is the only naming this test has to trust the UseSnakeCaseNamingConvention call for.
        // The set is the contract; EF's GetProperties() order is not (the migration's physical order is
        // whatever AddEpisodeIdeas emits, the source_candidates precedent).
        entity.GetProperties().Select(property => property.GetColumnName()!)
            .ShouldBe(Columns, ignoreOrder: true, customMessage: "the plan §3.1 columns of episode_ideas");
    }

    [Fact]
    public void EpisodeIdea_IsTenantFiltered()
    {
        // The filter itself is asserted for every user-owned entity by Tenancy/TenantFilterTests; this only
        // pins that the new table joined that set (09 README D10).
        EpisodeIdeaEntity().GetDeclaredQueryFilters()
            .ShouldContain(filter => filter.Key == "tenant");
    }

    [Fact]
    public void EpisodeIdea_StatusIsSnakeCaseTextWithTheClosedSetCheck()
    {
        var status = Property(nameof(EpisodeIdea.Status));

        status.ClrType.ShouldBe(typeof(IdeaStatus));
        status.GetColumnType().ShouldBe("text");
        // D8: the enum is stored as snake_case text, which is also the status the run log shows (plan §5).
        status.GetValueConverter().ShouldNotBeNull();
        status.GetValueConverter()!.ProviderClrType.ShouldBe(typeof(string));

        // CHECK constraints are DDL, so the runtime (read-optimized) model drops them. EnumSnakeCase
        // orders the listed values ordinal, so the closed set reads picked/proposed/rejected/used.
        EpisodeIdeaDesignEntity().GetCheckConstraints()
            .ShouldContain(constraint => constraint.Name == "ck_episode_ideas_status"
                && constraint.Sql.Contains("('picked', 'proposed', 'rejected', 'used')", StringComparison.Ordinal));
    }

    [Fact]
    public void EpisodeIdea_ColumnsFollowThePlan()
    {
        // The four text widths are the varchar widths of plan §3.1, read from IdeaLimits' plan defaults.
        Property(nameof(EpisodeIdea.Title)).GetMaxLength().ShouldBe(100);
        Property(nameof(EpisodeIdea.Logline)).GetMaxLength().ShouldBe(300);
        Property(nameof(EpisodeIdea.Hook)).GetMaxLength().ShouldBe(300);
        Property(nameof(EpisodeIdea.Setting)).GetMaxLength().ShouldBe(120);

        // rejected_reason is nullable and bounded by the vocabulary's own width.
        var reason = Property(nameof(EpisodeIdea.RejectedReason));
        reason.GetMaxLength().ShouldBe(EpisodeIdea.RejectedReasonMaxChars);
        reason.IsNullable.ShouldBeTrue();

        // The two measurements are numeric(5,4) and stay NULL until 02.8.1-T2 scores the row.
        foreach (var name in new[] { nameof(EpisodeIdea.Score), nameof(EpisodeIdea.Similarity) })
        {
            var score = Property(name);

            // Plan §3.1's numeric(5,4) column is covered by a nullable decimal: no measurement before the
            // pick (the series_bibles.proposed_content precedent for a null-until-later column).
            score.ClrType.ShouldBe(typeof(decimal?));
            score.GetPrecision().ShouldBe(5);
            score.GetScale().ShouldBe(4);
            score.IsNullable.ShouldBeTrue();
        }

        Property(nameof(EpisodeIdea.EpisodeId)).IsNullable.ShouldBeTrue();

        Property(nameof(EpisodeIdea.UserId)).IsNullable.ShouldBeFalse();
        Property(nameof(EpisodeIdea.SeriesId)).IsNullable.ShouldBeFalse();
        Property(nameof(EpisodeIdea.GenerationRunId)).IsNullable.ShouldBeFalse();
        Property(nameof(EpisodeIdea.Title)).IsNullable.ShouldBeFalse();
        Property(nameof(EpisodeIdea.Logline)).IsNullable.ShouldBeFalse();
        Property(nameof(EpisodeIdea.Hook)).IsNullable.ShouldBeFalse();
        Property(nameof(EpisodeIdea.Setting)).IsNullable.ShouldBeFalse();
        Property(nameof(EpisodeIdea.Status)).IsNullable.ShouldBeFalse();

        foreach (var name in new[] { nameof(EpisodeIdea.CreatedAt), nameof(EpisodeIdea.UpdatedAt) })
        {
            var timestamp = Property(name);

            timestamp.GetColumnType().ShouldBe("timestamptz");
            timestamp.IsNullable.ShouldBeFalse();
        }

        // No `xmin`: EpisodeIdea is an Entity, not an aggregate root, and its own state machine guards every
        // transition (the FactSheet precedent).
        EpisodeIdeaEntity().GetProperties()
            .ShouldNotContain(property => property.IsConcurrencyToken, "episode_ideas carries no concurrency token");
    }

    [Fact]
    public void EpisodeIdea_DocumentsAreCanonicalJsonScalars()
    {
        var structure = Property(nameof(EpisodeIdea.Structure));
        structure.ClrType.ShouldBe(typeof(IdeaStructure));
        structure.GetColumnType().ShouldBe("jsonb");
        structure.IsNullable.ShouldBeFalse();
        // The conversion is what keeps the stored bytes equal to IdeaJson's canonical document, which is the
        // shape 02.8.4-T1's variety guard and the run log read back; the property's provider type is the jsonb
        // type mapping's, so the converter carries the string provider type.
        structure.GetValueConverter().ShouldNotBeNull();
        structure.GetValueConverter()!.ProviderClrType.ShouldBe(typeof(string));

        var novelty = Property(nameof(EpisodeIdea.Novelty));
        novelty.ClrType.ShouldBe(typeof(IdeaNoveltyDetail));
        novelty.GetColumnType().ShouldBe("jsonb");
        novelty.IsNullable.ShouldBeTrue("a proposed idea has no measurements yet");
        novelty.GetValueConverter().ShouldNotBeNull();
        novelty.GetValueConverter()!.ProviderClrType.ShouldBe(typeof(string));
    }

    [Fact]
    public void EpisodeIdea_IndexesAreThePlansThree()
    {
        var indexes = EpisodeIdeaEntity().GetIndexes();

        var seriesStatusCreated = indexes.Single(index => index.GetDatabaseName() == SeriesStatusCreatedIndex);
        seriesStatusCreated.IsUnique.ShouldBeFalse();
        seriesStatusCreated.Properties.Select(property => property.Name)
            .ShouldBe(["SeriesId", "Status", "CreatedAt"]);
        seriesStatusCreated.GetFilter().ShouldBeNull();

        // "One idea per episode" (plan §3.1): partial, because a proposed or rejected row has no episode yet.
        var episode = indexes.Single(index => index.GetDatabaseName() == EpisodeIndex);
        episode.IsUnique.ShouldBeTrue();
        episode.Properties.Select(property => property.Name).ShouldBe(["EpisodeId"]);
        episode.GetFilter().ShouldBe("\"episode_id\" IS NOT NULL");

        var run = indexes.Single(index => index.GetDatabaseName() == GenerationRunIndex);
        run.IsUnique.ShouldBeFalse();
        run.Properties.Select(property => property.Name).ShouldBe(["GenerationRunId"]);

        // Sort directions live in the design-time model only: the runtime (read-optimized) model drops them,
        // exactly as it drops a CHECK constraint.
        var designIndexes = EpisodeIdeaDesignEntity().GetIndexes();
        designIndexes.Single(index => index.GetDatabaseName() == SeriesStatusCreatedIndex)
            .IsDescending.ShouldNotBeNull().ShouldBe(SeriesStatusCreatedDescending);
        designIndexes.Single(index => index.GetDatabaseName() == GenerationRunIndex)
            .IsDescending.ShouldBeNull("only the list index orders its keys");
    }

    [Fact]
    public void EpisodeIdea_ForeignKeysAreSameOwner()
    {
        var entity = EpisodeIdeaEntity();
        var sameOwnerKey = new[] { "Id", "UserId" };

        var series = entity.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(Series));
        series.Properties.Select(property => property.Name).ShouldBe(["SeriesId", "UserId"]);
        series.PrincipalKey.Properties.Select(property => property.Name).ShouldBe(sameOwnerKey);
        // Plan §8 retention: an idea is a series-level row, deleted with its series (like series_bibles).
        series.DeleteBehavior.ShouldBe(DeleteBehavior.Cascade);

        var run = entity.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(GenerationRun));
        run.Properties.Select(property => property.Name).ShouldBe(["GenerationRunId", "UserId"]);
        run.PrincipalKey.Properties.Select(property => property.Name).ShouldBe(sameOwnerKey);
        // generation_run_id is NOT NULL, so SET NULL is impossible; the idea is the run's product.
        run.DeleteBehavior.ShouldBe(DeleteBehavior.Cascade);

        var episode = entity.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(Episode));
        episode.Properties.Select(property => property.Name).ShouldBe(["EpisodeId", "UserId"]);
        episode.PrincipalKey.Properties.Select(property => property.Name).ShouldBe(sameOwnerKey);
        // The idea is the run log's record of the pick (plan §5), so deleting the episode it became must not
        // delete it: the migration spells the nullable same-owner key as SET NULL (episode_id) with raw SQL,
        // because a plain SET NULL would also try to null the NOT NULL user_id.
        episode.DeleteBehavior.ShouldBe(DeleteBehavior.SetNull);

        var user = entity.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(AppUser));
        user.Properties.Select(property => property.Name).ShouldBe(["UserId"]);
        user.DeleteBehavior.ShouldBe(DeleteBehavior.Restrict);
    }

    private static IReadOnlyProperty Property(string name) =>
        EpisodeIdeaEntity().FindProperty(name) ?? throw new InvalidOperationException($"episode_ideas.{name} is not mapped.");

    private static IReadOnlyEntityType EpisodeIdeaEntity() => Model().FindEntityType(typeof(EpisodeIdea))!;

    /// <summary>
    /// The idea from the design-time model: CHECK constraints and index sort directions are DDL, so the
    /// runtime (read-optimized) model drops them.
    /// </summary>
    private static IReadOnlyEntityType EpisodeIdeaDesignEntity() =>
        DesignModel().FindEntityType(typeof(EpisodeIdea))!;

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
    /// The same model with the configuration the runtime model drops, such as a CHECK constraint and an
    /// index' sort direction.
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
