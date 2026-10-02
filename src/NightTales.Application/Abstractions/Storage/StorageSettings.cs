namespace NightTales.Application.Abstractions.Storage;

/// <summary>
/// The <c>Storage:*</c> configuration of the blob store (09 README BD10, 11 D15/D21): the S3-compatible
/// gateway, its credentials and the buckets of this environment.
/// </summary>
/// <remarks>
/// <para>
/// The credentials are secret-manager references per 11 D15 (<c>nts/&lt;env&gt;/STORAGE_ACCESS_KEY</c>
/// and <c>..._SECRET_KEY</c>): they are never logged, never echoed in an error description and never
/// part of a metric tag. <c>AddStorage</c> refuses to start a host without them, the same way
/// <c>AddNightTalesAuth</c> refuses one without a signing key.
/// </para>
/// <para>
/// <see cref="ServiceUrl"/> is the endpoint the platform's own processes call
/// (<c>http://storage:8333</c> in dev, <c>https://storage-&lt;env&gt;.internal</c> in staging/prod, 11
/// D21/T77) and <see cref="PublicServiceUrl"/> is the browser-reachable one used **only** to presign
/// read URLs (10 CR-21): a presigned URL's signature covers the host, so it has to be signed for the
/// host the browser will call.
/// </para>
/// </remarks>
public sealed class StorageSettings
{
    /// <summary>The configuration section these options bind to.</summary>
    public const string SectionName = "Storage";

    /// <summary>The ceiling WF16 puts on a general read URL, in minutes (09 README BD16).</summary>
    public const int MaxPresignTtlMinutes = 15;

    /// <summary>The endpoint the platform's own processes use.</summary>
    public string ServiceUrl { get; set; } = string.Empty;

    /// <summary>
    /// The browser-reachable endpoint used only to presign read URLs; <see langword="null"/> means
    /// "same as <see cref="ServiceUrl"/>" (10 CR-21).
    /// </summary>
    public string? PublicServiceUrl { get; set; }

    /// <summary>
    /// The SigV4 signing region. The dev emulator ignores it, but the credential scope carries it, so a
    /// value that disagrees with the gateway's configuration is a misconfiguration rather than a
    /// failure (11 D15).
    /// </summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Whether every request uses the path-style endpoint (<c>/bucket/key</c>). True in every
    /// environment: the gateway has no per-bucket DNS (BD10 amended, 11 CR-15/T75).
    /// </summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>The access key (<c>nts/&lt;env&gt;/STORAGE_ACCESS_KEY</c>); a secret.</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>The secret key (<c>nts/&lt;env&gt;/STORAGE_SECRET_KEY</c>); a secret.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>The bucket names of this environment.</summary>
    public StorageBuckets Buckets { get; set; } = new();

    /// <summary>
    /// The configured lifetime of a general read URL, in minutes. The value is <em>clamped</em> to
    /// <see cref="MaxPresignTtlMinutes"/> when read (<see cref="ReadUrlTtl"/>): a deployment that
    /// configures a longer window still hands out a URL that honours the WF16 ceiling, instead of
    /// refusing to start.
    /// </summary>
    public int PresignTtlMinutes { get; set; } = MaxPresignTtlMinutes;

    /// <summary>
    /// The lifetime of the <c>PULL_FROM_URL</c> presign of 07.3.1-T1, in minutes. It is a separate
    /// ceiling (1–120) because the social platform fetches that URL itself, long after the request
    /// (07 CR-4); it is never mixed with the general 1–15 one.
    /// </summary>
    public int TikTokPresignTtlMinutes { get; set; } = 120;

    /// <summary>
    /// The key of the readiness marker object. It lives in the <em>media</em> bucket because the
    /// application key has no write on <c>nt-public-&lt;env&gt;</c> (11 D20 H9).
    /// </summary>
    public string HealthMarkerKey { get; set; } = "_health/storage-ready.txt";

    /// <summary>The interval of the storage health prober, in seconds; readiness reads a cache, never S3.</summary>
    public int HealthProbeSeconds { get; set; } = 30;

    /// <summary>The endpoint a presigned read URL is signed for (10 CR-21).</summary>
    public string PresignServiceUrl => string.IsNullOrWhiteSpace(PublicServiceUrl) ? ServiceUrl : PublicServiceUrl;

    /// <summary>The effective lifetime of a general read URL, clamped to the WF16 ceiling.</summary>
    public TimeSpan ReadUrlTtl => TimeSpan.FromMinutes(Math.Clamp(PresignTtlMinutes, 1, MaxPresignTtlMinutes));
}
