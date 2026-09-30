using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Outbox;
using NightTales.Infrastructure.Time;
using NightTales.Infrastructure.UnitTests.TestKit.Probes;

namespace NightTales.Infrastructure.UnitTests.TestKit;

/// <summary>Builds the probe contexts with the same provider options <c>AddPersistence</c> uses.</summary>
internal static class ProbeContexts
{
    public const string UnusedConnectionString = "Host=127.0.0.1;Port=5432;Database=nts_never_connected;Username=nts;Password=nts";

    /// <summary>
    /// The outbox write side, built over the probe events of this assembly (09 README D11): the probe model
    /// stores its events exactly as <c>src</c> stores the Domain's.
    /// </summary>
    private static readonly OutboxEventRegistry OutboxEvents = new(typeof(ProbeModelCustomizer).Assembly);

    public static DbContextOptions<AppDbContext> AppOptions(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .ReplaceService<IModelCustomizer, ProbeModelCustomizer>()
            // The same two interceptors in the same order as AddPersistence (09 W3 CR-38).
            .AddInterceptors(
                new AuditableSaveChangesInterceptor(new SystemClock()),
                new OutboxSaveChangesInterceptor(OutboxEvents));

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
