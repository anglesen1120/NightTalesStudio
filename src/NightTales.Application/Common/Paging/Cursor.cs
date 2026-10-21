using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightTales.Application.Common.Paging;

/// <summary>
/// The opaque keyset cursor of 09 README D14: base64url of the JSON <c>{"c":&lt;createdAt&gt;,"i":&lt;id&gt;}</c>,
/// which identifies the last row a page returned. A page is read with
/// <c>ORDER BY created_at DESC, id DESC</c> and the next page continues strictly after this pair, so
/// inserting a row between two requests can neither duplicate nor skip one.
/// </summary>
/// <remarks>
/// Shared with 09.5.1 (episodes) and the admin lists (09.9.1-T1). The encoding is deliberately not
/// signed and carries no user id: it is only an offset into a list that the tenant filter has already
/// restricted to the caller (09 README D10), so a forged cursor can only move the caller around their
/// own rows. Base64url without padding, so the value survives a query string unchanged.
/// </remarks>
/// <param name="CreatedAt">The <c>created_at</c> of the last row of the page.</param>
/// <param name="Id">The <c>id</c> of the last row of the page; the tie-breaker of the ordering.</param>
public readonly record struct Cursor(DateTimeOffset CreatedAt, Guid Id)
{
    /// <summary>The cursor of the last row of a page, ready for <c>nextCursor</c>.</summary>
    /// <returns>The base64url-encoded payload.</returns>
    public string Encode()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new Payload(CreatedAt.ToString("O", CultureInfo.InvariantCulture), Id.ToString("D")));

        return Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Decodes a <c>?cursor=</c> value; a <see langword="null"/> or empty value is "no cursor", not a
    /// failure.
    /// </summary>
    /// <remarks>
    /// Everything that is not exactly an encoded cursor is refused — malformed base64url, bytes that are
    /// not JSON or not this object, a missing member, and a payload whose id is <c>Guid.Empty</c> (which
    /// is not a row, so accepting it would silently answer the wrong page).
    /// </remarks>
    /// <remarks>
    /// The instant is normalized to UTC here, once, for every consumer (09.5.1, the admin lists): a
    /// client can encode any offset it likes, and Npgsql refuses a non-zero offset on a
    /// <c>timestamptz</c> parameter, so a decoded <c>+07:00</c> cursor would otherwise reach the query
    /// as a 500 instead of a page (SEC-0941-01).
    /// </remarks>
    /// <param name="value">The raw query-string value.</param>
    /// <param name="cursor">The decoded cursor when this method returns <see langword="true"/>.</param>
    /// <returns>Whether <paramref name="value"/> is a cursor produced by <see cref="Encode"/>.</returns>
    public static bool TryDecode(string? value, out Cursor cursor)
    {
        cursor = default;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(Pad(value.Replace('-', '+').Replace('_', '/')));
        }
        catch (FormatException)
        {
            return false;
        }

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(bytes);
        }
        catch (JsonException)
        {
            return false;
        }

        if (payload is null
            || !Guid.TryParseExact(payload.Id, "D", out var id)
            || id == Guid.Empty
            || !DateTimeOffset.TryParseExact(
                payload.CreatedAt,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var createdAt))
        {
            return false;
        }

        cursor = new Cursor(createdAt.ToUniversalTime(), id);

        return true;
    }

    /// <summary>Restores the base64 padding <see cref="Encode"/> trimmed; the length must stay valid.</summary>
    private static string Pad(string value)
    {
        var remainder = value.Length % 4;
        return remainder switch
        {
            0 => value,
            2 => string.Concat(value, "=="),
            3 => string.Concat(value, "="),
            _ => "!invalid!",
        };
    }

    /// <summary>The wire payload; the member names are the frozen <c>{"c":…,"i":…}</c> of D14.</summary>
    private sealed record Payload(
        [property: JsonPropertyName("c")] string CreatedAt,
        [property: JsonPropertyName("i")] string Id);
}
