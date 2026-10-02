using NightTales.Domain.Stories.Content;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Content;

/// <summary>
/// <c>DerivedStoryFields</c> (02.2.1-T1 Tests list, 02 README D3): the <c>body</c>, <c>keywords</c> and
/// <c>word_count</c> the domain recomputes on every save.
/// </summary>
public sealed class DerivedStoryFieldsTests
{
    /// <summary>"nhà cũ." with the accent decomposed, to prove the word count normalises before counting.</summary>
    private const string EndsDecomposed = "nha\u0300 cũ.";

    [Fact]
    public void DerivedFields_BodyJoinsHookParagraphsEnding_WithBlankLines()
    {
        var derived = DerivedStoryFields.From(Draft());

        // The body is the raw concatenation, blank line between the segments, and it is not normalised.
        derived.Body.ShouldBe("Mở đầu.\n\nĐoạn một.\n\nĐoạn hai.\n\n" + EndsDecomposed);
    }

    [Fact]
    public void DerivedFields_KeywordsConcatenateInOrder()
    {
        var derived = DerivedStoryFields.From(Draft());

        // hook ++ paragraphs ++ ending, in order, and duplicates are kept (D3 does not dedupe).
        string[] expected = ["opening", "duplicate", "second", "duplicate"];

        derived.Keywords.ShouldBe(expected);
    }

    [Fact]
    public void DerivedFields_WordCountCountsNfcTokens()
    {
        var derived = DerivedStoryFields.From(Draft());

        // 4 segments of 2 tokens each, the decomposed "nhà" counting as one.
        derived.WordCount.ShouldBe(8);
        derived.WordCount.ShouldBe(StoryText.Tokenize(derived.Body).Count);

        StoryText.Tokenize("nha\u0300").Count.ShouldBe(1);
        StoryText.Tokenize("nh\u00e0").Count.ShouldBe(1);
    }

    /// <summary>With no paragraphs the D3 formula degenerates to hook + two blank lines + ending.</summary>
    [Fact]
    public void DerivedFields_NoParagraphs_BodyIsHookBlankBlankEnding()
    {
        var draft = Draft() with { Paragraphs = [] };

        var derived = DerivedStoryFields.From(draft);

        derived.Body.ShouldBe("Mở đầu.\n\n\n\n" + EndsDecomposed);
        derived.Keywords.ShouldBe(["opening"]);
        derived.WordCount.ShouldBe(StoryText.Tokenize(derived.Body).Count);
    }

    private static StoryContentV1 Draft()
    {
        StorySegmentV1[] paragraphs =
        [
            new("Đoạn một.", ["duplicate", "second"], [], "setup"),
            new("Đoạn hai.", ["duplicate"], [], "twist"),
        ];

        return new StoryContentV1(
            "Tựa",
            new StorySegmentV1("Mở đầu.", ["opening"], []),
            paragraphs,
            new StorySegmentV1(EndsDecomposed, [], []),
            "Mô tả",
            ["tag"],
            [],
            new StoryStructureV1("sub", "setting", "opening", "twist", "first_person"));
    }
}
