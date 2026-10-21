using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Tenancy;

/// <summary>
/// The tenant filter of 09 README D10 against a real PostgreSQL 17: another user's rows are invisible
/// on the write <em>and</em> on the read model, an anonymous scope sees nothing at all, the filter is
/// a per-query parameter rather than a value baked into the cached model, and the ordering guard of
/// <see cref="ISystemContext"/> fires in the Api's composition root.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TenantFilterTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task TenantFilter_OtherUsersRows_AreInvisible_OnBothContexts()
    {
        var ct = TestContextToken.Value;
        var scenario = await TenantScenario.CreateAsync(Database, ct);

        await using (var write = Database.CreateAppDbContext(TenantScenario.ActorFor(scenario.UserA)))
        {
            (await write.Series.CountAsync(ct)).ShouldBe(1, "A owns exactly one series");
            (await write.Series.SingleAsync(ct)).Id.ShouldBe(scenario.SeriesA);
            (await write.Series.AnyAsync(candidate => candidate.Id == scenario.SeriesB, ct))
                .ShouldBeFalse("a lookup of B's id misses, so the handler answers 404 and not 403 (09 README D10)");
        }

        await using (var read = Database.CreateReadDbContext(TenantScenario.ActorFor(scenario.UserA)))
        {
            (await read.Query<Series>().CountAsync(ct)).ShouldBe(1, "the read model carries the same filter as the write model");
            (await read.Query<Series>().AnyAsync(candidate => candidate.Id == scenario.SeriesB, ct)).ShouldBeFalse();
            (await read.Query<Series>().SingleAsync(ct)).Id.ShouldBe(scenario.SeriesA);
        }

        // The hidden rows exist: what the two contexts hide is the other user's row, not the whole table
        // (the class database accumulates the rows of every test in this class, so the check is scoped).
        CountScenarioSeries(scenario).ShouldBe(2, "both series are in the table; the filter, not the seed, hides them");
    }

    [Fact]
    public async Task TenantFilter_Anonymous_SeesNoRows()
    {
        var ct = TestContextToken.Value;
        var scenario = await TenantScenario.CreateAsync(Database, ct);

        await using (var write = Database.CreateAppDbContext())
        await using (var read = Database.CreateReadDbContext())
        {
            (await write.Series.CountAsync(ct)).ShouldBe(0, "no user means the filter matches no row");
            (await read.Query<Series>().CountAsync(ct)).ShouldBe(0);
            (await write.Series.AnyAsync(candidate => candidate.Id == scenario.SeriesA, ct)).ShouldBeFalse();
            (await read.Query<Series>().AnyAsync(candidate => candidate.Id == scenario.SeriesB, ct)).ShouldBeFalse();
        }

        CountScenarioSeries(scenario).ShouldBe(2, "the rows are there; an anonymous scope simply cannot see them");
    }

    [Fact]
    public async Task TenantFilter_IsParameterized_NotCached()
    {
        var ct = TestContextToken.Value;
        var scenario = await TenantScenario.CreateAsync(Database, ct);

        // The same query text against two scopes with different users. EF caches the model per context
        // type, so a filter that had captured a value instead of the context member would still answer
        // with A's row in B's scope (09 README D10, plan §2 T2f).
        await using (var write = Database.CreateAppDbContext(TenantScenario.ActorFor(scenario.UserA)))
        {
            (await write.Series.SingleAsync(ct)).Id.ShouldBe(scenario.SeriesA);
        }

        await using (var write = Database.CreateAppDbContext(TenantScenario.ActorFor(scenario.UserB)))
        {
            (await write.Series.SingleAsync(ct)).Id.ShouldBe(scenario.SeriesB);
        }

        await using (var read = Database.CreateReadDbContext(TenantScenario.ActorFor(scenario.UserA)))
        {
            (await read.Query<Series>().SingleAsync(ct)).Id.ShouldBe(scenario.SeriesA);
        }

        await using (var read = Database.CreateReadDbContext(TenantScenario.ActorFor(scenario.UserB)))
        {
            (await read.Query<Series>().SingleAsync(ct)).Id.ShouldBe(scenario.SeriesB);
        }
    }

    [Fact]
    public void TenantFilter_EveryUserOwnedEntity_OfTheWriteModel_HasTheTenantFilter()
    {
        using var write = Database.CreateAppDbContext();

        AssertEveryUserOwnedEntityIsFiltered(write.Model);
    }

    [Fact]
    public void TenantFilter_EveryUserOwnedEntity_OfTheReadModel_HasTheTenantFilter()
    {
        using var read = Database.CreateReadDbContext();

        AssertEveryUserOwnedEntityIsFiltered(read.Model);
    }

    private static void AssertEveryUserOwnedEntityIsFiltered(IModel model)
    {
        var owned = model.GetEntityTypes()
            .Where(entityType => typeof(IUserOwned).IsAssignableFrom(entityType.ClrType))
            .ToList();

        owned.ShouldNotBeEmpty("the check is vacuous without user-owned entities");
        owned.Select(entityType => entityType.ClrType.Name)
            .ShouldContain("GenerationRun", "the new D2 table is user-owned");
        owned.Select(entityType => entityType.ClrType.Name)
            .ShouldContain("FactSheet", "the 02.5.3 fact_sheets table is user-owned");

        var unfiltered = owned
            .Where(entityType => entityType.GetDeclaredQueryFilters().All(filter => filter.Key != "tenant"))
            .Select(entityType => entityType.ClrType.Name)
            .ToList();

        unfiltered.ShouldBeEmpty("every IUserOwned entity needs the \"tenant\" query filter (09 README D10)");
    }

    [Fact]
    public async Task SystemContext_For_AfterContextResolved_Throws()
    {
        // The Api is where a context can be resolved before the system context is set (outbox-style work
        // started in-process), so the guard is proven in its composition root, not only in a unit test.
        await using var factory = new ApiFactory(Database, new FakeClock());
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        // Constructing the context is what pins the filter's user; no query is needed for the guard.
        _ = services.GetRequiredService<IAppDbContext>();

        var exception = Should.Throw<InvalidOperationException>(
            () => services.GetRequiredService<ISystemContext>().For(Guid.CreateVersion7()));

        exception.Message.ShouldBe("ISystemContext.For must run before any DbContext is created in this scope");
    }

    [Fact]
    public async Task TenantFilter_ForeignParentIsUnreachable_SoAChildCannotBeAttached()
    {
        var ct = TestContextToken.Value;
        var scenario = await TenantScenario.CreateAsync(Database, ct);

        // NTS-922-SEC-001 option (a) — this task's half: a handler that would attach a child to a parent
        // loads the parent through the filtered context, so B cannot reach A's series at all and the
        // command answers not_found. The database's own rule (composite same-owner foreign keys) arrives
        // with 09.2.2-T2's InitialCore, which is why this task also owns the 404 helper below.
        await using var write = Database.CreateAppDbContext(TenantScenario.ActorFor(scenario.UserB));

        (await write.Series.SingleOrDefaultAsync(series => series.Id == scenario.SeriesA, ct)).ShouldBeNull();

        // What such a miss returns: another user's row is a 404, never a 403 (09 README D10).
        var notFound = Errors.NotFound();
        notFound.Code.ShouldBe(ErrorCodes.NotFound);
        notFound.Type.ShouldBe(ErrorType.NotFound);
    }

    /// <summary>How many rows of this scenario are in the table, whatever the filter hides.</summary>
    private long CountScenarioSeries(TenantScenario scenario) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM series WHERE id IN ('{scenario.SeriesA}', '{scenario.SeriesB}')");
}
