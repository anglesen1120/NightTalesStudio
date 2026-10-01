using System.Text;
using NightTales.Infrastructure.Security;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Security;

/// <summary>
/// The RFC 4648 base32 codec of 09.10.4-T1 (BD4): the alphabet authenticator applications accept for the
/// <c>otpauth</c> secret.
/// </summary>
/// <remarks>
/// The codec is pinned to a published vector rather than to a round-trip through itself, so a change to the
/// alphabet or to the character values cannot pass by encoding and decoding with the same wrong table:
/// RFC 6238's Appendix B secret <c>12345678901234567890</c> has the well-known base32 form below, and it
/// must produce the RFC 6238 six-digit code.
/// </remarks>
public sealed class Base32Tests
{
    /// <summary>RFC 6238's Appendix B key, base32-encoded: ASCII <c>12345678901234567890</c>.</summary>
    private const string RfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    private static readonly byte[] RfcKey = Encoding.ASCII.GetBytes("12345678901234567890");

    [Fact]
    public void Alphabet_IsRfc4648()
    {
        Base32.Alphabet.ShouldBe("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567");
        Base32.Alphabet.ShouldNotContain('0');
        Base32.Alphabet.ShouldNotContain('1');
        Base32.Alphabet.ShouldNotContain('8');
        Base32.Alphabet.ShouldNotContain('9');
    }

    [Fact]
    public void Decode_OfThePublishedSecret_YieldsTheAsciiKey()
    {
        Base32.TryDecode(RfcSecret, out var key).ShouldBeTrue();

        key.ShouldBe(RfcKey, "the published base32 form must decode to the published ASCII key");
    }

    [Fact]
    public void Encode_OfTheAsciiKey_IsThePublishedSecret()
    {
        Base32.Encode(RfcKey).ShouldBe(RfcSecret);
    }

    [Fact]
    public void DecodedSecret_ReproducesTheRfc6238Vector()
    {
        Base32.TryDecode(RfcSecret, out var key).ShouldBeTrue();

        Rfc6238.Compute(key, 59L / Rfc6238.DefaultStepSeconds).ShouldBe(
            "287082",
            "the decoded secret must produce RFC 6238's six-digit vector, the end-to-end check");
    }

    [Fact]
    public void NewSecret_IsThirtyTwoAlphabetCharacters_AndRandom()
    {
        var first = Base32.NewSecret();
        var second = Base32.NewSecret();

        first.Length.ShouldBe(Base32.SecretCharacters);
        first.ShouldNotBe(second);
        first.ShouldAllBe(character => Base32.Alphabet.Contains(character, StringComparison.Ordinal));
    }

    [Fact]
    public void TryDecode_RejectsNonAlphabetCharacters_AndAcceptsCaseAndPadding()
    {
        Base32.TryDecode("ABCDEFG=", out _).ShouldBeTrue("padding is optional in the otpauth secret");
        Base32.TryDecode("abcdefgh", out _).ShouldBeTrue("decoding folds case");
        Base32.TryDecode("0189", out _).ShouldBeFalse("0, 1, 8 and 9 are outside RFC 4648's alphabet");
        Base32.TryDecode("-", out _).ShouldBeFalse();
        Base32.TryDecode(string.Empty, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryDecode_IntoASpan_ReportsTheLength()
    {
        Span<byte> buffer = stackalloc byte[Base32.SecretBytes];

        Base32.TryDecode(RfcSecret, buffer, out var written).ShouldBeTrue();

        written.ShouldBe(Base32.SecretBytes);
        buffer[..written].ToArray().ShouldBe(RfcKey);
    }
}
