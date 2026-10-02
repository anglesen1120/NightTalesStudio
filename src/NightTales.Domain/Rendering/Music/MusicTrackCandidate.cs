namespace NightTales.Domain.Rendering.Music;

/// <summary>
/// The rule-relevant fields of one catalog track (05 README D8), as read from
/// <c>seed/music-tracks.v1.json</c> or a row: the input of <see cref="MusicTrackRules.Validate"/>.
/// </summary>
/// <param name="TrackId">The id (<c>id</c> in the file); it equals the file stem.</param>
/// <param name="FileName">The audio file name, <c>.mp3</c> or <c>.m4a</c>.</param>
/// <param name="License">The parsed licence; an undefined value is the <c>license_unknown</c> failure.</param>
/// <param name="Attribution">The attribution text, required exactly for <see cref="MusicLicense.CcBy"/>.</param>
/// <param name="PlatformScope">The parsed scope; an undefined value is the <c>scope_unknown</c> failure.</param>
/// <param name="Moods">One to three mood tags of <see cref="MusicTrackRules.Moods"/>.</param>
/// <param name="DurationSec">The duration in seconds, at least <see cref="MusicTrackRules.MinDurationSec"/>.</param>
/// <param name="LoudnessLufs">The measured integrated loudness.</param>
/// <param name="BgmVolume">The stored gain, equal to <see cref="MusicTrackRules.ComputeBgmVolume"/>.</param>
/// <param name="Sha256">The lower-case hex SHA-256 of the audio file.</param>
/// <param name="Active">Whether the track is offered to the selector.</param>
public sealed record MusicTrackCandidate(
    string TrackId,
    string FileName,
    MusicLicense License,
    string? Attribution,
    MusicPlatformScope PlatformScope,
    IReadOnlyList<string> Moods,
    decimal DurationSec,
    decimal LoudnessLufs,
    decimal BgmVolume,
    string Sha256,
    bool Active);
