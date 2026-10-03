using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Common;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Queries;
using NightTales.Infrastructure.Persistence.Usage;
using NightTales.Infrastructure.Security;
using NightTales.TestKit.Database;

namespace NightTales.Infrastructure.UnitTests.Usage;

/// <summary>
/// The composition the metering classes need over a class database: the Worker's persistence graph with its
/// scoped system context (the meter pins the owner of every row it writes), a catalog and a meter built on it.
/// </summary>
internal sealed class UsageTestHost : IAsyncDisposable
{
    internal static readonly PriceRates Luna = new(0.10m, 0.01m, 0.125m, 0.50m);

    private readonly ServiceProvider _provider;

    internal UsageTestHost(TestDatabase database, IClock clock, PricingOptions? options = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"ConnectionStrings:{PersistenceOptions.ConnectionStringName}"] = database.ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(clock);
        services.AddPersistence(configuration);
        services.AddScoped<ScopedCurrentUser>();
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<ScopedCurrentUser>());
        services.AddScoped<ISystemContext>(provider => provider.GetRequiredService<ScopedCurrentUser>());
        _provider = services.BuildServiceProvider();

        Options = options ?? new PricingOptions();
        Catalog = new PriceCatalog(_provider.GetRequiredService<IServiceScopeFactory>());
        Meter = new UsageMeter(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            Catalog,
            clock);
    }

    internal PricingOptions Options { get; }

    internal PriceCatalog Catalog { get; }

    internal UsageMeter Meter { get; }

    /// <summary>Inserts one version whose rates are <paramref name="luna"/> for <c>gpt-6-luna</c>.</summary>
    internal static async Task AddVersionAsync(
        TestDatabase database,
        int version,
        DateTimeOffset activeFrom,
        PriceRates luna,
        CancellationToken ct,
        int surchargeThresholdTokens = TextCostCalculator.DefaultSurchargeThresholdTokens)
    {
        var rates = new PriceVersionRates(
            new Dictionary<string, PriceRates> { ["gpt-6-luna"] = luna },
            new Dictionary<string, ImagePriceRate>(),
            null,
            surchargeThresholdTokens).ToJson();
        var limits = PriceVersionJson.WriteLimits(PriceLimits.D4Defaults);

        await using var db = database.CreateAppDbContext();
        db.PriceVersions.Add(PriceVersion.Create(version, activeFrom, rates, limits, $"test-version-{version}", activeFrom));
        await db.SaveChangesAsync(ct);
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
