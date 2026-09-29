using Xunit;
using NightTales.Infrastructure.UnitTests.TestKit;
using NightTales.Infrastructure.UnitTests.TestKit.Probes;
using Shouldly;

namespace NightTales.Infrastructure.UnitTests.Persistence;

// 09.1.5-T1: creates its own database on the NTS_TEST_PG server, so the unit job (no NTS_TEST_PG)
// excludes it with Category=Integration and the CI integration job runs it.
[Trait("Category", "Integration")]
public sealed class ReadDbContextDatabaseTests(PostgresTestFixture fixture) : IClassFixture<PostgresTestFixture>
{
    [Fact]
    public async Task ReadDbContext_Queries_AreNotTracked()
    {
        await using (var write = fixture.CreateAppDbContext())
        {
            write.Add(ProbeAggregate.Create(
                Guid.CreateVersion7(),
                DateTimeOffset.UtcNow,
                ProbeStatus.Done,
                "tracked-check"));
            await write.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = fixture.CreateReadDbContext();
        var loaded = read.Query<ProbeAggregate>().Single(aggregate => aggregate.Name == "tracked-check");

        loaded.Name.ShouldBe("tracked-check");
        read.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public void ReadDbContext_MapsTheSameTablesAsTheWriteContext()
    {
        using var read = fixture.CreateReadDbContext();

        var tables = TestSql.QueryStrings(
            read,
            "SELECT table_name FROM information_schema.tables WHERE table_name = 'probe_aggregates'");

        tables.ShouldHaveSingleItem();
    }
}
