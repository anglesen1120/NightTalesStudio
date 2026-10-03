using NightTales.Application.Abstractions.Usage;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Persistence.Usage;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Usage;

/// <summary>
/// QA boundary tests of 02.2.4-T1 (added by the QA pass, not by the task's own suite): the seed hash
/// must be insensitive to config key order, a usage row's tenant id must be exactly the
/// <c>UsageItem.UserId</c>, the long-context threshold must come from <c>OpenAi:Pricing</c> options,
/// and token-count validation must reject negatives at zero boundary.
/// </summary>
[Trait("Category", "Integration")]
public sealed class QaUsageMeteringTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Qa_Seeder_ConfigHash_ModelKeyOrderInsignificant()
    {
        // The same rates, the models entered in the opposite order: one row, one hash — a reordered
        // config file must not mint a new version.
        var forward = Options(0.021m);
        var reverse = Options(0.021m);
        reverse.Models = reverse.Models.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        var first = await SeedAsync(forward);
        var second = await SeedAsync(reverse);

        second.ConfigHash.ShouldBe(first.ConfigHash, "model key order is canonicalised before hashing");
        second.Version.ShouldBe(first.Version, "an identical logical config never inserts a second row");
    }

    [Fact]
    public async Task Qa_UsageRow_UserId_IsExactlyTheUsageItemUserId()
    {
        await using var host = new UsageTestHost(Database, new FakeClock());
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);
        await UsageTestHost.AddVersionAsync(Database, 701, new DateTimeOffset(2090, 1, 1, 0, 0, 0, TimeSpan.Zero), UsageTestHost.Luna, Ct);
        var request = $"req-{Guid.NewGuid():N}";

        await host.Meter.RecordAsync(
            new UsageItem(user.Id, "openai", "story", "gpt-6-luna", 12_000, 0, 0, 3_000,
                ReservationId: null, EpisodeId: null, Estimated: false, CostUsd: null,
                PriceVersion: "701", ProviderRequestId: request),
            Ct);

        var ids = await Database.QueryStringsAsync(
            $"SELECT user_id::text FROM usage_records WHERE provider_request_id = '{request}'", Ct);
        ids.ShouldHaveSingleItem().ShouldBe(user.Id.ToString(), "the tenant column is UsageItem.UserId and nothing else");
    }

    [Fact]
    public async Task Qa_UsageMeter_Threshold_ComesFromTheRunsPriceVersion()
    {
        // Threshold 1,000 stored in the version's rates: an 12,000-token input doubles the input rate and
        // multiplies output by 1.5 → (12000×0.20 + 3000×0.75)/1e6 = 0.004650.
        await using var host = new UsageTestHost(Database, new FakeClock());
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);
        await UsageTestHost.AddVersionAsync(
            Database, 711, new DateTimeOffset(2090, 1, 1, 0, 0, 0, TimeSpan.Zero), UsageTestHost.Luna, Ct, surchargeThresholdTokens: 1_000);
        var request = $"req-{Guid.NewGuid():N}";

        await host.Meter.RecordAsync(
            new UsageItem(user.Id, "openai", "story", "gpt-6-luna", 12_000, 0, 0, 3_000,
                ReservationId: null, EpisodeId: null, Estimated: false, CostUsd: null,
                PriceVersion: "711", ProviderRequestId: request),
            Ct);

        var costs = await Database.QueryStringsAsync(
            $"SELECT cost_usd::text FROM usage_records WHERE provider_request_id = '{request}'", Ct);
        decimal.Parse(costs.ShouldHaveSingleItem(), System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(0.004650m, "the O3 surcharge is driven by the configured threshold");
    }

    [Fact]
    public async Task Qa_UsageMeter_EstimatedWithBound_IgnoresComputedCost()
    {
        // Estimated = true + usage present + bound: the bound wins — the caller asserted the provider
        // usage is untrustworthy, so it prices at the D1 bound (conservative, never lower than actual).
        await using var host = new UsageTestHost(Database, new FakeClock());
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);
        await UsageTestHost.AddVersionAsync(Database, 721, new DateTimeOffset(2090, 1, 1, 0, 0, 0, TimeSpan.Zero), UsageTestHost.Luna, Ct);
        var request = $"req-{Guid.NewGuid():N}";

        await host.Meter.RecordAsync(
            new UsageItem(user.Id, "openai", "story", "gpt-6-luna", 12_000, 0, 0, 3_000,
                ReservationId: null, EpisodeId: null, Estimated: true, CostUsd: 0.5m,
                PriceVersion: "721", ProviderRequestId: request),
            Ct);

        var rows = await Database.QueryStringsAsync(
            $"SELECT cost_usd::text || '|' || estimated::text FROM usage_records WHERE provider_request_id = '{request}'", Ct);
        rows.ShouldHaveSingleItem().ShouldBe("0.500000|true");
    }

    [Fact]
    public async Task Qa_UsageMeter_CachedAboveInput_StillWritesAnEstimatedRowAtTheCallersBound()
    {
        await using var host = new UsageTestHost(Database, new FakeClock());
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);
        await UsageTestHost.AddVersionAsync(Database, 731, new DateTimeOffset(2090, 1, 1, 0, 0, 0, TimeSpan.Zero), UsageTestHost.Luna, Ct);
        var request = $"req-{Guid.NewGuid():N}";

        await host.Meter.RecordAsync(
            new UsageItem(user.Id, "openai", "story", "gpt-6-luna", 1_000, 5_000, 0, 300,
                ReservationId: null, EpisodeId: null, Estimated: false, CostUsd: 0.25m,
                PriceVersion: "731", ProviderRequestId: request),
            Ct);

        (await Database.QueryStringsAsync(
            $"SELECT cost_usd::text || '|' || estimated::text FROM usage_records WHERE provider_request_id = '{request}'", Ct))
            .ShouldHaveSingleItem().ShouldBe("0.250000|true", "a paid call with a provider miscount keeps its row at the D1 bound");
    }

    [Fact]
    public async Task Qa_UsageMeter_UnpricedModelAfterAPaidCall_StillWritesAnEstimatedRowAtTheCallersBound()
    {
        await using var host = new UsageTestHost(Database, new FakeClock());
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);
        await UsageTestHost.AddVersionAsync(Database, 741, new DateTimeOffset(2090, 1, 1, 0, 0, 0, TimeSpan.Zero), UsageTestHost.Luna, Ct);
        var request = $"req-{Guid.NewGuid():N}";

        await host.Meter.RecordAsync(
            new UsageItem(user.Id, "openai", "story", "gpt-6-unlisted", 1_000, 0, 0, 300,
                ReservationId: null, EpisodeId: null, Estimated: false, CostUsd: 0.25m,
                PriceVersion: "741", ProviderRequestId: request),
            Ct);

        (await Database.QueryStringsAsync(
            $"SELECT cost_usd::text || '|' || estimated::text FROM usage_records WHERE provider_request_id = '{request}'", Ct))
            .ShouldHaveSingleItem().ShouldBe("0.250000|true");
    }

    [Fact]
    public async Task Qa_UsageMeter_UnpricedModel_WithoutABound_Throws()
    {
        await using var host = new UsageTestHost(Database, new FakeClock());
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);
        await UsageTestHost.AddVersionAsync(Database, 751, new DateTimeOffset(2090, 1, 1, 0, 0, 0, TimeSpan.Zero), UsageTestHost.Luna, Ct);

        await Should.ThrowAsync<UnknownModelPricingException>(() => host.Meter.RecordAsync(
            new UsageItem(user.Id, "openai", "story", "gpt-6-unlisted", 1_000, 0, 0, 300,
                ReservationId: null, EpisodeId: null, Estimated: false, CostUsd: null,
                PriceVersion: "751", ProviderRequestId: $"req-{Guid.NewGuid():N}"),
            Ct));
    }

    [Fact]
    public async Task Qa_UsageMeter_AlreadyCancelledToken_StillWritesTheRow()
    {
        await using var host = new UsageTestHost(Database, new FakeClock());
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);
        await UsageTestHost.AddVersionAsync(Database, 761, new DateTimeOffset(2090, 1, 1, 0, 0, 0, TimeSpan.Zero), UsageTestHost.Luna, Ct);
        var request = $"req-{Guid.NewGuid():N}";
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await host.Meter.RecordAsync(
            new UsageItem(user.Id, "openai", "story", "gpt-6-luna", 12_000, 0, 0, 3_000,
                ReservationId: null, EpisodeId: null, Estimated: false, CostUsd: null,
                PriceVersion: "761", ProviderRequestId: request),
            cancelled.Token);

        (await Database.QueryStringsAsync(
            $"SELECT count(*)::text FROM usage_records WHERE provider_request_id = '{request}'", Ct))
            .ShouldHaveSingleItem().ShouldBe("1", "the call was paid for: the cancelled job must not lose its cost");
    }

    [Theory]
    [InlineData(-1, 0, 0, null)]
    [InlineData(10, 0, -1, null)]
    [InlineData(10, -1, 0, null)]
    [InlineData(null, null, null, 0)]
    [InlineData(null, null, null, -1)]
    public async Task Qa_UsageMeter_InvalidItem_IsRejectedBeforeAnyWrite(int? input, int? cached, int? output, int? costCents)
    {
        await using var host = new UsageTestHost(Database, new FakeClock());
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);
        var request = $"req-{Guid.NewGuid():N}";

        await Should.ThrowAsync<ArgumentException>(() => host.Meter.RecordAsync(
            new UsageItem(user.Id, "openai", "story", "gpt-6-luna", input, cached, 0, output,
                ReservationId: null, EpisodeId: null, Estimated: costCents is not null, CostUsd: costCents / 100m,
                PriceVersion: "771", ProviderRequestId: request),
            Ct));

        (await Database.QueryStringsAsync(
            $"SELECT count(*)::text FROM usage_records WHERE provider_request_id = '{request}'", Ct))
            .ShouldHaveSingleItem().ShouldBe("0");
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-3")]
    [InlineData("0")]
    [InlineData("1.5")]
    public async Task Qa_UsageMeter_NonNumericPriceVersion_IsRejectedWithAnArgumentException(string priceVersion)
    {
        await using var host = new UsageTestHost(Database, new FakeClock());
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);

        await Should.ThrowAsync<ArgumentException>(() => host.Meter.RecordAsync(
            new UsageItem(user.Id, "openai", "story", "gpt-6-luna", 10, 0, 0, 10,
                ReservationId: null, EpisodeId: null, Estimated: false, CostUsd: null,
                PriceVersion: priceVersion, ProviderRequestId: $"req-{Guid.NewGuid():N}"),
            Ct));
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

    private async Task<PriceVersion> SeedAsync(PricingOptions options)
    {
        await using var db = Database.CreateAppDbContext();
        var seeder = new PriceVersionSeeder(db, Microsoft.Extensions.Options.Options.Create(options), new FakeClock());

        return await seeder.EnsureRowAsync(Ct);
    }
}
