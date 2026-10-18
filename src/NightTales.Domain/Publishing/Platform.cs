namespace NightTales.Domain.Publishing;

/// <summary>
/// The external platform a channel connection publishes to (06 README D2/D3): stored as its lower-case wire
/// value (<c>youtube</c>) and guarded by <c>ck_oauth_states_platform</c>. 07 adds <c>tiktok</c>.
/// </summary>
public enum Platform
{
    /// <summary>Google's YouTube Data API v3.</summary>
    YouTube,
}

/// <summary>
/// The wire spelling of <see cref="Platform"/> (06 README D3): the value the API path segment, the
/// <c>platform</c> column and its <c>CHECK</c> all use, so the three can never drift apart.
/// </summary>
public static class PlatformExtensions
{
    /// <summary>The stored and routed value of <see cref="Platform.YouTube"/>.</summary>
    private const string YouTubeWire = "youtube";

    /// <summary>The wire value of <paramref name="platform"/>.</summary>
    /// <param name="platform">A platform.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a declared platform.</exception>
    public static string ToWire(this Platform platform) => platform switch
    {
        Platform.YouTube => YouTubeWire,
        _ => throw new ArgumentOutOfRangeException(
            nameof(platform),
            platform,
            "No wire value is declared for this platform."),
    };

    /// <summary>
    /// Parses a platform wire value. The comparison ignores case, so a path segment of <c>YouTube</c> routes the
    /// same as the canonical <c>youtube</c>; anything else — a blank or unknown value included — is not a platform.
    /// </summary>
    /// <param name="wire">The value to parse, usually a path segment.</param>
    /// <param name="platform">The parsed platform, or <see langword="default"/> when the result is <see langword="false"/>.</param>
    public static bool TryParse(string? wire, out Platform platform)
    {
        if (string.Equals(wire, YouTubeWire, StringComparison.OrdinalIgnoreCase))
        {
            platform = Platform.YouTube;
            return true;
        }

        platform = default;
        return false;
    }
}
