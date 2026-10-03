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

    [Fact]
    public async Task PriceVersionSeeder_RevertedConfig_ActivatesOldRatesAgain()
    {
        var configA = Options(0.021m);
        var configB = Options(0.021m);
        configB.Models["gpt-6-luna"].Out = 0.55m;

        var a1 = await SeedAsync(configA);
        var b = await SeedAsync(configB);
        var a2 = await SeedAsync(configA);

        a2.Version.ShouldBeGreaterThan(b.Version, "A -> B -> A inserts a third row: the newest row is what the catalog serves");
        a2.ConfigHash.ShouldBe(a1.ConfigHash);
        (await CountByHashAsync(a1.ConfigHash)).ShouldBe(2, "append-only: the first A row is untouched");
    }

    [Fact]
    public async Task PriceVersionSeeder_ConcurrentDifferentConfigs_BothInsertWithDistinctVersions()
    {
        var configA = Options(0.031m);
        var configB = Options(0.032m);
        var total = await TotalAsync();

        var rows = await Task.WhenAll(SeedAsync(configA), SeedAsync(configB), SeedAsync(configA), SeedAsync(configB));

        rows.Select(row => row.Version).Distinct().Count().ShouldBeLessThanOrEqualTo(4);
        rows.Select(row => row.ConfigHash).Distinct().Count().ShouldBe(2);
        (await TotalAsync()).ShouldBeGreaterThanOrEqualTo(total + 2, "both distinct configs inserted");
        (await TotalAsync()).ShouldBeLessThanOrEqualTo(total + 4, "no seed fails on a max(version)+1 collision");
    }

    [Fact]
    public async Task PriceVersionSeeder_InvalidConfig_FailsTheSeedAndInsertsNothing()
    {
        var total = await TotalAsync();
        var zeroRate = Options(0.041m);
        zeroRate.Models["gpt-6-luna"].Out = 0m;
        var cachedAboveInput = Options(0.042m);
        cachedAboveInput.Models["gpt-6-luna"].Cached = 5m;
        var badThreshold = Options(0.043m);
        badThreshold.SurchargeThresholdTokens = 0;

        foreach (var config in new[] { zeroRate, cachedAboveInput, badThreshold })
        {
            await Should.ThrowAsync<InvalidOperationException>(() => SeedAsync(config));
        }

        (await TotalAsync()).ShouldBe(total);
    }

    [Fact]
    public async Task PriceVersionSeeder_ScaleOnlyDifferences_HashEqual()
    {
        var plain = Options(0.051m);
        var padded = Options(0.0510m);
        padded.Models["gpt-6-luna"].In = 0.100m;

        var first = await SeedAsync(plain);
        var second = await SeedAsync(padded);

        second.ConfigHash.ShouldBe(first.ConfigHash, "0.10 and 0.100 are one rate");
        second.Version.ShouldBe(first.Version);
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
