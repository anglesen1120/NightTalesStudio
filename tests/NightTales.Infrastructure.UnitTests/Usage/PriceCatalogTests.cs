using NightTales.Application.Common.Errors;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Usage;

/// <summary>
/// <c>IPriceCatalog.GetActiveAsync</c> (02.2.4-T1): a run queued before a new version settles at the version
/// that was active at its queue time, and a queue time before the first version is a configuration error.
/// The class database is shared by its tests, so every test uses its own version numbers and its own era.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PriceCatalogTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PriceCatalog_GetActive_UsesNewestRowAtOrBeforeQueuedAt()
    {
        await using var host = new UsageTestHost(Database, new FakeClock());
        var era = new DateTimeOffset(2090, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await UsageTestHost.AddVersionAsync(Database, 501, era, UsageTestHost.Luna, Ct);
        await UsageTestHost.AddVersionAsync(Database, 502, era.AddDays(10), UsageTestHost.Luna with { Input = 0.20m }, Ct);

        (await host.Catalog.GetActiveAsync(era.AddDays(9), Ct)).Value.Version.ShouldBe(501, "queued before the new version");
        (await host.Catalog.GetActiveAsync(era.AddDays(10), Ct)).Value.Version.ShouldBe(502, "active_from is inclusive");
        (await host.Catalog.GetActiveAsync(era.AddDays(11), Ct)).Value.Version.ShouldBe(502);
    }

    [Fact]
    public async Task PriceCatalog_GetActive_BeforeFirstVersion_ReturnsTheTypedError()
    {
        await using var host = new UsageTestHost(Database, new FakeClock());
        await UsageTestHost.AddVersionAsync(Database, 511, new DateTimeOffset(2091, 1, 1, 0, 0, 0, TimeSpan.Zero), UsageTestHost.Luna, Ct);

        var result = await host.Catalog.GetActiveAsync(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.PriceCatalogEmpty);
    }
}
