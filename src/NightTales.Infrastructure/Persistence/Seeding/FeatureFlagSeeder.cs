using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Platform;

namespace NightTales.Infrastructure.Persistence.Seeding;

/// <summary>
/// Inserts the publishing flags of the dev catalog (09.2.5 plan §3.1/§3.4), all disabled, and never
/// touches an existing row: a reseed repairs a missing key but does not undo an admin toggle
/// (<see cref="FeatureFlag.Enabled"/> and <see cref="FeatureFlag.Rules"/> are preserved).
/// </summary>
/// <remarks>
/// The keys come from <c>featureFlags</c> in <c>Seed:CatalogFile</c>. When that file is missing in
/// Development the flags still seed from <see cref="DefaultKeys"/>, so a developer has the publishing
/// switches off and present without the catalog.
/// </remarks>
public sealed partial class FeatureFlagSeeder(
    IAppDbContext db,
    IOptions<SeedOptions> options,
    IHostEnvironment environment,
    ILogger<FeatureFlagSeeder> logger) : ISeeder
{
    /// <summary>The publishing flags of the platform (02.7, 06, plan §3.4), used when no file lists them.</summary>
    public static readonly IReadOnlyList<string> DefaultKeys = ["youtube_publish", "tiktok_publish", "source_finder"];

    /// <inheritdoc />
    public string Name => "feature_flags";

    /// <inheritdoc />
    public int Order => 30;

    /// <inheritdoc />
    public async ValueTask<SeedReport> SeedAsync(CancellationToken ct)
    {
        var keys = LoadKeys();

        try
        {
            return await ApplyAsync(keys, ct);
        }
        catch (DbUpdateException exception) when (SeedDbContext.IsUniqueViolation(exception))
        {
            LogConcurrentInsertRetry();
            SeedDbContext.ResetTracking(db);

            return await ApplyAsync(keys, ct);
        }
    }

    private IReadOnlyList<string> LoadKeys()
    {
        var path = SeedFiles.Resolve(options.Value.CatalogFile, environment);
        if (path is null)
        {
            if (!environment.IsDevelopment())
            {
                throw new SeedValidationException($"seed_feature_flags_missing:{options.Value.CatalogFile}");
            }

            LogCatalogMissing(options.Value.CatalogFile);

            return DefaultKeys;
        }

        return SeedCatalogFile.ReadFeatureFlags(File.ReadAllText(path));
    }

    private async ValueTask<SeedReport> ApplyAsync(IReadOnlyList<string> keys, CancellationToken ct)
    {
        var existing = await db.FeatureFlags.Select(flag => flag.Key).ToListAsync(ct);
        var known = existing.ToHashSet(StringComparer.Ordinal);
        var inserted = 0;
        var skipped = 0;

        foreach (var key in keys)
        {
            if (!known.Add(key))
            {
                skipped++;
                continue;
            }

            // Insert-only: an existing row is never loaded or written, so its enabled/rules survive.
            db.FeatureFlags.Add(FeatureFlag.Create(key));
            inserted++;
        }

        if (inserted > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return new SeedReport(Name, inserted, 0, skipped);
    }

    [LoggerMessage(EventId = 2320, Level = LogLevel.Warning, Message = "seed.feature-flags.catalog-missing path={Path}")]
    private partial void LogCatalogMissing(string path);

    [LoggerMessage(EventId = 2321, Level = LogLevel.Information, Message = "seed.feature-flags.concurrent-insert-retry")]
    private partial void LogConcurrentInsertRetry();
}
