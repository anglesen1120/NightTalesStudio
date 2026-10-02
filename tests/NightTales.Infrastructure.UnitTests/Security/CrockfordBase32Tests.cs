using NightTales.Infrastructure.Security;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Security;

/// <summary>
/// The Crockford base32 alphabet of 09.10.4-T1 (BD4): the recovery codes' alphabet, and nothing else.
/// </summary>
/// <remarks>
/// The authenticator secret no longer uses this alphabet — see <see cref="Base32Tests"/> — so only the
/// recovery code's generator is left here to pin.
/// </remarks>
public sealed class CrockfordBase32Tests
{
    [Fact]
    public void Alphabet_ExcludesConfusableCharacters()
    {
        CrockfordBase32.Alphabet.Length.ShouldBe(32);
        CrockfordBase32.Alphabet.ShouldBe("0123456789ABCDEFGHJKMNPQRSTVWXYZ");
        CrockfordBase32.Alphabet.ShouldNotContain('I');
        CrockfordBase32.Alphabet.ShouldNotContain('L');
        CrockfordBase32.Alphabet.ShouldNotContain('O');
        CrockfordBase32.Alphabet.ShouldNotContain('U');
    }

    [Fact]
    public void NewCode_IsTenAlphabetCharacters_AndRandom()
    {
        var first = CrockfordBase32.NewCode(10);
        var second = CrockfordBase32.NewCode(10);

        first.Length.ShouldBe(10);
        first.ShouldNotBe(second);
        first.ShouldAllBe(character => CrockfordBase32.Alphabet.Contains(character, StringComparison.Ordinal));
    }
}
