using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Tenancy;

/// <summary>
/// The Worker's half of 09 README D10, run through the real Worker composition root
/// (<see cref="WorkerHost"/>): a scope sees nothing until <see cref="ISystemContext.For"/> pins it to
/// one user, and from then on the tenant filter hides every other user's rows — including the episode
/// the job was not started for.
/// </summary>
/// <remarks>
/// In the "Hangfire server" collection: the Worker graph installs Hangfire's schema and its
/// process-global log provider, which the hosts of that collection share.
/// </remarks>
[Trait("Category", "Integration")]
[Collection("Hangfire server")]
public sealed class WorkerSystemContextTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task WorkerScope_UnderSystemContextA_CannotReadB()
    {
        var ct = TestContextToken.Value;
        var scenario = await TenantScenario.CreateAsync(Database, ct);
        var episodes = new EpisodeBuilder(Database, new TestCurrentUser());
        var episodeA = await episodes.CreateAsync(scenario.UserA, scenario.SeriesA, ct: ct);
        var episodeB = await episodes.CreateAsync(scenario.UserB, scenario.SeriesB, ct: ct);

        await using var host = new WorkerHost(Database, new FakeClock());
        await host.StartAsync(ct);

        using (var scope = host.CreateScope())
        {
            using var system = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(scenario.UserA);
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

            (await db.Episodes.SingleOrDefaultAsync(candidate => candidate.Id == episodeB.Id, ct))
                .ShouldBeNull("under For(A) another user's episode is missing, so the job's lookup exits (idempotent)");
            (await db.Episodes.CountAsync(ct)).ShouldBe(1);
            (await db.Episodes.SingleAsync(ct)).Id.ShouldBe(episodeA.Id, "A's own episode stays reachable through the same filter");
        }

        // One user per scope: the same graph answers for B in a fresh scope.
        using (var scope = host.CreateScope())
        {
            using var system = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(scenario.UserB);
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

            (await db.Episodes.SingleOrDefaultAsync(candidate => candidate.Id == episodeA.Id, ct)).ShouldBeNull();
            (await db.Episodes.SingleAsync(ct)).Id.ShouldBe(episodeB.Id);
        }
    }

    [Fact]
    public async Task WorkerScope_WithoutSystemContext_SeesNoRows()
    {
        var ct = TestContextToken.Value;
        var scenario = await TenantScenario.CreateAsync(Database, ct);
        var episode = await new EpisodeBuilder(Database, new TestCurrentUser())
            .CreateAsync(scenario.UserA, scenario.SeriesA, ct: ct);

        await using var host = new WorkerHost(Database, new FakeClock());
        await host.StartAsync(ct);

        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

        // 09 README D10: there is no "no user" mode — a job that forgot For() must fail loudly instead
        // of sweeping rows it was never meant to see.
        (await db.Episodes.CountAsync(ct)).ShouldBe(0);
        (await db.Episodes.SingleOrDefaultAsync(candidate => candidate.Id == episode.Id, ct)).ShouldBeNull();
    }
}
