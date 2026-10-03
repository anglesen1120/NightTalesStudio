namespace NightTales.Domain.Rendering.Music;

/// <summary>
/// The licence of a catalog track (05 README D8). Stored as the licence's own name
/// (<c>CC0-1.0</c>, <c>CC-BY-4.0</c>, <c>YouTube-Audio-Library</c>), see <see cref="MusicTrackRules"/>.
/// </summary>
public enum MusicLicense
{
    /// <summary>Creative Commons Zero: no attribution, any platform.</summary>
    Cc0 = 1,

    /// <summary>Creative Commons Attribution: the track's attribution text must be shown (06 CR-5).</summary>
    CcBy = 2,

    /// <summary>The YouTube Audio Library licence: YouTube only (CR-8).</summary>
    YouTubeAudioLibrary = 3,
}
