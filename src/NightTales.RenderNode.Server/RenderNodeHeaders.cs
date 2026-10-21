namespace NightTales.RenderNode.Server;

/// <summary>
/// The HTTP header and media-type names of the frozen interface (05.4.1-T2 §1–§2), spelled once because the
/// adapter, the mock, the tests and (through the contract) the backend client all use them.
/// </summary>
public static class RenderNodeHeaders
{
    /// <summary>The single authentication header: exactly one value on every route.</summary>
    public const string ApiKey = "x-api-key";

    /// <summary>The served artefact's sha256, taken from the measured check output — never re-hashed (§2).</summary>
    public const string ContentSha256 = "X-Content-SHA256";

    /// <summary>Seconds until a throttled or capacity-blocked request may be retried (60 for auth, 30 for capacity).</summary>
    public const string RetryAfter = "Retry-After";

    /// <summary>The video output's media type.</summary>
    public const string VideoContentType = "video/mp4";

    /// <summary>The subtitles output's media type (SubRip).</summary>
    public const string SubRipContentType = "application/x-subrip; charset=utf-8";

    /// <summary>The JSON media type of every request and response body.</summary>
    public const string JsonContentType = "application/json; charset=utf-8";
}
