using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Seeding;

/// <summary>
/// The Development gate of the auto-seed (09.2.5 plan §7, task acceptance): a host booted in
/// <c>Production</c> runs <see cref="NightTales.Infrastructure.Persistence.Seeding.SeedHostedService"/>
/// and it writes nothing, so no non-Development boot ever touches the catalogs.
/// </summary>
public sealed class SeedHostedServiceTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task SeedHostedService_NonDevelopment_DoesNothing()
    {
        await using var factory = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder => builder.UseEnvironment(Environments.Production));

        // Reading Services builds and starts the production host, which starts its hosted services.
        _ = factory.Services;

        var voices = await Database.QueryStringsAsync("SELECT count(*)::text FROM voices", TestContext.Current.CancellationToken);

        voices.Single().ShouldBe("0");
    }
}
