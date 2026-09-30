using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NightTales.Infrastructure.Persistence.Seeding;

/// <summary>
/// Runs every <see cref="ISeeder"/> in <see cref="ISeeder.Order"/> (09.2.5 plan §3.1), logging one line
/// per seeder. It stops at the first failure unless <see cref="SeedOptions.ContinueOnError"/> is set, so
/// the explicit <c>nts-seed</c> command surfaces a non-zero exit while a Development boot just logs.
/// </summary>
/// <param name="seeders">The registered seeders.</param>
/// <param name="options">The section that owns <see cref="SeedOptions.ContinueOnError"/>.</param>
/// <param name="logger">The run log.</param>
public sealed partial class SeedRunner(
    IEnumerable<ISeeder> seeders,
    IOptions<SeedOptions> options,
    ILogger<SeedRunner> logger)
{
    /// <summary>Runs every seeder and returns their reports, in run order.</summary>
    public async ValueTask<SeedReport[]> RunAsync(CancellationToken ct)
    {
        var ordered = seeders.OrderBy(seeder => seeder.Order).ToArray();
        var reports = new List<SeedReport>(ordered.Length);

        foreach (var seeder in ordered)
        {
            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                var report = await seeder.SeedAsync(ct);
                reports.Add(report);

#pragma warning disable CA1873 // LoggerMessage partial methods are not recognized as guarded by IsEnabled.
                if (logger.IsEnabled(LogLevel.Information))
                {
                    LogSeederCompleted(
                        seeder.Name,
                        report.Inserted,
                        report.Updated,
                        report.Skipped,
                        (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
                }
#pragma warning restore CA1873
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogSeederFailed(exception, seeder.Name);
                if (!options.Value.ContinueOnError)
                {
                    throw;
                }

                reports.Add(new SeedReport(seeder.Name, 0, 0, 0));
            }
        }

        return [.. reports];
    }

    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Information,
        Message = "seed.runner.completed seeder={Seeder} inserted={Inserted} updated={Updated} skipped={Skipped} durationMs={DurationMs}")]
    private partial void LogSeederCompleted(string seeder, int inserted, int updated, int skipped, long durationMs);

    [LoggerMessage(EventId = 2101, Level = LogLevel.Error, Message = "seed.runner.failed seeder={Seeder}")]
    private partial void LogSeederFailed(Exception exception, string seeder);
}
