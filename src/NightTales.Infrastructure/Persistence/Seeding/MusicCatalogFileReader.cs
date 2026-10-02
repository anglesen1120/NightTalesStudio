using System.Text.Json;
using ErrorOr;
using NightTales.Domain.Rendering.Music;

namespace NightTales.Infrastructure.Persistence.Seeding;

/// <summary>
/// Loads and validates the tracked <c>seed/music-tracks.v1.json</c> (05 README D8). It is the C# twin of
/// <c>worker-node/music/tools/check_catalog.py</c>: the same file keys and the same error codes, pinned
/// together by the shared <c>seed/fixtures/music</c> fixtures. A file whose shape cannot be read at all fails
/// with <see cref="CatalogInvalidCode"/>; every value rule is <see cref="MusicTrackRules"/>'s.
/// </summary>
public static class MusicCatalogFileReader
{
    /// <summary>The expected <c>schema</c> value of the file.</summary>
    public const string Schema = "music-tracks.v1";

    /// <summary>The code of a file that is not a readable <c>music-tracks.v1</c> document.</summary>
    public const string CatalogInvalidCode = "music_catalog_invalid";

    private static readonly string[] EntryStrings = ["id", "file", "title", "artist", "sourceUrl", "license", "platformScope", "sha256"];

    /// <summary>Parses and validates a catalog file.</summary>
    /// <param name="bytes">The raw UTF-8 JSON.</param>
    /// <param name="allowNonPositiveRevision">
    /// <c>true</c> only for Development (05.3.2-T3: <c>revision &lt; 1</c> warns there); every other caller,
    /// including the fixture parity test, gets <c>revision_not_positive</c>.
    /// </param>
    /// <returns>The file, or every violation found (structural ones stop at the first).</returns>
    public static ErrorOr<MusicCatalogFile> Load(ReadOnlyMemory<byte> bytes, bool allowNonPositiveRevision = false)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return Invalid("not JSON", trackId: null);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryString(root, "schema", out var schema)
                || schema != Schema
                || !root.TryGetProperty("revision", out var revisionElement)
                || revisionElement.ValueKind != JsonValueKind.Number
                || !revisionElement.TryGetInt32(out var revision)
                || !root.TryGetProperty("tracks", out var tracksElement)
                || tracksElement.ValueKind != JsonValueKind.Array)
            {
                return Invalid("schema, revision or tracks missing", trackId: null);
            }

            var entries = new List<MusicCatalogEntry>();
            foreach (var element in tracksElement.EnumerateArray())
            {
                var entry = ReadEntry(element);
                if (entry.IsError)
                {
                    return entry.Errors;
                }

                entries.Add(entry.Value);
            }

            var errors = new List<Error>();
            var revisionCheck = allowNonPositiveRevision ? Result.Success : MusicTrackRules.ValidateRevision(revision);
            if (revisionCheck.IsError)
            {
                errors.AddRange(revisionCheck.Errors);
            }

            var catalogCheck = MusicTrackRules.ValidateCatalog([.. entries.Select(entry => entry.Track)]);
            if (catalogCheck.IsError)
            {
                errors.AddRange(catalogCheck.Errors);
            }

            return errors.Count == 0 ? new MusicCatalogFile(revision, entries) : errors;
        }
    }

    private static ErrorOr<MusicCatalogEntry> ReadEntry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return Invalid("track is not an object", trackId: null);
        }

        TryString(element, "id", out var idForError);
        foreach (var key in EntryStrings)
        {
            if (!TryString(element, key, out _))
            {
                return Invalid($"'{key}' missing or not a string", idForError);
            }
        }

        if (!TryNullableString(element, "attribution", out var attribution)
            || !TryDecimal(element, "durationSec", out var duration)
            || !TryDecimal(element, "loudnessLufs", out var loudness)
            || !TryDecimal(element, "bgmVolume", out var bgmVolume)
            || !element.TryGetProperty("active", out var activeElement)
            || activeElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !element.TryGetProperty("moods", out var moodsElement)
            || moodsElement.ValueKind != JsonValueKind.Array)
        {
            return Invalid("a field is missing or has the wrong type", idForError);
        }

        var moods = new List<string>();
        foreach (var mood in moodsElement.EnumerateArray())
        {
            if (mood.ValueKind != JsonValueKind.String)
            {
                return Invalid("a mood is not a string", idForError);
            }

            moods.Add(mood.GetString()!);
        }

        // An unknown licence or scope keeps the enum's undefined default (0), so the rules report
        // license_unknown / scope_unknown instead of the reader inventing a code.
        _ = MusicTrackRules.TryParseLicense(element.GetProperty("license").GetString(), out var license);
        _ = MusicTrackRules.TryParseScope(element.GetProperty("platformScope").GetString(), out var scope);

        var candidate = new MusicTrackCandidate(
            element.GetProperty("id").GetString()!,
            element.GetProperty("file").GetString()!,
            license,
            attribution,
            scope,
            moods,
            duration,
            loudness,
            bgmVolume,
            element.GetProperty("sha256").GetString()!,
            activeElement.GetBoolean());

        return new MusicCatalogEntry(
            element.GetProperty("title").GetString()!,
            element.GetProperty("artist").GetString()!,
            element.GetProperty("sourceUrl").GetString()!,
            candidate);
    }

    private static bool TryString(JsonElement element, string property, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(property, out var found) || found.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = found.GetString();

        return true;
    }

    private static bool TryNullableString(JsonElement element, string property, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(property, out var found))
        {
            return false;
        }

        if (found.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        return TryString(element, property, out value);
    }

    private static bool TryDecimal(JsonElement element, string property, out decimal value)
    {
        value = 0m;

        return element.TryGetProperty(property, out var found)
            && found.ValueKind == JsonValueKind.Number
            && found.TryGetDecimal(out value);
    }

    private static Error Invalid(string detail, string? trackId) =>
        Error.Validation(
            CatalogInvalidCode,
            detail,
            trackId is null ? null : new Dictionary<string, object> { [MusicTrackRules.TrackIdMetadataKey] = trackId });
}
