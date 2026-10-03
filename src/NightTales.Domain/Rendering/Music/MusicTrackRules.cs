using ErrorOr;
using NightTales.Domain.Series;

namespace NightTales.Domain.Rendering.Music;

/// <summary>
/// The catalog rules of 05 README D8 for <c>seed/music-tracks.v1.json</c> and the <c>music_tracks</c> table,
/// with the <em>same error codes</em> as <c>worker-node/music/tools/check_catalog.py</c>, so the shared
/// fixtures (<c>seed/fixtures/music</c>) pin both implementations to one table. Every violation of a track
/// is collected into one <see cref="ErrorOr{TValue}"/>.
/// </summary>
public static class MusicTrackRules
{
    /// <summary>The longest accepted track id.</summary>
    public const int TrackIdMaxLength = 40;

    /// <summary>The shortest accepted duration, in seconds.</summary>
    public const decimal MinDurationSec = 60m;

    /// <summary>
    /// The longest accepted duration, in seconds. D8 gives only a minimum; one hour is the review-chosen
    /// ceiling (05.3.2-T3 gate SEC-3, §10 T85) — far above any background bed and far below the
    /// <c>numeric(7,1)</c> column limit, so a typo cannot store an absurd length.
    /// </summary>
    public const decimal MaxDurationSec = 3600m;

    /// <summary>The fewest moods a track carries.</summary>
    public const int MoodsMin = 1;

    /// <summary>The most moods a track carries.</summary>
    public const int MoodsMax = 3;

    /// <summary>The loudness the voice bed is mixed against (LUFS), D8's <c>MusicBedLufs</c>.</summary>
    public const decimal DefaultMusicBedLufs = -34m;

    /// <summary>The lowest stored gain.</summary>
    public const decimal BgmVolumeMin = 0.03m;

    /// <summary>The highest stored gain.</summary>
    public const decimal BgmVolumeMax = 0.40m;

    /// <summary>The metadata key carrying the offending track id of a failure (absent for file-level ones).</summary>
    public const string TrackIdMetadataKey = "track_id";

    public const string RevisionNotPositiveCode = "revision_not_positive";
    public const string TrackIdFormatCode = "track_id_format";
    public const string TrackIdNotFileStemCode = "track_id_not_file_stem";
    public const string TrackIdDuplicateCode = "track_id_duplicate";
    public const string FileDuplicateCode = "file_duplicate";
    public const string FileExtensionCode = "file_extension";
    public const string LicenseUnknownCode = "license_unknown";
    public const string AttributionRequiredCode = "attribution_required";
    public const string AttributionNotAllowedCode = "attribution_not_allowed";
    public const string ScopeUnknownCode = "scope_unknown";
    public const string ScopeLicenseMismatchCode = "scope_license_mismatch";
    public const string MoodsUnknownCode = "moods_unknown";
    public const string MoodsCountCode = "moods_count";
    public const string DurationBelowMinimumCode = "duration_below_minimum";
    public const string DurationAboveMaximumCode = "duration_above_maximum";
    public const string TitleBlankCode = "title_blank";
    public const string ArtistBlankCode = "artist_blank";
    public const string SourceUrlInvalidCode = "source_url_invalid";
    public const string Sha256FormatCode = "sha256_format";
    public const string BgmVolumeRangeCode = "bgm_volume_range";
    public const string BgmVolumeFormulaMismatchCode = "bgm_volume_formula_mismatch";
    public const string LoudnessScaleCode = "loudness_scale";

    private const int Sha256HexLength = 64;

    private static readonly string[] AllowedExtensions = [".mp3", ".m4a"];

    /// <summary>The closed mood vocabulary of D8.</summary>
    public static IReadOnlyList<string> Moods { get; } =
        ["dark_ambient", "suspense", "temple", "meditation", "noir", "calm"];

    /// <summary>The moods a topic may be scored with (D8 / M7): <c>ghost</c>, <c>spiritual</c>, <c>true_crime</c>.</summary>
    public static IReadOnlyList<string> TopicMoods(Topic topic) => topic switch
    {
        Topic.Ghost => ["dark_ambient", "suspense"],
        Topic.Spiritual => ["temple", "meditation", "calm"],
        Topic.TrueCrime => ["noir", "suspense"],
        _ => throw new ArgumentOutOfRangeException(nameof(topic), topic, "Unknown topic."),
    };

    /// <summary>The licence's stored name (<c>CC0-1.0</c>, <c>CC-BY-4.0</c>, <c>YouTube-Audio-Library</c>).</summary>
    public static string LicenseName(MusicLicense license) => license switch
    {
        MusicLicense.Cc0 => "CC0-1.0",
        MusicLicense.CcBy => "CC-BY-4.0",
        MusicLicense.YouTubeAudioLibrary => "YouTube-Audio-Library",
        _ => throw new ArgumentOutOfRangeException(nameof(license), license, "Unknown licence."),
    };

    /// <summary>Parses a stored licence name; <c>false</c> for any other text.</summary>
    public static bool TryParseLicense(string? name, out MusicLicense license)
    {
        license = name switch
        {
            "CC0-1.0" => MusicLicense.Cc0,
            "CC-BY-4.0" => MusicLicense.CcBy,
            "YouTube-Audio-Library" => MusicLicense.YouTubeAudioLibrary,
            _ => default,
        };

        return license != default;
    }

    /// <summary>The scope's stored name (<c>any</c> or <c>youtube</c>).</summary>
    public static string ScopeName(MusicPlatformScope scope) => scope switch
    {
        MusicPlatformScope.Any => "any",
        MusicPlatformScope.YouTube => "youtube",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown scope."),
    };

    /// <summary>Parses a stored scope name; <c>false</c> for any other text.</summary>
    public static bool TryParseScope(string? name, out MusicPlatformScope scope)
    {
        scope = name switch
        {
            "any" => MusicPlatformScope.Any,
            "youtube" => MusicPlatformScope.YouTube,
            _ => default,
        };

        return scope != default;
    }

    /// <summary>The revision rule: the file's <c>revision</c> is at least 1.</summary>
    public static ErrorOr<Success> ValidateRevision(int revision) =>
        revision >= 1
            ? Result.Success
            : Fail(RevisionNotPositiveCode, $"revision={revision}", trackId: null);

    /// <summary>
    /// Every D8 rule of one track. All violations are returned together, in the order
    /// <c>check_catalog.py</c> evaluates them.
    /// </summary>
    /// <param name="candidate">The track under test.</param>
    public static ErrorOr<Success> Validate(MusicTrackCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var errors = new List<Error>();
        var id = candidate.TrackId;

        if (!IsTrackId(id))
        {
            errors.Add(Failure(TrackIdFormatCode, $"id={id}", id));
        }

        var file = candidate.FileName;
        if (string.IsNullOrEmpty(file) || !AllowedExtensions.Contains(ExtensionOf(file), StringComparer.Ordinal))
        {
            errors.Add(Failure(FileExtensionCode, $"file={file}", id));
        }
        else if (!string.Equals(StemOf(file), id, StringComparison.Ordinal))
        {
            errors.Add(Failure(TrackIdNotFileStemCode, $"id={id} file={file}", id));
        }

        if (string.IsNullOrWhiteSpace(candidate.Title))
        {
            errors.Add(Failure(TitleBlankCode, "title is blank", id));
        }

        if (string.IsNullOrWhiteSpace(candidate.Artist))
        {
            errors.Add(Failure(ArtistBlankCode, "artist is blank", id));
        }

        if (!IsHttpsUrl(candidate.SourceUrl))
        {
            errors.Add(Failure(SourceUrlInvalidCode, $"sourceUrl={candidate.SourceUrl}", id));
        }
        var licenseKnown = Enum.IsDefined(candidate.License);
        if (!licenseKnown)
        {
            errors.Add(Failure(LicenseUnknownCode, $"license={(int)candidate.License}", id));
        }

        var hasAttribution = !string.IsNullOrWhiteSpace(candidate.Attribution);
        if (candidate.License == MusicLicense.CcBy && !hasAttribution)
        {
            errors.Add(Failure(AttributionRequiredCode, "CC-BY-4.0 needs attribution", id));
        }

        if (candidate.License != MusicLicense.CcBy && hasAttribution)
        {
            errors.Add(Failure(AttributionNotAllowedCode, "attribution only applies to CC-BY-4.0", id));
        }

        if (!Enum.IsDefined(candidate.PlatformScope))
        {
            errors.Add(Failure(ScopeUnknownCode, $"platformScope={(int)candidate.PlatformScope}", id));
        }
        else if (licenseKnown && candidate.PlatformScope != RequiredScope(candidate.License))
        {
            errors.Add(Failure(
                ScopeLicenseMismatchCode,
                $"{LicenseName(candidate.License)} -> {ScopeName(candidate.PlatformScope)}",
                id));
        }

        ValidateMoods(candidate.Moods, id, errors);

        if (candidate.DurationSec < MinDurationSec)
        {
            errors.Add(Failure(DurationBelowMinimumCode, $"durationSec={candidate.DurationSec}", id));
        }
        else if (candidate.DurationSec > MaxDurationSec)
        {
            errors.Add(Failure(DurationAboveMaximumCode, $"durationSec={candidate.DurationSec}", id));
        }

        if (!IsSha256(candidate.Sha256))
        {
            errors.Add(Failure(Sha256FormatCode, "sha256 is not 64 lower-case hex characters", id));
        }

        ValidateGain(candidate, id, errors);

        return errors.Count == 0 ? Result.Success : errors;
    }

    /// <summary>
    /// The catalog-level rules over a file's tracks: every track passes <see cref="Validate"/>, and no id or
    /// file name repeats (<c>track_id_duplicate</c>, <c>file_duplicate</c>).
    /// </summary>
    /// <param name="tracks">The file's tracks, in file order.</param>
    public static ErrorOr<Success> ValidateCatalog(IReadOnlyList<MusicTrackCandidate> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var errors = new List<Error>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var files = new HashSet<string>(StringComparer.Ordinal);

        foreach (var track in tracks)
        {
            var validated = Validate(track);
            if (validated.IsError)
            {
                errors.AddRange(validated.Errors);
            }

            if (IsTrackId(track.TrackId) && !ids.Add(track.TrackId))
            {
                errors.Add(Failure(TrackIdDuplicateCode, $"id={track.TrackId}", track.TrackId));
            }

            if (!string.IsNullOrEmpty(track.FileName) && !files.Add(track.FileName))
            {
                errors.Add(Failure(FileDuplicateCode, $"file={track.FileName}", track.TrackId));
            }
        }

        return errors.Count == 0 ? Result.Success : errors;
    }

    /// <summary>
    /// D8's gain: <c>round(clamp(10^((musicBedLufs − loudnessLufs) / 20), 0.03, 0.40), 3)</c>. A louder track
    /// gets a smaller gain, so the bed sits at the same level under the voice. The clamp is the only range:
    /// any loudness maps to a gain in [0.03, 0.40] (<c>−80</c> → 0.40, as <c>check_catalog.py</c> pins).
    /// </summary>
    /// <param name="loudnessLufs">The track's measured integrated loudness.</param>
    /// <param name="musicBedLufs">The target bed loudness, <see cref="DefaultMusicBedLufs"/> by default.</param>
    public static ErrorOr<decimal> ComputeBgmVolume(decimal loudnessLufs, decimal musicBedLufs = DefaultMusicBedLufs)
    {
        // Double arithmetic: a decimal subtraction of two extreme inputs would overflow, and the clamp
        // below makes the precision beyond the third place irrelevant.
        var raw = Math.Pow(10d, ((double)musicBedLufs - (double)loudnessLufs) / 20d);
        var clamped = Math.Clamp(raw, (double)BgmVolumeMin, (double)BgmVolumeMax);

        return Math.Round((decimal)clamped, MusicTrack.BgmVolumeScale, MidpointRounding.AwayFromZero);
    }

    private static void ValidateMoods(IReadOnlyList<string>? moods, string? id, List<Error> errors)
    {
        if (moods is null || moods.Count < MoodsMin || moods.Count > MoodsMax)
        {
            errors.Add(Failure(MoodsCountCode, $"count={moods?.Count ?? 0}", id));
        }

        foreach (var mood in moods ?? [])
        {
            if (!Moods.Contains(mood, StringComparer.Ordinal))
            {
                errors.Add(Failure(MoodsUnknownCode, $"mood={mood}", id));
            }
        }
    }

    private static void ValidateGain(MusicTrackCandidate candidate, string? id, List<Error> errors)
    {
        var persistedLoudness = Math.Round(candidate.LoudnessLufs, MusicTrack.LoudnessScale, MidpointRounding.AwayFromZero);
        if (persistedLoudness != candidate.LoudnessLufs)
        {
            errors.Add(Failure(
                LoudnessScaleCode,
                $"loudnessLufs={candidate.LoudnessLufs} has more than {MusicTrack.LoudnessScale} decimals",
                id));
        }

        if (candidate.BgmVolume < BgmVolumeMin || candidate.BgmVolume > BgmVolumeMax)
        {
            errors.Add(Failure(BgmVolumeRangeCode, $"bgmVolume={candidate.BgmVolume}", id));

            return;
        }

        // The database CHECK recomputes the gain from the stored (rounded) loudness, so the file's gain must
        // agree with that value, not with the unrounded one.
        var expected = ComputeBgmVolume(persistedLoudness).Value;
        if (Math.Round(candidate.BgmVolume, MusicTrack.BgmVolumeScale, MidpointRounding.AwayFromZero) != expected)
        {
            errors.Add(Failure(
                BgmVolumeFormulaMismatchCode,
                $"bgmVolume={candidate.BgmVolume} expected={expected}",
                id));
        }
    }

    /// <summary>An absolute <c>https</c> URL with a host.</summary>
    private static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrEmpty(uri.Host);

    private static MusicPlatformScope RequiredScope(MusicLicense license) =>
        license == MusicLicense.YouTubeAudioLibrary ? MusicPlatformScope.YouTube : MusicPlatformScope.Any;

    /// <summary><c>^[a-z0-9]+(-[a-z0-9]+)*$</c>, at most <see cref="TrackIdMaxLength"/> characters.</summary>
    private static bool IsTrackId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > TrackIdMaxLength)
        {
            return false;
        }

        var previousWasHyphen = true;
        foreach (var character in value)
        {
            if (character == '-')
            {
                if (previousWasHyphen)
                {
                    return false;
                }

                previousWasHyphen = true;
                continue;
            }

            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9')))
            {
                return false;
            }

            previousWasHyphen = false;
        }

        return !previousWasHyphen;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: Sha256HexLength } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static int ExtensionStart(string file)
    {
        var dot = file.LastIndexOf('.');

        return dot > 0 ? dot : file.Length;
    }

    private static string ExtensionOf(string file) => file[ExtensionStart(file)..];

    private static string StemOf(string file) => file[..ExtensionStart(file)];

    private static Error Failure(string code, string detail, string? trackId) =>
        Error.Validation(
            code,
            detail,
            trackId is null ? null : new Dictionary<string, object> { [TrackIdMetadataKey] = trackId });

    private static ErrorOr<Success> Fail(string code, string detail, string? trackId) =>
        Failure(code, detail, trackId);
}
