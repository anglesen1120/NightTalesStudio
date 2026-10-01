namespace NightTales.Api.Http;

/// <summary>
/// The <c>Catalog</c> section (09.4.2-T1 plan §3): how long a client and a shared cache may keep the two
/// catalog answers.
/// </summary>
/// <remarks>
/// The catalogs are identical for every user, so <c>public</c> caching is safe (plan §8). A re-seed
/// changes the ETag of the answer, so a stale cached body is revalidated and replaced within one TTL
/// without any purge hook (plan §11).
/// </remarks>
public sealed class CatalogOptions
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string SectionName = "Catalog";

    /// <summary>The <c>max-age</c> of <c>Cache-Control</c> on both catalog routes, in seconds.</summary>
    public int PublicCacheSeconds { get; set; } = 3600;
}
