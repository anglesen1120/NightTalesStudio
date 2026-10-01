using System.Security.Cryptography;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The RFC 4648 base32 codec of 09 README BD4: the alphabet an authenticator application accepts for the
/// <c>secret</c> parameter of an <c>otpauth://</c> URI.
/// </summary>
/// <remarks>
/// <para>
/// Crockford's alphabet (<see cref="CrockfordBase32"/>) is <em>not</em> interchangeable with this one: it
/// maps <c>0</c> and <c>1</c> where RFC 4648 maps <c>O</c> and <c>I</c>, and it gives equal characters
/// different values, so a secret encoded with one decodes to a different key under the other. The
/// authenticator secret therefore uses this alphabet and Crockford stays the recovery codes' alphabet only.
/// </para>
/// <para>
/// Encoding is unpadded: a 20-byte secret (160 bits) is a whole number of five-bit characters, so it
/// encodes to exactly 32 characters and the <c>otpauth</c> URI carries no <c>=</c> (the key-URI format's
/// convention). Decoding accepts that form and the padded form, tolerates lower case, and rejects anything
/// that is not a canonical encoding: a character outside the alphabet, padding that is not one trailing
/// run, a length no whole number of bytes produces, non-zero residual bits, and a text that decodes to no
/// byte.
/// </para>
/// </remarks>
public static class Base32
{
    /// <summary>The RFC 4648 alphabet, in its canonical order.</summary>
    public const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>The bits one character carries.</summary>
    public const int BitsPerCharacter = 5;

    /// <summary>The entropy of one authenticator secret (BD4: 20 bytes).</summary>
    public const int SecretBytes = 20;

    /// <summary>The characters that entropy encodes to (20 × 8 / 5 = 32).</summary>
    public const int SecretCharacters = SecretBytes * 8 / BitsPerCharacter;

    /// <summary>Encodes <paramref name="bytes"/> as RFC 4648 base32, without padding.</summary>
    /// <param name="bytes">The bytes to encode.</param>
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var characters = new char[((bytes.Length * 8) + BitsPerCharacter - 1) / BitsPerCharacter];
        var written = 0;
        var buffer = 0;
        var bits = 0;

        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;

            while (bits >= BitsPerCharacter)
            {
                characters[written++] = Alphabet[(buffer >> (bits - BitsPerCharacter)) & 31];
                bits -= BitsPerCharacter;
            }
        }

        if (bits > 0)
        {
            characters[written++] = Alphabet[(buffer << (BitsPerCharacter - bits)) & 31];
        }

        return new string(characters, 0, written);
    }

    /// <summary>
    /// Decodes RFC 4648 base32 into <paramref name="destination"/>, folding lower case and accepting the
    /// optional trailing <c>=</c> padding.
    /// </summary>
    /// <param name="text">The text to decode.</param>
    /// <param name="destination">The buffer to write into; its length bounds the decoded value.</param>
    /// <param name="written">The bytes written when the answer is <see langword="true"/>.</param>
    /// <returns>
    /// <see langword="false"/> for a character outside the alphabet, for an empty text, for padding that is
    /// not a single trailing run, for a length that is not the canonical encoding of a whole number of
    /// bytes, for non-zero residual (padding) bits, for a decode that yields no byte, or when the value
    /// does not fit <paramref name="destination"/>.
    /// </returns>
    public static bool TryDecode(ReadOnlySpan<char> text, Span<byte> destination, out int written)
    {
        written = 0;

        if (text.IsEmpty)
        {
            return false;
        }

        // Padding may appear only as one trailing run (RFC 4648 §6); interior `=` is rejected.
        var paddingAt = text.IndexOf('=');
        var data = paddingAt < 0 ? text : text[..paddingAt];
        if (paddingAt >= 0)
        {
            foreach (var character in text[paddingAt..])
            {
                if (character != '=')
                {
                    return false;
                }
            }
        }

        if (data.IsEmpty || !IsCanonicalLength(data.Length))
        {
            return false;
        }

        var buffer = 0;
        var bits = 0;
        var count = 0;

        foreach (var character in data)
        {
            if (!TryValue(character, out var value))
            {
                return false;
            }

            buffer = (buffer << BitsPerCharacter) | value;
            bits += BitsPerCharacter;

            if (bits >= 8)
            {
                if (count >= destination.Length)
                {
                    return false;
                }

                destination[count++] = (byte)((buffer >> (bits - 8)) & 0xFF);
                bits -= 8;
            }
        }

        // The bits left over are the encoding's zero padding: a non-zero value means the text is not the
        // canonical encoding of the bytes it decodes to.
        if (bits > 0 && (buffer & ((1 << bits) - 1)) != 0)
        {
            return false;
        }

        if (count == 0)
        {
            return false;
        }

        written = count;

        return true;
    }

    /// <summary>Decodes RFC 4648 base32 into a fresh array, folding lower case and skipping padding.</summary>
    /// <param name="text">The text to decode.</param>
    /// <param name="bytes">The decoded bytes when the answer is <see langword="true"/>.</param>
    /// <returns><see langword="false"/> for a character outside the alphabet or an empty text.</returns>
    public static bool TryDecode(string text, out byte[] bytes)
    {
        bytes = [];

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var buffer = new byte[(text.Length * BitsPerCharacter / 8) + 1];
        if (!TryDecode(text.AsSpan(), buffer, out var written))
        {
            return false;
        }

        bytes = buffer[..written];

        return true;
    }

    /// <summary>A fresh 20-byte secret as 32 RFC 4648 base32 characters (BD4).</summary>
    public static string NewSecret() => Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>
    /// Whether <paramref name="length"/> data characters are the canonical encoding of a whole number of
    /// bytes: 1..5 bytes encode to 2, 4, 5, 7 and 8 characters, so the length modulo 8 is always one of
    /// 0, 2, 4, 5 or 7 (never 1, 3 or 6).
    /// </summary>
    private static bool IsCanonicalLength(int length) => (length % 8) is 0 or 2 or 4 or 5 or 7;

    /// <summary>The five bits of one character (case-insensitive); <c>false</c> outside the alphabet.</summary>
    private static bool TryValue(char character, out int value)
    {
        var upper = char.ToUpperInvariant(character);

        value = upper switch
        {
            >= 'A' and <= 'Z' => upper - 'A',
            >= '2' and <= '7' => upper - '2' + 26,
            _ => -1,
        };

        return value >= 0;
    }
}
