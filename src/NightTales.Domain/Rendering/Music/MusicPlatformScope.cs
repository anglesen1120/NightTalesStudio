namespace NightTales.Domain.Rendering.Music;

/// <summary>
/// The platforms a catalog track may be published on (05 README D8, CR-8). Stored as <c>any</c> or
/// <c>youtube</c>; 07 blocks a <see cref="YouTube"/> track on any other platform.
/// </summary>
public enum MusicPlatformScope
{
    /// <summary>Any platform.</summary>
    Any = 1,

    /// <summary>YouTube only.</summary>
    YouTube = 2,
}
