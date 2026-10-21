using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NightTales.Infrastructure.Persistence.Seeding;

/// <summary>
/// Seeds the catalogs at start-up in Development (09.2.5 plan §7, UC1). It runs
/// <see cref="SeedRunner"/> in its own scope when <c>IHostEnvironment.IsDevelopment()</c> and
/// <c>Seed:Enabled</c> is not <c>false</c>; a failure is logged and the boot continues, so a broken
/// catalog never keeps the Api or the Worker down.
/// </summary>
/// <remarks>
/// Registered in <c>AddSeeding</c>, so both composition roots (Api and Worker) get it. Because the
/// seeders are idempotent, either host may seed first against a fresh database.
/// </remarks>
/// <param name="scopeFactory">Creates the scope the scoped seeders are resolved from.</param>
/// <param name="environment">Decides whether the auto-seed runs at all.</param>
/// <param name="options">Carries <see cref="SeedOptions.Enabled"/>.</param>
/// <param name="logger">The failure log.</param>
public sealed partial class SeedHostedService(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment,
    IOptions<SeedOptions> options,
    ILogger<SeedHostedService> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment() || options.Value.Enabled is false)
        {
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<SeedRunner>();

            await runner.RunAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSeedFailed(exception);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 2200, Level = LogLevel.Error, Message = "seed.hosted-service.failed")]
    private partial void LogSeedFailed(Exception exception);
}
