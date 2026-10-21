using System.Globalization;
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

    [Fact]
    public async Task SeedHostedService_Development_SeedsCatalog()
    {
        try
        {
            await using var factory = ApiFactory.ProductionHost(
                Database.ConnectionString,
                builder => builder.UseEnvironment(Environments.Development));

            // Reading Services builds and starts the development host, which starts its hosted
            // services: SeedHostedService runs the seeders against this class's database before
            // Services returns.
            _ = factory.Services;

            (await ScalarAsync("SELECT count(*)::text FROM voices")).ShouldBe(
                SeedTestHarness.SeedVoices.Count.ToString(CultureInfo.InvariantCulture));
            (await ScalarAsync("SELECT count(*)::text FROM style_templates")).ShouldBe("7");
            (await ScalarAsync("SELECT count(*)::text FROM feature_flags")).ShouldBe("3");
            (await ScalarAsync("SELECT count(*)::text FROM feature_flags WHERE enabled")).ShouldBe("0");
        }
        finally
        {
            // The two tests in this class share the one class database, and xUnit may run them in
            // either order: leave the catalogs empty so
            // SeedHostedService_NonDevelopment_DoesNothing keeps observing no rows.
            await Database.ExecuteAsync(
                "DELETE FROM voices; DELETE FROM style_templates; DELETE FROM feature_flags",
                TestContext.Current.CancellationToken);
        }
    }

    private async Task<string> ScalarAsync(string sql)
        => (await Database.QueryStringsAsync(sql, TestContext.Current.CancellationToken)).Single();
}
