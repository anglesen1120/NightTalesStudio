namespace NightTales.RenderNode.Server;

/// <summary>
/// The server core's configuration, bound from the <c>RenderNodeServer</c> section — the env names are
/// <c>RenderNodeServer__ApiKey</c> (= <c>ADAPTER_API_KEY</c>) and friends (11 README D5/D6, 05 D15 S3).
/// </summary>
/// <remarks>
/// The properties are settable (not <c>init</c>): options binding and the DI configure callback assign them
/// after construction, and <see cref="RenderNodeServerOptionsValidator"/> is the single place that decides
/// whether a bound set is usable (11.3.5-T1 deviation from the task file's <c>init</c>, which cannot bind).
/// </remarks>
public sealed class RenderNodeServerOptions
{
    /// <summary>The configuration section this type binds to (D6).</summary>
    public const string SectionName = "RenderNodeServer";

    /// <summary>The minimum key length in bytes: D15 S3 makes a shorter <c>ADAPTER_API_KEY</c> a startup exit.</summary>
    public const int MinimumApiKeyBytes = 32;

    /// <summary>The frozen default of §1: the largest valid body is ≈ 300 KB, the cap is 2 MiB (05 D15 S2).</summary>
    public const long DefaultMaxBodyBytes = 2_097_152;

    /// <summary><c>ADAPTER_API_KEY</c>. Required and at least <see cref="MinimumApiKeyBytes"/> bytes long.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// <c>ADAPTER_API_KEY_NEXT</c>, the rotation window of D15 S3. Null or empty disables the NEXT compare —
    /// it is never an empty-string match.
    /// </summary>
    public string? ApiKeyNext { get; set; }

    /// <summary>The per-endpoint request body cap; a larger body is <c>413 adapter_body_too_large</c> (S2).</summary>
    public long MaxBodyBytes { get; set; } = DefaultMaxBodyBytes;

    /// <summary>Auth failures per minute per source address before <c>429 adapter_auth_throttled</c> (S2).</summary>
    public int AuthFailuresPerMinute { get; set; } = 10;

    /// <summary>Non-terminal tasks the node accepts before <c>429 adapter_capacity_full</c> (F07/C10).</summary>
    public int MaxConcurrentTasks { get; set; } = 1;
}
