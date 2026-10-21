using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Catalog;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Persistence.Seeding;

/// <summary>
/// Upserts the <c>voices</c> catalog (09.2.5 plan §3.1/§3.4) by natural key <see cref="Voice.Id"/>, from
/// the tracked <c>seed/voices.v1.json</c> in <em>every</em> environment (04 README D9, CR-9). Rows
/// absent from the file are inserted, present rows are updated in every catalog-owned column —
/// including <c>preview_storage_key</c> — and nothing is ever deleted, so a series never loses its
/// voice (P6).
/// </summary>
/// <remarks>
/// The file is the same one the proxy loads, validated with the same rules and the same codes
/// (<see cref="VoiceCatalogRules"/>, shared fixtures 04 D14): any structural or value failure aborts
/// the whole seed with <c>seed_voice_invalid:&lt;id or -&gt;:&lt;code&gt;</c> before a single row is
/// written. A file at <c>revision 0</c> is the development placeholder and is refused outside
/// Development, so a production operator cannot ship it.
/// </remarks>
public sealed partial class VoiceSeeder(
    IAppDbContext db,
    IOptions<SeedOptions> options,
    IHostEnvironment environment,
    ILogger<VoiceSeeder> logger) : ISeeder
{
    /// <summary>The <c>&lt;id&gt;</c> field of a failure code whose problem is not tied to one entry.</summary>
    private const string NoEntryId = "-";

    /// <inheritdoc />
    public string Name => "voices";

    /// <inheritdoc />
    public int Order => 10;

    /// <inheritdoc />
    public async ValueTask<SeedReport> SeedAsync(CancellationToken ct)
    {
        var entries = LoadEntries();
        if (entries is null)
        {
            return new SeedReport(Name, 0, 0, 0);
        }

        try
        {
            return await ApplyAsync(entries, ct);
        }
        catch (DbUpdateException exception) when (SeedDbContext.IsUniqueViolation(exception))
        {
            // The Api and the Worker may seed at the same time: the loser of the natural-key race
            // re-reads the committed row and applies it as an update (plan §Failure handling).
            LogConcurrentInsertRetry();
            SeedDbContext.ResetTracking(db);

            return await ApplyAsync(entries, ct);
        }
    }

    private IReadOnlyList<VoiceCatalogEntry>? LoadEntries()
    {
        var catalogFile = options.Value.VoiceCatalogFile;
        var path = SeedFiles.Resolve(catalogFile, environment);
        if (path is null)
        {
            if (!environment.IsDevelopment())
            {
                throw new SeedValidationException($"seed_voices_missing:{catalogFile}");
            }

            // A missing dev catalog is not fatal: the other seeders still run (task failure handling).
            LogCatalogMissing(catalogFile);

            return null;
        }

        var loaded = VoiceCatalogFileReader.Load(File.ReadAllBytes(path));
        if (loaded.IsError)
        {
            throw SeedFailure(loaded.FirstError);
        }

        var file = loaded.Value;
        if (file.Revision == 0 && !environment.IsDevelopment())
        {
            throw SeedFailure(VoiceCatalogRules.CatalogInvalid, null);
        }

        return file.Voices;
    }

    private async ValueTask<SeedReport> ApplyAsync(IReadOnlyList<VoiceCatalogEntry> entries, CancellationToken ct)
    {
        var existing = await db.Voices.ToDictionaryAsync(voice => voice.Id, ct);
        var inserted = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var entry in entries)
        {
            var language = ParseLanguage(entry.Language);
            var gender = ParseGender(entry.Gender);

            if (!existing.TryGetValue(entry.Id, out var voice))
            {
                db.Voices.Add(Voice.Create(
                    entry.Id,
                    entry.DisplayName,
                    language,
                    gender,
                    entry.Tags,
                    entry.PreviewStorageKey,
                    entry.Active));
                inserted++;
                continue;
            }

            if (voice.DisplayName == entry.DisplayName
                && voice.Language == language
                && voice.Gender == gender
                && voice.Active == entry.Active
                && voice.PreviewStorageKey == entry.PreviewStorageKey
                && voice.Tags.SequenceEqual(entry.Tags, StringComparer.Ordinal))
            {
                skipped++;
                continue;
            }

            voice.UpdateFromCatalog(entry.DisplayName, language, gender, entry.Tags, entry.Active, entry.PreviewStorageKey);
            updated++;
        }

        if (inserted + updated > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return new SeedReport(Name, inserted, updated, skipped);
    }

    /// <summary>The seeder already validated the raw value, so an unknown one is a bug, not input.</summary>
    private static Language ParseLanguage(string language) => language switch
    {
        "vi" => Language.Vi,
        "en" => Language.En,
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "The language must have been validated."),
    };

    /// <summary>The seeder already validated the raw value, so an unknown one is a bug, not input.</summary>
    private static VoiceGender ParseGender(string gender) => gender switch
    {
        "female" => VoiceGender.Female,
        "male" => VoiceGender.Male,
        "neutral" => VoiceGender.Neutral,
        _ => throw new ArgumentOutOfRangeException(nameof(gender), gender, "The gender must have been validated."),
    };

    private static SeedValidationException SeedFailure(Error error) =>
        SeedFailure(error.Code, VoiceIdOf(error));

    private static SeedValidationException SeedFailure(string code, string? voiceId) =>
        new($"seed_voice_invalid:{voiceId ?? NoEntryId}:{code}");

    /// <summary>The id the failing rule reported, or <c>null</c> for a file-level failure.</summary>
    private static string? VoiceIdOf(Error error) =>
        error.Metadata is not null
        && error.Metadata.TryGetValue(VoiceCatalogRules.VoiceIdMetadataKey, out var voiceId)
        && voiceId is string id
            ? id
            : null;

    [LoggerMessage(EventId = 2300, Level = LogLevel.Warning, Message = "seed.voices.catalog-missing path={Path}")]
    private partial void LogCatalogMissing(string path);

    [LoggerMessage(EventId = 2301, Level = LogLevel.Information, Message = "seed.voices.concurrent-insert-retry")]
    private partial void LogConcurrentInsertRetry();
}
