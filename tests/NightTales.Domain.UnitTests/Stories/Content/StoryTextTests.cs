using NightTales.Domain.Stories.Content;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Content;

/// <summary>
/// <c>StoryText</c> (02.2.1-T1 Tests list, 02 README D4, 02 T72): the token rule, the sentence count and
/// NFC normalisation that the validator, the derived fields, 02.5.6 (shingles) and 03.2.2 share.
/// </summary>
public sealed class StoryTextTests
{
    [Fact]
    public void StoryText_TokenizeVietnameseSyllables_CountsEachSyllable()
    {
        // D4/V1: a vi token is a syllable, so five words are five tokens.
        string[] expected = ["Ngôi", "nhà", "cũ", "ven", "sông"];

        var tokens = StoryText.Tokenize("Ngôi nhà cũ ven sông");

        tokens.ShouldBe(expected);

        // Whitespace runs collapse, and an empty text has no token.
        StoryText.Tokenize("  hai   ba ").ShouldBe(["hai", "ba"]);
        StoryText.Tokenize(string.Empty).ShouldBeEmpty();
        StoryText.Tokenize(null).ShouldBeEmpty();

        // The failure-handling edge: one 500-character token with no whitespace is one token.
        StoryText.Tokenize(new string('x', 500)).Count.ShouldBe(1);
    }

    [Fact]
    public void StoryText_CountSentences_HandlesVietnameseTerminators()
    {
        StoryText.CountSentences("Chào. Bạn khỏe?").ShouldBe(2);

        // A trailing stretch with no terminator still counts as one sentence.
        StoryText.CountSentences("Chào. Bạn khỏe").ShouldBe(2);

        // A run of terminators is one sentence end.
        StoryText.CountSentences("Sao?! Thật ư.").ShouldBe(2);
        StoryText.CountSentences("Kể… rồi!").ShouldBe(2);

        StoryText.CountSentences("Chào").ShouldBe(1);
        StoryText.CountSentences("   ").ShouldBe(0);
        StoryText.CountSentences(string.Empty).ShouldBe(0);
        StoryText.CountSentences(null).ShouldBe(0);
    }

    [Fact]
    public void StoryText_NfcNormalisesCombiningMarks()
    {
        const string Decomposed = "nha\u0300";  // "nhà" with a combining grave accent (NFD)
        const string Composed = "nh\u00e0";     // "nhà" precomposed (NFC)

        StoryText.Normalize(Decomposed).ShouldBe(Composed);
        StoryText.Normalize(Decomposed).Length.ShouldBe(3);
        StoryText.Tokenize(Decomposed).Count.ShouldBe(StoryText.Tokenize(Composed).Count);
        StoryText.Tokenize(Decomposed).ShouldBe([Composed]);
    }

    /// <summary>The ordered-list marker edge (02 T72): "N. " at a line start is markup; "N." without the space, or digits mid-line, are prose.</summary>
    [Fact]
    public void StoryText_ListMarkers_RequireLineStartAndSpace()
    {
        StoryText.FindMarkup("Anh ghi lại.\n2. chiếc ghế thứ hai").ShouldBe("2. ");

        // A number followed by a dot mid-sentence, or "N." without a trailing space, is prose.
        StoryText.FindMarkup("Anh đọc trang 12. rồi ngủ").ShouldBeNull();
        StoryText.FindMarkup("Ngày 12.3 trời mưa").ShouldBeNull();
        StoryText.FindMarkup("12.tháng ba").ShouldBeNull();
    }

    /// <summary>A URL embedded without a space around it is still caught (02 T72).</summary>
    [Fact]
    public void StoryText_FindMarkup_CatchesBareWwwAndEmbeddedUrl()
    {
        StoryText.FindMarkup("xem www.example.com ngay").ShouldBe("www.");
        StoryText.FindMarkup("đọc thêm:https://x.test/y").ShouldBe("https://");
    }
}
