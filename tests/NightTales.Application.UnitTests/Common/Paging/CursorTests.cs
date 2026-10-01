using System.Globalization;
using System.Text;
using NightTales.Application.Common.Paging;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Common.Paging;

/// <summary>
/// The shared keyset cursor (09 README D14, 09.4.1 plan §Design step 2): a value that survives the
/// round trip byte for byte, and a decoder that refuses everything else — an accepted garbage cursor
/// would silently answer the wrong page instead of 400.
/// </summary>
public sealed class CursorTests
{
    [Fact]
    public void Cursor_EncodeDecode_RoundTrips_AndRejectsGarbage()
    {
        var cursor = new Cursor(
            new DateTimeOffset(2026, 10, 1, 9, 30, 15, 250, TimeSpan.FromHours(7)),
            Guid.CreateVersion7());

        var encoded = cursor.Encode();

        encoded.ShouldNotContain('+');
        encoded.ShouldNotContain('/');
        encoded.ShouldNotContain('=');

        Cursor.TryDecode(encoded, out var decoded).ShouldBeTrue();
        decoded.ShouldBe(cursor);

        Cursor.TryDecode("x", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("!!!")]
    [InlineData("___")]
    [InlineData("not-base-64url-json!")]
    [InlineData("e30")]                                     // "{}" — no members at all
    [InlineData("eyJjIjoiMjAyNi0xMC0wMSIsImkiOiJ4In0")]    // a date, but an unparsable id
    [InlineData("eyJjIjpudWxsLCJpIjpudWxsfQ")]                    // {"c":null,"i":null}
    [InlineData("eyJDIjoiMjAyNi0xMC0wMVQwOTowMDowMC4wMDAwMDAwKzAwOjAwIiwiaSI6IjBhOTlhYmE0LWJjYWItNDNiYS05NTYyLWIxY2Q2MzA0MGQyMyJ9")] // correct shape, wrong member casing
    public void Cursor_TryDecode_RejectsAnythingThatIsNotACursor(string? value)
    {
        Cursor.TryDecode(value, out var cursor).ShouldBeFalse();
        cursor.ShouldBe(default);
    }

    [Fact]
    public void Cursor_TryDecode_RejectsAnEmptyIdAndAnUnparsableInstant()
    {
        // Two payloads a lenient decoder would accept: the id is not a row (Guid.Empty), and the
        // instant is not the "O" format this encoder writes. Either would place the caller at the wrong
        // offset instead of answering 400.
        Cursor.TryDecode(Encode("2026-10-01T09:30:00.0000000+00:00", Guid.Empty.ToString("D")), out _)
            .ShouldBeFalse();
        Cursor.TryDecode(Encode("2026-10-01", Guid.CreateVersion7().ToString("D")), out _)
            .ShouldBeFalse();
    }

    [Fact]
    public void Cursor_TryDecode_RejectsAnOversizedAndTruncatedPayload()
    {
        // A 4 KiB blob padded into a valid-looking payload, and a real cursor with its tail cut
        // off: neither may be treated as "no cursor" (the filter is the shape, not the size).
        var padded = Encode(
            "2026-10-01T09:30:00.0000000+00:00",
            Guid.CreateVersion7().ToString("D") + new string('x', 4096));
        Cursor.TryDecode(padded, out _).ShouldBeFalse();

        var truncated = new Cursor(
                new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero),
                Guid.CreateVersion7())
            .Encode();
        Cursor.TryDecode(truncated[..^8], out _).ShouldBeFalse("a cut-off cursor is not the cursor");
    }

    [Fact]
    public void Cursor_TryDecode_NormalizesAnOffsetToUtc()
    {
        // SEC-0941-01: Npgsql refuses a non-zero offset on a `timestamptz` parameter, so a client that
        // encodes its own offset must be normalized here or every consumer answers 500.
        var instant = new DateTimeOffset(2026, 10, 1, 2, 30, 0, TimeSpan.FromHours(7));
        var id = Guid.CreateVersion7();

        Cursor.TryDecode(Encode(instant.ToString("O", CultureInfo.InvariantCulture), id.ToString("D")), out var cursor)
            .ShouldBeTrue();

        cursor.CreatedAt.Offset.ShouldBe(TimeSpan.Zero);
        cursor.CreatedAt.ShouldBe(instant.ToUniversalTime());
        cursor.Id.ShouldBe(id);
    }

    /// <summary>Builds the wire payload by hand, so the decoder is fed values the encoder cannot emit.</summary>
    private static string Encode(string createdAt, string id)
    {
        var json = $"{{\"c\":\"{createdAt}\",\"i\":\"{id}\"}}";

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
