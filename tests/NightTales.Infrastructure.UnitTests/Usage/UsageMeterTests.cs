using System.Globalization;
using NightTales.Application.Abstractions.Usage;
using NightTales.Domain.Usage;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Usage;

/// <summary>
/// <c>IUsageMeter</c> (02 README D14, 02.2.4-T1): one row per provider call, written in the meter's own
/// transaction so a failed job keeps its cost, priced with the rates of the run's price version, and never
/// free when the provider reported no usage. Rows are read back by their provider request id, so the shared
/// class database cannot couple the tests.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UsageMeterTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UsageMeter_WritesOneRowPerCall()
    {
        await using var host = new UsageTestHost(Database, new FakeClock());
        var (userId, version) = await ArrangeAsync(601);
        var request = Request();

        await host.Meter.RecordAsync(Item(userId, version, "story", providerRequestId: request + "-a"), Ct);
        await host.Meter.RecordAsync(Item(userId, version, "story_review", providerRequestId: request + "-b"), Ct);

        (await Rows(request + "%")).Count.ShouldBe(2);
    }

    [Fact]
    public async Task UsageMeter_FailedJob_StillWritesRow()
    {
        await using var host = new UsageTestHost(Database, new FakeClock());
        var (userId, version) = await ArrangeAsync(611);
        var request = Request();

        // The job pays for the call, meters it, and only then fails with story_invalid_output.
        var failure = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await host.Meter.RecordAsync(Item(userId, version, "story", providerRequestId: request), Ct);
            throw new InvalidOperationException("story_invalid_output");
        });

        failure.Message.ShouldBe("story_invalid_output");
        (await Rows(request)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task UsageMeter_UsageAbsent_MarksEstimatedAndUsesBound()
    {
        await using var host = new UsageTestHost(Database, new FakeClock());
        var (userId, version) = await ArrangeAsync(621);
        var request = Request();
        const decimal Bound = 0.012300m;

        await host.Meter.RecordAsync(
            Item(userId, version, "story", providerRequestId: request, withUsage: false, costUsd: Bound),
            Ct);

        var row = (await Rows(request)).ShouldHaveSingleItem();
        row.Estimated.ShouldBe("true");
        decimal.Parse(row.CostUsd, CultureInfo.InvariantCulture).ShouldBe(Bound);
        decimal.Parse(row.CostUsd, CultureInfo.InvariantCulture).ShouldNotBe(0m, "an unmetered call must not look free");

        // A bound is mandatory: an estimated item without one is a caller bug, never a free row.
        await Should.ThrowAsync<ArgumentException>(() => host.Meter.RecordAsync(
            Item(userId, version, "story", providerRequestId: request + "-x", withUsage: false, costUsd: null),
            Ct));
    }

    [Fact]
    public async Task UsageMeter_UsesRunPriceVersionRates()
    {
        await using var host = new UsageTestHost(Database, new FakeClock());
        var (userId, oldVersion) = await ArrangeAsync(631);
        var era = new DateTimeOffset(2092, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await UsageTestHost.AddVersionAsync(Database, 632, era, UsageTestHost.Luna with { Input = 1.00m, Output = 5.00m }, Ct);
        var request = Request();

        // The run was queued under 631: it settles at 631's rates although 632 exists now (worked example A).
        await host.Meter.RecordAsync(Item(userId, oldVersion, "story", providerRequestId: request + "-old"), Ct);
        await host.Meter.RecordAsync(Item(userId, "632", "story", providerRequestId: request + "-new"), Ct);

        var rows = await Rows(request + "%");
        decimal.Parse(rows.Single(row => row.RequestId.EndsWith("-old", StringComparison.Ordinal)).CostUsd, CultureInfo.InvariantCulture)
            .ShouldBe(0.002700m);
        decimal.Parse(rows.Single(row => row.RequestId.EndsWith("-new", StringComparison.Ordinal)).CostUsd, CultureInfo.InvariantCulture)
            .ShouldBe(0.027000m);
    }

    [Fact]
    public async Task UsageMeter_MapsOperationFromStep()
    {
        await using var host = new UsageTestHost(Database, new FakeClock());
        var (userId, version) = await ArrangeAsync(641);
        var request = Request();
        string[] operations =
            ["story", "story_review", "story_revision", "fact_sheet", "bible_proposal", "idea", "source_find", "moderation"];

        foreach (var operation in operations)
        {
            await host.Meter.RecordAsync(Item(userId, version, operation, providerRequestId: $"{request}-{operation}"), Ct);
        }

        var stored = await Rows(request + "%");
        stored.Select(row => row.Operation).Order(StringComparer.Ordinal).ShouldBe(operations.Order(StringComparer.Ordinal));

        await Should.ThrowAsync<ArgumentException>(
            () => host.Meter.RecordAsync(Item(userId, version, "not_an_operation", providerRequestId: request + "-bad"), Ct));
    }

    [Fact]
    public async Task UsageMeter_StoresProviderRequestId()
    {
        await using var host = new UsageTestHost(Database, new FakeClock());
        var (userId, version) = await ArrangeAsync(651);
        var request = Request();

        await host.Meter.RecordAsync(Item(userId, version, "story", providerRequestId: request), Ct);

        var row = (await Rows(request)).ShouldHaveSingleItem();
        row.RequestId.ShouldBe(request);
        row.Provider.ShouldBe("openai");
        row.Model.ShouldBe("gpt-6-luna");
        row.PriceVersion.ShouldBe("651");
    }

    [Fact]
    public async Task UsageMeter_OwnTransaction_IsNotRolledBackWithTheJob()
    {
        await using var host = new UsageTestHost(Database, new FakeClock());
        var (userId, version) = await ArrangeAsync(661);
        var request = Request();

        // The "job": a caller transaction on its own connection, rolled back after the call was metered.
        await using (var job = Database.CreateAppDbContext())
        await using (var transaction = await job.Database.BeginTransactionAsync(Ct))
        {
            await host.Meter.RecordAsync(Item(userId, version, "story", providerRequestId: request), Ct);
            await transaction.RollbackAsync(Ct);
        }

        (await Rows(request)).ShouldHaveSingleItem("the meter committed in its own transaction");
    }

    private async Task<(Guid UserId, string Version)> ArrangeAsync(int version)
    {
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);
        await UsageTestHost.AddVersionAsync(Database, version, new DateTimeOffset(2090, 1, 1, 0, 0, 0, TimeSpan.Zero), UsageTestHost.Luna, Ct);

        return (user.Id, version.ToString(CultureInfo.InvariantCulture));
    }

    private static string Request() => $"req-{Guid.NewGuid():N}";

    private static UsageItem Item(
        Guid userId,
        string priceVersion,
        string operation,
        string providerRequestId,
        bool withUsage = true,
        decimal? costUsd = null) =>
        new(
            userId,
            "openai",
            operation,
            "gpt-6-luna",
            withUsage ? 12_000 : null,
            withUsage ? 0 : null,
            withUsage ? 0 : null,
            withUsage ? 3_000 : null,
            ReservationId: Guid.NewGuid(),
            EpisodeId: null,
            Estimated: !withUsage,
            CostUsd: costUsd,
            PriceVersion: priceVersion,
            ProviderRequestId: providerRequestId);

    private sealed record Row(string Operation, string Provider, string Model, string CostUsd, string Estimated, string PriceVersion, string RequestId);

    private async Task<List<Row>> Rows(string requestIdPattern)
    {
        var lines = await Database.QueryStringsAsync(
            "SELECT concat_ws('|', operation, provider, model, cost_usd::text, estimated::text, price_version::text, provider_request_id) "
            + $"FROM usage_records WHERE provider_request_id LIKE '{requestIdPattern}'",
            Ct);

        return [.. lines.Select(line =>
        {
            var part = line.Split('|');
            return new Row(part[0], part[1], part[2], part[3], part[4], part[5], part[6]);
        })];
    }
}
