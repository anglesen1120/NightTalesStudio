using NightTales.Domain.Catalog;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Catalog;

/// <summary>
/// The <c>voices.v1</c> value rules of 04 README D9 (04.2.2-T2 Tests table). The boundary cases are the
/// ones where the Python reader and .NET disagree if the C# side is written the obvious way: the id
/// pattern is <em>Python</em> <c>isalnum</c> semantics (a connector such as <c>‿</c> is not a word
/// character there, a superscript digit is), and the length is counted in runes, not UTF-16 units.
/// </summary>
public sealed class VoiceCatalogRulesUnitTests
{
    [Fact]
    public void Slugify_HaiDang_ReturnsHaiDang()
    {
        VoiceCatalogRules.Slugify("Hải Đăng").ShouldBe("hai-dang");
    }

    [Fact]
    public void Slugify_NgocHuyen_ReturnsNgocHuyen()
    {
        VoiceCatalogRules.Slugify("Ngọc Huyền").ShouldBe("ngoc-huyen");
    }

    [Fact]
    public void Slugify_DStroke_MapsToD()
    {
        VoiceCatalogRules.Slugify("Đèn Đỏ").ShouldBe("den-do");
    }

    [Fact]
    public void ValidateEntry_Id64Runes_Ok_65Runes_VoiceIdInvalid()
    {
        VoiceCatalogRules.ValidateEntry(Entry(new string('a', VoiceCatalogRules.IdMaxRunes))).IsError.ShouldBeFalse();
        VoiceCatalogRules.ValidateEntry(Entry(new string('a', VoiceCatalogRules.IdMaxRunes + 1)))
            .FirstError.Code.ShouldBe(VoiceCatalogRules.VoiceIdInvalid);
    }

    [Fact]
    public void ValidateEntry_IdWithConnectorPunctuation_VoiceIdInvalid()
    {
        // U+203F CONNECTOR PUNCTUATION: .NET's `\w` accepts it (category Pc), Python's does not.
        VoiceCatalogRules.ValidateEntry(Entry("a‿b")).FirstError.Code.ShouldBe(VoiceCatalogRules.VoiceIdInvalid);
    }

    [Fact]
    public void ValidateEntry_IdWithSuperscriptDigit_Ok()
    {
        // U+00B2 SUPERSCRIPT TWO (category No): Python's str.isalnum() accepts it, .NET's `\w` does not.
        VoiceCatalogRules.ValidateEntry(Entry("Giọng²", slug: "giong")).IsError.ShouldBeFalse();
    }

    [Fact]
    public void ValidateLanguages_Revision0_NoActiveEn_Ok()
    {
        var entries = new List<VoiceCatalogEntry>
        {
            Entry("Hải Đăng"),
            Entry("Ngọc Huyền", language: "en", active: false),
        };

        VoiceCatalogRules.ValidateLanguages(revision: 0, entries).IsError.ShouldBeFalse();
        VoiceCatalogRules.ValidateLanguages(revision: 1, entries).FirstError.Code
            .ShouldBe(VoiceCatalogRules.CatalogInvalid);
    }

    [Fact]
    public void ValidateHeader_UppercaseHexCommit_Ok()
    {
        // Python's _COMMIT_RE is [0-9a-fA-F]{40} (QA-0422T2-1): a commit the proxy accepts must seed.
        VoiceCatalogRules.ValidateHeader("voices.v1", 1, "C1390ABBDB2EEDCDF58EAFB546966C06CE27AF71")
            .IsError.ShouldBeFalse();
    }

    [Fact]
    public void ValidateEntry_DisplayName40CodePoints_WithAstral_Ok()
    {
        // 39 ASCII + one astral character: 40 code points, but 41 UTF-16 units (QA-0422T2-2).
        var displayName = new string('A', 39) + "🎭";

        VoiceCatalogRules.ValidateEntry(Entry("Hải Đăng", displayName: displayName)).IsError.ShouldBeFalse();
        VoiceCatalogRules.ValidateEntry(Entry("Hải Đăng", displayName: displayName + "A"))
            .FirstError.Code.ShouldBe(VoiceCatalogRules.CatalogInvalid);
    }

    [Fact]
    public void ValidateEntry_PreviewVersionZeroPaddedAndUnicodeDigits_Ok()
    {
        // Python's regex is \d+ and int(group) >= 1: 'v01' is 1 and '\u0661\u0662' is 12 (QA-0422T2-3).
        VoiceCatalogRules.ValidateEntry(Entry("Hải Đăng", previewStorageKey: "voice-previews/hai-dang.v01.mp3"))
            .IsError.ShouldBeFalse();
        VoiceCatalogRules.ValidateEntry(Entry("Hải Đăng", previewStorageKey: "voice-previews/hai-dang.v١٢.mp3"))
            .IsError.ShouldBeFalse();
        VoiceCatalogRules.ValidateEntry(Entry("Hải Đăng", previewStorageKey: "voice-previews/hai-dang.v0.mp3"))
            .FirstError.Code.ShouldBe(VoiceCatalogRules.CatalogInvalid);
    }

    /// <summary>
    /// A valid entry whose id is the case under test; the slug defaults to the id's slug and the display
    /// name stays short, so a long id exercises the id rule and not the 40-character name rule.
    /// </summary>
    private static VoiceCatalogEntry Entry(
        string id,
        string? slug = null,
        string language = "vi",
        bool active = true,
        string displayName = "Giọng kiểm tra",
        string? previewStorageKey = null) =>
        new(id, slug ?? VoiceCatalogRules.Slugify(id), displayName, language, "female", [], active, previewStorageKey);
}
