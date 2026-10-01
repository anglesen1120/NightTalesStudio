namespace NightTales.Infrastructure.Storage;

/// <summary>
/// The <c>Storage</c> section (09.4.2-T1, plan §3): the public bucket the catalog previews live in.
/// </summary>
/// <remarks>
/// The base URL is configuration, not a secret — it is the address of the read-only
/// <c>nt-public-&lt;env&gt;</c> bucket, and 11.2.2-T2 sets it per environment. It is empty by default
/// on purpose: a developer's host serves catalogs without previews, and the startup warning says so,
/// rather than inventing a URL that does not exist.
/// </remarks>
public sealed class StorageOptions
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string SectionName = "Storage";

    /// <summary>
    /// The absolute base URL of the public bucket, for example
    /// <c>https://nt-public-dev.example.test</c>; empty when the host has no public bucket.
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Whether a composed URL must be <c>https</c>. Ignored in Development, whose emulator may be
    /// <c>http://localhost:9000</c> (plan §3).
    /// </summary>
    public bool RequireHttps { get; set; } = true;
}
