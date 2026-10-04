using NightTales.Application.Features.Stories;
using NightTales.Domain.Stories.Content;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories;

/// <summary>
/// The canonical no-op rule of the edit command (02.3.1-T1 [DECISION]): canonical equality ignores NFC form, line
/// endings and surrounding whitespace, and nothing else — list order and every other character are meaningful.
/// </summary>
public sealed class StoryContentComparerTests
{
    [Fact]
    public void CanonicalEquals_IdenticalDrafts_IsTrue() =>
        StoryContentComparer.CanonicalEquals(Draft(), Draft()).ShouldBeTrue();

    [Fact]
    public void CanonicalEquals_SurroundingWhitespaceOnAnyString_IsTrue()
    {
        var padded = Draft() with
        {
            Title = "  Căn nhà cuối hành lang \t",
            Hook = Hook() with { Text = "\n" + Hook().Text + "  ", Keywords = [" old house "] },
            Description = " Mô tả. ",
            Tags = [" truyện ma ", "tâm linh\n"],
        };

        StoryContentComparer.CanonicalEquals(padded, Draft()).ShouldBeTrue();
    }

    [Fact]
    public void CanonicalEquals_CrlfAndCrVersusLf_IsTrue()
    {
        var left = Draft() with { Paragraphs = [Paragraph("Dòng một.\r\nDòng hai.\rDòng ba.")] };
        var right = Draft() with { Paragraphs = [Paragraph("Dòng một.\nDòng hai.\nDòng ba.")] };

        StoryContentComparer.CanonicalEquals(left, right).ShouldBeTrue();
    }

    [Fact]
    public void CanonicalEquals_DecomposedVersusComposedVietnamese_IsTrue()
    {
        // "ệ" as one code point versus "e" + combining dot below + combining circumflex: the same text after NFC.
        var composed = Draft() with { Title = "Bí mật nhà \u1EC7" };
        var decomposed = Draft() with { Title = "Bí mật nhà e\u0323\u0302" };

        StoryContentComparer.CanonicalEquals(composed, decomposed).ShouldBeTrue();
    }

    [Fact]
    public void CanonicalEquals_ReorderedKeywords_IsFalse()
    {
        var left = Draft() with { Hook = Hook() with { Keywords = ["a", "b"] } };
        var right = Draft() with { Hook = Hook() with { Keywords = ["b", "a"] } };

        StoryContentComparer.CanonicalEquals(left, right).ShouldBeFalse();
    }

    [Fact]
    public void CanonicalEquals_ReorderedTagsOrParagraphs_IsFalse()
    {
        var tags = Draft() with { Tags = ["tâm linh", "truyện ma"] };
        var paragraphs = Draft() with { Paragraphs = [Paragraph("Hai."), Paragraph("Một.")] };

        StoryContentComparer.CanonicalEquals(tags, Draft()).ShouldBeFalse();
        StoryContentComparer.CanonicalEquals(paragraphs, Draft() with { Paragraphs = [Paragraph("Một."), Paragraph("Hai.")] })
            .ShouldBeFalse();
    }

    [Fact]
    public void CanonicalEquals_ChangedWordInsideAParagraph_IsFalse() =>
        StoryContentComparer.CanonicalEquals(
                Draft() with { Paragraphs = [Paragraph("Tiếng gõ vang lên.")] },
                Draft() with { Paragraphs = [Paragraph("Tiếng gõ vang xa.")] })
            .ShouldBeFalse();

    [Fact]
    public void CanonicalEquals_InnerWhitespaceChange_IsFalse() =>
        StoryContentComparer.CanonicalEquals(
                Draft() with { Title = "Căn nhà cuối" },
                Draft() with { Title = "Căn nhà  cuối" })
            .ShouldBeFalse();

    [Fact]
    public void CanonicalEquals_ChangedFactRefBeatOrStructure_IsFalse()
    {
        StoryContentComparer.CanonicalEquals(
                Draft() with { Hook = Hook() with { FactRefs = ["f1"] } },
                Draft() with { Hook = Hook() with { FactRefs = ["f2"] } })
            .ShouldBeFalse();
        StoryContentComparer.CanonicalEquals(
                Draft() with { Paragraphs = [Paragraph("Một.") with { Beat = "setup" }] },
                Draft() with { Paragraphs = [Paragraph("Một.") with { Beat = "twist" }] })
            .ShouldBeFalse();
        StoryContentComparer.CanonicalEquals(
                Draft() with { Structure = Structure() with { Setting = "nhà cũ" } },
                Draft())
            .ShouldBeFalse();
    }

    [Fact]
    public void CanonicalEquals_DifferentParagraphCountOrWarnings_IsFalse()
    {
        StoryContentComparer.CanonicalEquals(
                Draft() with { Paragraphs = [Paragraph("Một."), Paragraph("Hai.")] },
                Draft() with { Paragraphs = [Paragraph("Một.")] })
            .ShouldBeFalse();
        StoryContentComparer.CanonicalEquals(
                Draft() with { ContentWarnings = ["supernatural"] },
                Draft() with { ContentWarnings = [] })
            .ShouldBeFalse();
    }

    [Fact]
    public void CanonicalEquals_NullArgument_Throws()
    {
        Should.Throw<ArgumentNullException>(() => StoryContentComparer.CanonicalEquals(null!, Draft()));
        Should.Throw<ArgumentNullException>(() => StoryContentComparer.CanonicalEquals(Draft(), null!));
    }

    private static StorySegmentV1 Hook() => new("Đêm ấy mưa rất to.", ["old house"], []);

    private static StorySegmentV1 Paragraph(string text) => new(text, ["rain"], []);

    private static StoryStructureV1 Structure() =>
        new("truyện ma tâm linh", "ngôi nhà cũ", "một cuộc gọi", "người kể là nguyên nhân", "first_person");

    private static StoryContentV1 Draft() => new(
        "Căn nhà cuối hành lang",
        Hook(),
        [Paragraph("Một."), Paragraph("Hai.")],
        new StorySegmentV1("Anh trả nhà.", ["letter"], []),
        "Mô tả.",
        ["truyện ma", "tâm linh"],
        [],
        Structure());
}
