using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;

namespace NightTales.Api.IntegrationTests.Tenancy;

/// <summary>
/// Two users of one database with one series each (09.2.3-T1): the smallest world in which the tenant
/// filter has something to hide. The rows are written by the TestKit builders through
/// <c>AppDbContext</c>, exactly like production code writes them.
/// </summary>
/// <param name="UserA">The first owner.</param>
/// <param name="UserB">The second owner.</param>
/// <param name="SeriesA">A's series id; B must never see it.</param>
/// <param name="SeriesB">B's series id; A must never see it.</param>
internal sealed record TenantScenario(Guid UserA, Guid UserB, Guid SeriesA, Guid SeriesB)
{
    /// <summary>Creates both users and both series in <paramref name="database"/>.</summary>
    public static async Task<TenantScenario> CreateAsync(TestDatabase database, CancellationToken ct)
    {
        // The builders' own actor is irrelevant: the tenant filter does not restrict writes, only reads.
        var actor = new TestCurrentUser();
        var userA = await new UserBuilder(database, actor).CreateAsync(ct: ct);
        var userB = await new UserBuilder(database, actor).CreateAsync(ct: ct);

        var builder = new SeriesBuilder(database, actor);
        var seriesA = await builder.CreateAsync(userA.Id, title: "A's series", ct: ct);
        var seriesB = await builder.CreateAsync(userB.Id, title: "B's series", ct: ct);

        return new TenantScenario(userA.Id, userB.Id, seriesA.Id, seriesB.Id);
    }

    /// <summary>An actor for one of the two users, as a request scope or a Worker scope would hold it.</summary>
    public static TestCurrentUser ActorFor(Guid userId) => new() { Id = userId };
}
