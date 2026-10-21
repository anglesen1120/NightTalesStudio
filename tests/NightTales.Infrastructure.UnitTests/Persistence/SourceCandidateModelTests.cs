using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using NightTales.Application.Abstractions;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.UnitTests.TestKit;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Persistence;

/// <summary>
/// The 02.7.2-T2 model contract of <c>source_candidates</c> (plan §3), read from the EF model alone: no database
/// and no migration is needed. It pins the table's columns, its snake_case status CHECK, the two plan invariants
/// (the per-episode URL uniqueness and <c>added ⇔ source_id</c>), the list index and the three same-owner keys of
/// 09 W3 CR-46.
/// </summary>
public sealed class SourceCandidateModelTests
{
    private const string EpisodeUrlIndex = "ux_source_candidates_episode_url";
    private const string ListIndex = "ix_source_candidates_episode_status_rank";

    [Fact]
    public void SourceCandidate_IsTheSourceCandidatesTable()
    {
        var entity = CandidateEntity();

        entity.GetTableName().ShouldBe("source_candidates");
        entity.FindPrimaryKey()!.Properties.Select(property => property.Name).ShouldBe(["Id"]);
    }

    [Fact]
    public void SourceCandidate_IsTenantFiltered()
    {
        // The filter itself is asserted for every user-owned entity by TenantFilterTests; this only pins
        // that the new table joined that set.
        CandidateEntity().GetDeclaredQueryFilters()
            .ShouldContain(filter => filter.Key == "tenant");
    }

    [Fact]
    public void SourceCandidate_StatusIsSnakeCaseTextWithTheClosedSetCheck()
    {
        var status = Property(nameof(SourceCandidate.Status));

        status.ClrType.ShouldBe(typeof(SourceCandidateStatus));
        status.GetColumnType().ShouldBe("text");
        // D8: the enum is stored as snake_case text, which is also the list endpoint's wire value.
        status.GetValueConverter().ShouldNotBeNull();
        status.GetValueConverter()!.ProviderClrType.ShouldBe(typeof(string));

        CandidateDesignEntity().GetCheckConstraints()
            .ShouldContain(constraint => constraint.Name == "ck_source_candidates_status"
                && constraint.Sql.Contains("('added', 'dismissed', 'proposed')", StringComparison.Ordinal));
    }

    [Fact]
    public void SourceCandidate_RankAndAddedInvariantsAreChecks()
    {
        var rank = Property(nameof(SourceCandidate.Rank));
        rank.ClrType.ShouldBe(typeof(short));
        rank.GetColumnType().ShouldBe("smallint");

        var checks = CandidateDesignEntity().GetCheckConstraints().ToList();
        checks.ShouldContain(constraint => constraint.Name == "ck_source_candidates_rank"
            && constraint.Sql.Contains("BETWEEN 1 AND 10", StringComparison.Ordinal));
        // Plan §3 invariant: an added candidate always names its source, and only an added one does.
        checks.ShouldContain(constraint => constraint.Name == "ck_source_candidates_added"
            && constraint.Sql.Contains("'added'", StringComparison.Ordinal)
            && constraint.Sql.Contains("source_id", StringComparison.Ordinal));
    }

    [Fact]
    public void SourceCandidate_UrlIsProposedOncePerEpisode()
    {
        var url = CandidateEntity().GetIndexes().Single(index => index.GetDatabaseName() == EpisodeUrlIndex);

        url.IsUnique.ShouldBeTrue();
        url.Properties.Select(property => property.Name).ShouldBe(["EpisodeId", "NormalizedUrl"]);
        url.GetFilter().ShouldBeNull("the rule is the same for proposed, added and dismissed rows");

        var list = CandidateEntity().GetIndexes().Single(index => index.GetDatabaseName() == ListIndex);
        list.IsUnique.ShouldBeFalse();
        list.Properties.Select(property => property.Name).ShouldBe(["EpisodeId", "Status", "Rank"]);
    }

    [Fact]
    public void SourceCandidate_ColumnsFollowThePlan()
    {
        Property(nameof(SourceCandidate.Url)).GetMaxLength().ShouldBe(2_048);
        Property(nameof(SourceCandidate.NormalizedUrl)).GetMaxLength().ShouldBe(2_048);
        Property(nameof(SourceCandidate.RegistrableDomain)).GetMaxLength().ShouldBe(253);
        Property(nameof(SourceCandidate.Title)).GetMaxLength().ShouldBe(300);
        Property(nameof(SourceCandidate.Title)).IsNullable.ShouldBeTrue();
        Property(nameof(SourceCandidate.SiteName)).GetColumnType().ShouldBe("text");
        Property(nameof(SourceCandidate.SourceId)).IsNullable.ShouldBeTrue();
    }

    [Fact]
    public void SourceCandidate_AllThreeForeignKeysAreSameOwnerAndCascade()
    {
        var entity = CandidateEntity();
        var sameOwnerKey = new[] { "Id", "UserId" };

        var episode = entity.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(Episode));
        episode.Properties.Select(property => property.Name).ShouldBe(["EpisodeId", "UserId"]);
        episode.PrincipalKey.Properties.Select(property => property.Name).ShouldBe(sameOwnerKey);
        // D19: a deleted episode takes its candidates with it.
        episode.DeleteBehavior.ShouldBe(DeleteBehavior.Cascade);

        var run = entity.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(GenerationRun));
        run.Properties.Select(property => property.Name).ShouldBe(["GenerationRunId", "UserId"]);
        run.PrincipalKey.Properties.Select(property => property.Name).ShouldBe(sameOwnerKey);
        // generation_run_id is NOT NULL, so SET NULL is impossible; the candidate is the run's product.
        run.DeleteBehavior.ShouldBe(DeleteBehavior.Cascade);

        var source = entity.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(StorySource));
        source.Properties.Select(property => property.Name).ShouldBe(["SourceId", "UserId"]);
        source.PrincipalKey.Properties.Select(property => property.Name).ShouldBe(sameOwnerKey);
        // SET NULL over source_id would break ck_source_candidates_added when a source is removed.
        source.DeleteBehavior.ShouldBe(DeleteBehavior.Cascade);
    }

    private static IReadOnlyProperty Property(string name) =>
        CandidateEntity().FindProperty(name) ?? throw new InvalidOperationException($"source_candidates.{name} is not mapped.");

    private static IReadOnlyEntityType CandidateEntity() => Model().FindEntityType(typeof(SourceCandidate))!;

    /// <summary>
    /// The candidate from the design-time model: CHECK constraints are DDL, so the runtime (read-optimized)
    /// model drops them.
    /// </summary>
    private static IReadOnlyEntityType CandidateDesignEntity() => DesignModel().FindEntityType(typeof(SourceCandidate))!;

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
    /// The same model with the configuration the runtime model drops, such as a CHECK constraint.
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
