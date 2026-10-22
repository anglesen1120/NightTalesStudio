using System.Globalization;

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
    /// Series-autopilot ETag: <c>"sa-&lt;series.xmin&gt;"</c> (<c>GET/PUT /series/{id}/autopilot</c>, 02 README
    /// D9, 02.8.3 plan §10 open item 7).
    /// </summary>
    /// <remarks>
    /// The settings live on the <c>series</c> row, so the value comes from <c>series.xmin</c> — the same
    /// column <see cref="Series"/> names. The <c>sa</c> prefix is what keeps the two tags apart: the autopilot
    /// route and <c>PATCH /series/{id}</c> write different columns of one row, so a client must not be able to
    /// replay a <c>"se-…"</c> tag it read from the series route on this one, nor the other way round.
    /// </remarks>
    /// <param name="xmin">The series row's <c>xmin</c>.</param>
    /// <returns>The strong ETag, quotes included.</returns>
    public static string SeriesAutopilot(uint xmin) => $"\"sa-{xmin}\"";

    /// <summary>
    /// Fact-sheet ETag: <c>"fs-&lt;fact_sheets.id&gt;"</c> (02 README D9, 02.5.4-T1).
    /// </summary>
    /// <remarks>
    /// Unlike the aggregate ETags above it is derived from the version row's own id, not from
    /// <c>xmin</c>: a fact sheet is immutable — an edit creates the next version — so a fresh id is
    /// exactly what makes the tag change on every write, with no <c>xmin</c> read to race.
    /// </remarks>
    /// <param name="factSheetId">The version id the tag names.</param>
    /// <returns>The strong ETag, quotes included.</returns>
    public static string FactSheet(Guid factSheetId) =>
        $"\"fs-{factSheetId.ToString("D", CultureInfo.InvariantCulture)}\"";

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
