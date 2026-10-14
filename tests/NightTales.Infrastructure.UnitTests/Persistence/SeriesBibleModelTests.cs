using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NightTales.Application.Abstractions;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Bible;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.UnitTests.TestKit;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Persistence;

/// <summary>
/// The model contract of <c>series_bibles</c> (02.6.3 plan §3.3), read from the EF model alone: no database and no
/// migration is needed, so this stays green while the orchestrator generates <c>AddSeriesBible</c>. It covers the
/// column spec the task owns — the two jsonb documents, the version default, the one-to-one series relationship
/// and the two indexes — plus the tenant filter the table joins.
/// </summary>
public sealed class SeriesBibleModelTests
{
    private const string SeriesIndex = "UX_series_bibles_series_id";
    private const string UserIndex = "IX_series_bibles_user_id";

    [Fact]
    public void SeriesBible_IsTheSeriesBiblesTable_OneToOneWithItsSeries()
    {
        var entity = Bible();

        entity.GetTableName().ShouldBe("series_bibles");
        entity.FindPrimaryKey()!.Properties.Select(property => property.Name).ShouldBe(["Id"]);

        // The same-owner composite FK every child table reaches a series through (NTS-922-SEC-001), declared as a
        // one-to-one from the series side: a bible of another user can never point at this series.
        var series = entity.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(Series));
        series.Properties.Select(property => property.Name).ShouldBe(["SeriesId", "UserId"]);
        series.PrincipalKey.Properties.Select(property => property.Name).ShouldBe(["Id", "UserId"]);
        series.IsUnique.ShouldBeTrue("a series owns one bible row, not one per version");
        series.DeleteBehavior.ShouldBe(DeleteBehavior.Cascade);
    }

    [Fact]
    public void SeriesBible_Indexes_AreTheUniqueSeriesIndexAndTheTenantIndex()
    {
        var indexes = Bible().GetIndexes();

        var series = indexes.Single(index => index.GetDatabaseName() == SeriesIndex);
        series.IsUnique.ShouldBeTrue();
        series.Properties.Select(property => property.Name).ShouldBe(["SeriesId"]);

        var user = indexes.Single(index => index.GetDatabaseName() == UserIndex);
        user.IsUnique.ShouldBeFalse();
        user.Properties.Select(property => property.Name).ShouldBe(["UserId"]);
    }

    [Fact]
    public void SeriesBible_IsTenantFiltered()
    {
        // The filter itself is asserted for every user-owned entity by the Api tenancy tests; this only pins that
        // the new table joined that set (09 README D10).
        Bible().GetDeclaredQueryFilters().ShouldContain(filter => filter.Key == "tenant");
    }

    [Fact]
    public void SeriesBible_DocumentsAreCanonicalJsonScalars()
    {
        var content = Property(nameof(SeriesBible.Content));
        content.ClrType.ShouldBe(typeof(SeriesBibleContent));
        content.GetColumnType().ShouldBe("jsonb");
        content.IsNullable.ShouldBeFalse();
        // The conversion is what keeps the stored bytes equal to the canonical document the validator measures with
        // MaxContentBytes; the property's provider type is the jsonb type mapping's, so the converter carries the
        // string provider type. BibleJsonContextTests pins that the two documents are byte-identical.
        content.GetValueConverter().ShouldNotBeNull();
        content.GetValueConverter()!.ProviderClrType.ShouldBe(typeof(string));

        var proposal = Property(nameof(SeriesBible.ProposedContent));
        proposal.ClrType.ShouldBe(typeof(BibleProposal));
        proposal.GetColumnType().ShouldBe("jsonb");
        proposal.IsNullable.ShouldBeTrue();
        proposal.GetValueConverter().ShouldNotBeNull();
        proposal.GetValueConverter()!.ProviderClrType.ShouldBe(typeof(string));
    }

    [Fact]
    public void SeriesBible_Version_IsThePlanDefaultAndNotAConcurrencyToken()
    {
        var version = Property(nameof(SeriesBible.Version));

        version.ClrType.ShouldBe(typeof(int));
        version.GetDefaultValue().ShouldBe(SeriesBible.InitialVersion);
        // The ETag source is the version itself (`"sb-<version>"`, D9), never `xmin` and never a second
        // concurrency mechanism: the commands take the row lock through IAppDbContext and compare the number.
        version.IsConcurrencyToken.ShouldBeFalse();
    }

    [Fact]
    public void SeriesBible_EpisodeForeignKeyIsNullableAndSetNull()
    {
        var episode = Bible().GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(Episode));

        episode.Properties.Select(property => property.Name).ShouldBe(["ProposedFromEpisodeId"]);
        episode.PrincipalKey.Properties.Select(property => property.Name).ShouldBe(["Id"]);
        // The one FK of this table that is not a same-owner composite: the column is nullable and a composite
        // could not be SET NULL when the episode goes, because user_id is not nullable.
        episode.DeleteBehavior.ShouldBe(DeleteBehavior.SetNull);
        Property(nameof(SeriesBible.ProposedFromEpisodeId)).IsNullable.ShouldBeTrue();
    }

    [Fact]
    public void SeriesBible_UserForeignKeyIsRestrict()
    {
        var user = Bible().GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(AppUser));

        user.Properties.Select(property => property.Name).ShouldBe(["UserId"]);
        user.DeleteBehavior.ShouldBe(DeleteBehavior.Restrict);
    }

    [Fact]
    public void SeriesBible_TimestampsAreTimestamptz()
    {
        foreach (var name in new[] { nameof(SeriesBible.CreatedAt), nameof(SeriesBible.UpdatedAt) })
        {
            var property = Property(name);

            property.GetColumnType().ShouldBe("timestamptz");
            property.IsNullable.ShouldBeFalse();
        }
    }

    private static IReadOnlyProperty Property(string name) =>
        Bible().FindProperty(name) ?? throw new InvalidOperationException($"series_bibles.{name} is not mapped.");

    private static IReadOnlyEntityType Bible() => Model().FindEntityType(typeof(SeriesBible))!;

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
}
