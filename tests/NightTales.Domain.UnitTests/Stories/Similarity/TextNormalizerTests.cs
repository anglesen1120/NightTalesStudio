using System.Diagnostics;
using System.Text;
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

    [Theory]
    [InlineData("\u00AD")]
    [InlineData("\u200B\u200C\u200D\u200E\u200F")]
    [InlineData("\u202A\u202B\u202C\u202D\u202E")]
    [InlineData("\u2060\u2061\u2062\u2063\u2064")]
    [InlineData("\u2066\u2067\u2068\u2069\u206A\u206B\u206C\u206D\u206E\u206F")]
    [InlineData("\uFEFF")]
    [InlineData("\uFE00\uFE0F")]
    [InlineData("\U000E0100\U000E01EF")]
    public void Normalize_InvisibleCharactersInsideWord_AreDeleted(string invisible) =>
        TextNormalizer.Normalize("qu" + invisible + "ick", Language.En).ShouldBe("quick");

    [Fact]
    public void Normalize_AllVariationSelectors_AreDeleted()
    {
        var selectors = new StringBuilder();
        for (var scalar = 0xFE00; scalar <= 0xFE0F; scalar++)
        {
            selectors.Append((char)scalar);
        }

        for (var scalar = 0xE0100; scalar <= 0xE01EF; scalar++)
        {
            selectors.Append(char.ConvertFromUtf32(scalar));
        }

        TextNormalizer.Normalize("qu" + selectors + "ick", Language.En).ShouldBe("quick");
    }

    [Fact]
    public void Normalize_PrivateUseAndUnassignedScalars_RetainOpaqueTokenPolicy() =>
        TextNormalizer.Normalize("\uE000 \u0378 \U000F0000 \U00100000", Language.En)
            .ShouldBe("\uE000 \u0378 \U000F0000 \U00100000");

    [Theory]
    [InlineData('\u0301')]
    [InlineData('\u0903')]
    [InlineData('\u20DD')]
    public void Normalize_CombiningMarkCap_IsThirtyAndRestartsAfterBase(char mark)
    {
        var bounded = "x" + new string(mark, 30);
        TextNormalizer.Normalize(bounded + mark + "y" + new string(mark, 31), Language.En)
            .ShouldBe(TextNormalizer.Normalize(bounded + "y" + new string(mark, 30), Language.En));
        TextNormalizer.Normalize(new string(mark, 31), Language.En)
            .ShouldBe(TextNormalizer.Normalize(new string(mark, 30), Language.En));
    }

    [Fact]
    public void Normalize_DeletedFormatCharacters_DoNotResetCombiningMarkCap()
    {
        var marks = string.Concat(Enumerable.Repeat("\u0301\u200B", 100));
        TextNormalizer.Normalize("x" + marks, Language.En)
            .ShouldBe(TextNormalizer.Normalize("x" + new string('\u0301', 30), Language.En));
    }

    [Fact]
    public void Normalize_LongAlternatingCombiningRun_IsBoundedAndFast()
    {
        var characters = new char[300_000];
        characters[0] = 'a';
        for (var i = 1; i < characters.Length; i++)
        {
            characters[i] = i % 2 == 1 ? '\u0301' : '\u0316';
        }

        var text = new string(characters);
        var watch = Stopwatch.StartNew();
        var normalized = TextNormalizer.Normalize(text, Language.En);
        watch.Stop();
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
        normalized.Length.ShouldBeLessThanOrEqualTo(31);
        normalized.ShouldBe(TextNormalizer.Normalize(text[..31], Language.En));
        normalized.IsNormalized(NormalizationForm.FormC).ShouldBeTrue();
    }

    [Fact]
    public void Normalize_CompositionExcludedMarks_CanExpandSafely()
    {
        var text = string.Concat(Enumerable.Repeat("\u0344 ", 100));
        TextNormalizer.Normalize(text, Language.En)
            .ShouldBe(string.Join(' ', Enumerable.Repeat("\u0308\u0301", 100)));
    }
}
