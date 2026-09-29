namespace NightTales.Application.Common;

/// <summary>
/// The ETag values of the aggregates (09 README D15, 02 README D9) and the comparison the handlers use
/// to answer a stale <c>If-Match</c> with 412 and the resource's own conflict code.
/// </summary>
/// <remarks>
/// The values are strong ETags and opaque to clients: the prefix names the resource kind (<c>ep</c>,
/// <c>se</c>, <c>pu</c>) and the suffix is the value it was derived from (<c>xmin</c> for an aggregate
/// so that every write changes it). They are returned in the <c>ETag</c> header and in the DTO field
/// <c>etag</c>.
/// </remarks>
public static class ETags
{
    /// <summary>Episode ETag: <c>"ep-&lt;episodes.xmin&gt;"</c>.</summary>
    public static string Episode(uint xmin) => $"\"ep-{xmin}\"";

    /// <summary>Series ETag: <c>"se-&lt;series.xmin&gt;"</c> (<c>GET/PATCH /series/{id}</c>).</summary>
    public static string Series(uint xmin) => $"\"se-{xmin}\"";

    /// <summary>Publication ETag: <c>"pu-&lt;publications.xmin&gt;"</c> (06.2.1-T1).</summary>
    public static string Publication(uint xmin) => $"\"pu-{xmin}\"";

    /// <summary>
    /// A strong, exact comparison of an <c>If-Match</c> header against <paramref name="current"/>:
    /// only the identical quoted tag last seen by the client matches.
    /// </summary>
    /// <remarks>
    /// <c>*</c> does not match [DECISION]: the client must have read the row, so a star would let a
    /// caller overwrite a value it never saw. A weak tag (<c>W/"…"</c>) never matches either — weak
    /// validation is not enough for a lost-update check — and a comma-separated list is treated as a
    /// mismatch, because a D9/D15 request carries the one exact tag it read.
    /// </remarks>
    /// <param name="ifMatch">The <c>If-Match</c> header, as received; may be absent.</param>
    /// <param name="current">The current ETag of the resource, quotes included.</param>
    /// <returns><see langword="true"/> only when the header is exactly <paramref name="current"/>.</returns>
    public static bool Matches(string? ifMatch, string current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (string.IsNullOrEmpty(ifMatch) || string.Equals(ifMatch, "*", StringComparison.Ordinal))
        {
            return false;
        }

        if (ifMatch.StartsWith("W/", StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(ifMatch, current, StringComparison.Ordinal);
    }
}
