using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Domain.Series;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Tenancy;

/// <summary>
/// <see cref="ISystemWorkQuery"/> (09 README D10): the one allow-listed cross-user read outside
/// <c>Features/Admin</c>. It yields <c>(id, user_id)</c> pairs of <em>every</em> user's rows — the
/// ids are all that crosses the tenant boundary — and it is registered by <c>AddPersistence</c>, so
/// both hosts can resolve it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SystemWorkQueryTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task SystemWorkQuery_ReturnsPairsAcrossUsers()
    {
        var ct = TestContextToken.Value;
        var scenario = await TenantScenario.CreateAsync(Database, ct);

        using var provider = PersistenceProvider(Database.ConnectionString);
        using var scope = provider.CreateScope();
        var services = scope.ServiceProvider;
        var query = services.GetRequiredService<ISystemWorkQuery>();

        // The scope's own actor is anonymous (AddPersistence's deny-by-default fallback) and the bypass
        // still sees both users: the port, not the scope, is what crosses the boundary.
        services.GetRequiredService<ICurrentUser>().Id.ShouldBeNull();

        var all = await PairsAsync(query.PendingAsync<Series>(_ => true, batch: 10, ct));

        all.ShouldBe(
            [(scenario.SeriesA, scenario.UserA), (scenario.SeriesB, scenario.UserB)],
            ignoreOrder: true,
            customMessage: "the sweep resolves work for every user, with the owner of each row");

        // The predicate is the caller's own, on top of the bypass.
        (await PairsAsync(query.PendingAsync<Series>(series => series.UserId == scenario.UserB, batch: 10, ct)))
            .ShouldBe([(scenario.SeriesB, scenario.UserB)], ignoreOrder: true);

        // The batch is a real limit: one pair asked for, one pair yielded.
        (await PairsAsync(query.PendingAsync<Series>(_ => true, batch: 1, ct))).Count.ShouldBe(1);
    }

    [Fact]
    public async Task SystemWorkQuery_RejectsAnEmptyBatch()
    {
        using var provider = PersistenceProvider(Database.ConnectionString);
        using var scope = provider.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<ISystemWorkQuery>();

        Should.Throw<ArgumentOutOfRangeException>(
            () => query.PendingAsync<Series>(_ => true, batch: 0, TestContextToken.Value));
    }

    private static async Task<List<(Guid Id, Guid UserId)>> PairsAsync(
        IAsyncEnumerable<(Guid Id, Guid UserId)> pairs)
    {
        var rows = new List<(Guid Id, Guid UserId)>();
        await foreach (var row in pairs)
        {
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Only the persistence layer, exactly as a host that registers no actor would have it.</summary>
    private static ServiceProvider PersistenceProvider(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"ConnectionStrings:{PersistenceOptions.ConnectionStringName}"] = connectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPersistence(configuration);

        return services.BuildServiceProvider();
    }
}
