using NightTales.Domain.Common;
using NightTales.Domain.Stories.Similarity;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Similarity;

public sealed class TextNormalizerTests
{
    [Fact]
    public void Normalize_VietnameseDiacritics_AreKept() =>
        TextNormalizer.Normalize("“Tôi đã đi — về nhà”", Language.Vi).ShouldBe("tôi đã đi về nhà");

    [Fact]
    public void Normalize_VietnameseQuotes_AreStripped() =>
        TextNormalizer.Normalize("“nhà” ‘cửa’ «đêm» …", Language.Vi).ShouldBe("nhà cửa đêm");

    [Fact]
    public void Normalize_Dashes_AreStripped() =>
        TextNormalizer.Normalize("đi—về – nhà-cũ", Language.Vi).ShouldBe("đi về nhà cũ");

    [Fact]
    public void Normalize_MultipleSpaces_Collapse() =>
        TextNormalizer.Normalize("  Tôi   đã    đi  ", Language.Vi).ShouldBe("tôi đã đi");

    [Fact]
    public void Normalize_NfcAndDecomposedForms_AreEqual() =>
        TextNormalizer.Normalize("Tôi đã đi về nhà", Language.Vi)
            .ShouldBe(TextNormalizer.Normalize("Tôi đã đi về nhà", Language.Vi));

    [Fact]
    public void Normalize_TabsAndNewlines_BecomeSpace() =>
        TextNormalizer.Normalize("Tôi\tđã\r\nđi\u00a0về\u2028nhà", Language.Vi).ShouldBe("tôi đã đi về nhà");

    [Fact]
    public void Normalize_LoneSurrogatesAndControls_DoNotThrow() =>
        TextNormalizer.Normalize("a\0b\u0001c\u0085d\ud800e\udc00f", Language.En).ShouldBe("a b c d e f");

    [Fact]
    public void Normalize_SupplementaryLettersAndDigits_AreKept() =>
        TextNormalizer.Normalize("𐐀 123", Language.En).ShouldBe("𐐨 123");

    [Fact]
    public void Normalize_PunctuationSeparatesRatherThanJoiningWords() =>
        TextNormalizer.Normalize("a.b:c_d/e", Language.En).ShouldBe("a b c d e");
}
