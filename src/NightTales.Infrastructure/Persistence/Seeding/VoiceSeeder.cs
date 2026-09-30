using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Catalog;

namespace NightTales.Infrastructure.Persistence.Seeding;

/// <summary>
/// Upserts the <c>voices</c> catalog (09.2.5 plan §3.1/§3.4) by natural key <see cref="Voice.Id"/>.
/// Rows absent from the file are inserted, present rows are updated only in the fields the catalog owns,
/// and nothing is ever deleted.
/// </summary>
/// <remarks>
/// The source is <c>Seed:CatalogFile</c> (the dev placeholder set, 4 rows: 2 vi + 2 en). 04.2.2-T2
/// switches the source to <c>seed/voices.v1.json</c> in every environment and adds
/// <c>VoiceCatalogRules</c> plus the <c>revision</c> check; the upsert code does not change.
/// </remarks>
public sealed partial class VoiceSeeder(
    IAppDbContext db,
    IOptions<SeedOptions> options,
    IHostEnvironment environment,
    ILogger<VoiceSeeder> logger) : ISeeder
{
    /// <inheritdoc />
    public string Name => "voices";

    /// <inheritdoc />
    public int Order => 10;

    /// <inheritdoc />
    public async ValueTask<SeedReport> SeedAsync(CancellationToken ct)
    {
        var rows = LoadRows();
        if (rows is null)
        {
            return new SeedReport(Name, 0, 0, 0);
        }

        try
        {
            return await ApplyAsync(rows, ct);
        }
        catch (DbUpdateException exception) when (SeedDbContext.IsUniqueViolation(exception))
        {
            // The Api and the Worker may seed at the same time: the loser of the natural-key race
            // re-reads the committed row and applies it as an update (plan §Failure handling).
            LogConcurrentInsertRetry();
            SeedDbContext.ResetTracking(db);

            return await ApplyAsync(rows, ct);
        }
    }

    private IReadOnlyList<VoiceSeedRow>? LoadRows()
    {
        var path = SeedFiles.Resolve(options.Value.CatalogFile, environment);
        if (path is null)
        {
            if (!environment.IsDevelopment())
            {
                throw new SeedValidationException($"seed_voices_missing:{options.Value.CatalogFile}");
            }

            // A missing dev catalog is not fatal: the flags still seed (task failure handling).
            LogCatalogMissing(options.Value.CatalogFile);

            return null;
        }

        return SeedCatalogFile.ReadVoices(File.ReadAllText(path));
    }

    private async ValueTask<SeedReport> ApplyAsync(IReadOnlyList<VoiceSeedRow> rows, CancellationToken ct)
    {
        var existing = await db.Voices.ToDictionaryAsync(voice => voice.Id, ct);
        var inserted = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var row in rows)
        {
            if (!existing.TryGetValue(row.Id, out var voice))
            {
                db.Voices.Add(Voice.Create(row.Id, row.DisplayName, row.Language, row.Gender, row.Tags, active: row.Active));
                inserted++;
                continue;
            }

            if (voice.DisplayName == row.DisplayName
                && voice.Language == row.Language
                && voice.Gender == row.Gender
                && voice.Active == row.Active
                && voice.Tags.SequenceEqual(row.Tags, StringComparer.Ordinal))
            {
                skipped++;
                continue;
            }

            voice.UpdateFromCatalog(row.DisplayName, row.Language, row.Gender, row.Tags, row.Active);
            updated++;
        }

        if (inserted + updated > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return new SeedReport(Name, inserted, updated, skipped);
    }

    [LoggerMessage(EventId = 2300, Level = LogLevel.Warning, Message = "seed.voices.catalog-missing path={Path}")]
    private partial void LogCatalogMissing(string path);

    [LoggerMessage(EventId = 2301, Level = LogLevel.Information, Message = "seed.voices.concurrent-insert-retry")]
    private partial void LogConcurrentInsertRetry();
}
