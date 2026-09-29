using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.UnitTests.TestKit.Probes;

namespace NightTales.Infrastructure.UnitTests.TestKit;

/// <summary>Builds the probe contexts with the same provider options <c>AddPersistence</c> uses.</summary>
internal static class ProbeContexts
{
    public const string UnusedConnectionString = "Host=127.0.0.1;Port=5432;Database=nts_never_connected;Username=nts;Password=nts";

    public static DbContextOptions<AppDbContext> AppOptions(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .ReplaceService<IModelCustomizer, ProbeModelCustomizer>();

        return builder.Options;
    }

    public static DbContextOptions<ReadDbContext> ReadOptions(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<ReadDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ReplaceService<IModelCustomizer, ProbeModelCustomizer>();

        return builder.Options;
    }
}
