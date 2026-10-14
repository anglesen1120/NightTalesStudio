using NightTales.Domain.Series.Bible;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Series.Bible;

/// <summary>
/// The comparison key of <see cref="BibleOpNormalizer"/> (02.6.3 plan §3.2, §4 U4): NFC, case-folded, whitespace
/// collapsed, punctuation dropped — and total, because a value the validator only reports must never make it
/// throw (02.6.3-T1 QA defect D2).
/// </summary>
public sealed class BibleOpNormalizerTests
{
    [Fact]
    public void Normalize_DropsUnpairedSurrogatesWithoutThrowing()
    {
        // A lone surrogate is not a Unicode scalar value, so ICU's NFC normalisation throws
        // ArgumentException on it. The normaliser drops the unpaired unit before normalising, because the
        // validator refuses the field with `invalid_structure` and must get there instead of a throw.
        //
        // An unpaired surrogate cannot be written as a source literal — the compiler replaces it with U+FFFD —
        // so every case is built at run time.
        var high = new string((char)0xD800, 1);
        var low = new string((char)0xDC00, 1);

        BibleOpNormalizer.Normalize("Bà Tư" + high).ShouldBe("bà tư");
        BibleOpNormalizer.Normalize(low + "Bà Tư").ShouldBe("bà tư");
        BibleOpNormalizer.Normalize($"Không{high} nói{low} tên").ShouldBe("không nói tên");
        BibleOpNormalizer.Normalize(high).ShouldBeEmpty("a lone surrogate carries no letter to compare");
    }

    [Fact]
    public void Normalize_KeepsAWellFormedSurrogatePair()
    {
        // U+104A0 (an Osmanya digit, a letter-or-digit) is one scalar value carried by two UTF-16 units: the
        // guard must not mistake either unit for an unpaired surrogate, or the value would vanish from the key.
        BibleOpNormalizer.Normalize("\U000104A0").ShouldBe("\U000104A0");
    }

    [Fact]
    public void Normalize_ComposesFoldsAndCollapses()
    {
        // The rules the validator's dedupe relies on: composed and decomposed spellings meet, case is folded,
        // runs of whitespace become one separator and punctuation is dropped — diacritics are kept, they are
        // letters.
        var decomposed = "Ba\u0300  Tu\u031b,";

        BibleOpNormalizer.Normalize(decomposed).ShouldBe(BibleOpNormalizer.Normalize("Bà Tư."));
        BibleOpNormalizer.Normalize("BÀ TƯ").ShouldBe("bà tư");
        BibleOpNormalizer.Normalize("ch1").ShouldBe("ch1");
        BibleOpNormalizer.Normalize("ch 1").ShouldBe("ch 1", "the separator is kept, so the two stay distinct");
        BibleOpNormalizer.Normalize("   ").ShouldBeEmpty();
        BibleOpNormalizer.Normalize(null).ShouldBeEmpty();
    }
}
