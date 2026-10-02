using Microsoft.EntityFrameworkCore;
using NightTales.Infrastructure.Persistence.Usage;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Usage;

/// <summary>
/// The configuration seeder of 02.2.4-T1: a config hash maps to exactly one immutable row, an unchanged
/// config never inserts a second one, a changed rate inserts the next version. Each test seeds a config that
/// is unique to it (the web-search rate), so the shared class database cannot couple them.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PriceVersionSeederTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PriceVersionSeeder_UnchangedConfig_InsertsOneRow()
    {
        var options = Options(0.011m);
        var first = await SeedAsync(options);
        var second = await SeedAsync(options);

        second.Version.ShouldBe(first.Version);
        (await CountByHashAsync(first.ConfigHash)).ShouldBe(1);
    }

    [Fact]
    public async Task PriceVersionSeeder_ChangedRate_InsertsSecondRow()
    {
        var before = await SeedAsync(Options(0.012m));
        var changed = Options(0.012m);
        changed.Models["gpt-6-luna"].Out = 0.55m;
        var after = await SeedAsync(changed);

        after.Version.ShouldBeGreaterThan(before.Version);
        after.ConfigHash.ShouldNotBe(before.ConfigHash);
        (await CountByHashAsync(before.ConfigHash)).ShouldBe(1, "the old row is append-only and untouched");
        (await CountByHashAsync(after.ConfigHash)).ShouldBe(1);
    }

    [Fact]
    public async Task PriceVersionSeeder_IsIdempotentAcrossRestarts()
    {
        var options = Options(0.013m);
        var first = await SeedAsync(options);
        var total = await TotalAsync();

        // Three "start-ups" (a fresh seeder and scope each) over an unchanged config add nothing.
        for (var restart = 0; restart < 3; restart++)
        {
            await using var db = Database.CreateAppDbContext();
            var seeder = new PriceVersionSeeder(db, Microsoft.Extensions.Options.Options.Create(options), new FakeClock());
            (await seeder.EnsureRowAsync(Ct)).Version.ShouldBe(first.Version);
        }

        (await TotalAsync()).ShouldBe(total);
    }

    private static PricingOptions Options(decimal webSearchCall)
    {
        var options = new PricingOptions { WebSearchCallUsd = webSearchCall };
        foreach (var (model, rate) in PricingOptions.DefaultModels)
        {
            options.Models[model] = new PricingOptions.ModelPricing
            {
                In = rate.In,
                Cached = rate.Cached,
                CacheWrite = rate.CacheWrite,
                Out = rate.Out,
            };
        }

        return options;
    }

    private async Task<NightTales.Domain.Usage.PriceVersion> SeedAsync(PricingOptions options)
    {
        await using var db = Database.CreateAppDbContext();
        var seeder = new PriceVersionSeeder(db, Microsoft.Extensions.Options.Options.Create(options), new FakeClock());

        return await seeder.EnsureRowAsync(Ct);
    }

    private async Task<int> CountByHashAsync(string hash)
    {
        await using var db = Database.CreateAppDbContext();
        return await db.PriceVersions.CountAsync(row => row.ConfigHash == hash, Ct);
    }

    private async Task<int> TotalAsync()
    {
        await using var db = Database.CreateAppDbContext();
        return await db.PriceVersions.CountAsync(Ct);
    }
}
