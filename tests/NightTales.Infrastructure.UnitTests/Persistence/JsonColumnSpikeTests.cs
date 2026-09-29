using Xunit;
using Microsoft.EntityFrameworkCore;
using NightTales.Infrastructure.UnitTests.TestKit;
using NightTales.Infrastructure.UnitTests.TestKit.Probes;
using Shouldly;

namespace NightTales.Infrastructure.UnitTests.Persistence;

/// <summary>
/// The 09.2.1-T1 spike (09 README D8, N9): one row with a nested value in a JSON column, then a
/// round-trip, a query that filters on a JSON property and an <c>ExecuteUpdate</c> that sets one.
/// The outcome decides the JSON mapping every later feature uses.
/// </summary>
public sealed class JsonColumnSpikeTests(PostgresTestFixture fixture) : IClassFixture<PostgresTestFixture>
{
    [Fact]
    public async Task JsonComplexTypeSpike_RoundTripsQueriesAndUpdates()
    {
        var userId = Guid.CreateVersion7();

        await using (var write = fixture.CreateAppDbContext())
        {
            write.Add(ProbeAggregate.Create(
                userId,
                DateTimeOffset.UtcNow,
                ProbeStatus.Draft,
                "json-spike",
                new ProbeSettings { Tone = "calm", Tags = ["a", "b"] }));
            await write.SaveChangesAsync(CancellationToken.None);
        }

        await using (var read = fixture.CreateAppDbContext())
        {
            var loaded = read.Set<ProbeAggregate>().Single(aggregate => aggregate.Name == "json-spike");

            loaded.Settings.ShouldNotBeNull();
            loaded.Settings.Tone.ShouldBe("calm");
            loaded.Settings.Tags.ShouldBe(["a", "b"]);
        }

        await using (var query = fixture.CreateAppDbContext())
        {
            query.Set<ProbeAggregate>().Count(aggregate => aggregate.Settings!.Tone == "calm").ShouldBe(1);
        }

        await using (var update = fixture.CreateAppDbContext())
        {
            var updated = await update.Set<ProbeAggregate>()
                .Where(aggregate => aggregate.Name == "json-spike")
                .ExecuteUpdateAsync(setters => setters.SetProperty(aggregate => aggregate.Settings!.Tone, "loud"), CancellationToken.None);

            updated.ShouldBe(1);
        }

        await using (var verify = fixture.CreateAppDbContext())
        {
            verify.Set<ProbeAggregate>()
                .Single(aggregate => aggregate.Name == "json-spike")
                .Settings!.Tone.ShouldBe("loud");
        }
    }
}
