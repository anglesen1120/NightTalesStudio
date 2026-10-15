using System.Text;
using NightTales.Domain.Stories.Keywords;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Keywords;

/// <summary>
/// <c>NameCorpus</c> (03 README D11, F10, G4; 03.2.2-T1 Tests list): the fold of vi and en names, the run
/// extraction with its run-start stop list and Title Case guard, and the two-consecutive-token match — plus the
/// hostile-input rows (NFC vs NFD, a zero-width or noncharacter unit, null texts, a very long text).
/// </summary>
public sealed class NameCorpusTests
{
    [Theory]
    [InlineData("Nguyễn Văn An", "nguyen van an")]
    [InlineData("Đặng Thị Hồng", "dang thi hong")]
    public void Fold_VietnameseName_ToAscii(string text, string expected)
    {
        NameCorpus.Fold(text).ShouldBe(expected);
    }

    [Fact]
    public void Fold_DStroke_ToD()
    {
        // NFD does not decompose U+0111/U+0110, so the fold maps them explicitly.
        NameCorpus.Fold("đường").ShouldBe("duong");
        NameCorpus.Fold("Đà Lạt").ShouldBe("da lat");
    }

    [Fact]
    public void Fold_NfdAndNfc_AreEqual()
    {
        var nfc = "Nguyễn Văn An";
        var nfd = nfc.Normalize(NormalizationForm.FormD);

        nfd.ShouldNotBe(nfc, "the fixture must really be decomposed, or the row proves nothing");
        NameCorpus.Fold(nfd).ShouldBe(NameCorpus.Fold(nfc));
    }

    [Fact]
    public void Fold_PrecomposedDStrokeAndCombiningMark_AreEqual()
    {
        // "đ" and a "d" carrying a combining mark are the same letter to a reader and must fold alike.
        NameCorpus.FoldTokens("\u0111").ShouldBe(["d"]);
        NameCorpus.FoldTokens("d\u0323").ShouldBe(["d"]);
        NameCorpus.FoldTokens("\u0110").ShouldBe(["D"]);
        NameCorpus.Fold("\u0110").ShouldBe("d");
    }

    [Fact]
    public void Fold_FormatCharacterInName_KeepsBothTokens()
    {
        // A zero-width char is not a letter or a digit, so it separates tokens: dropping it instead would merge
        // "NguyễnVăn" into one token and hide the name from every later match.
        NameCorpus.FoldTokens("Nguyễn\u200BVăn An").ShouldBe(["Nguyen", "Van", "An"]);
        NameCorpus.FoldTokens("Vụ án Nguyễn\u200CVăn An").ShouldBe(["Vu", "an", "Nguyen", "Van", "An"]);
    }

    [Fact]
    public void Fold_NoncharacterAndUnpairedSurrogate_DoNotThrow()
    {
        // ICU refuses U+FFFE and an unpaired surrogate; the fold must report, never throw (02 D2).
        NameCorpus.Fold("Nguyễn\uFFFEVăn An").ShouldBe("nguyen van an");
        NameCorpus.Fold("a\uD800b").ShouldBe("a b");
        NameCorpus.Fold("\uFFFF").ShouldBeEmpty();
    }

    [Fact]
    public void From_RunOfTwoCapitalisedTokens()
    {
        var corpus = NameCorpus.From(["Vụ án Nguyễn Văn An"]);

        corpus.Matches("portrait of nguyen van at dawn").ShouldBeTrue();
        corpus.Matches("portrait of van an at dawn").ShouldBeTrue();
    }

    [Fact]
    public void From_SingleCapitalisedToken_NoRun()
    {
        // A run needs two capitalised tokens: a lone sentence-initial word is not a name.
        var corpus = NameCorpus.From(["Mystery at the pier"]);

        corpus.Matches("portrait of mystery at the pier").ShouldBeFalse();
    }

    [Theory]
    [InlineData("The Old Mill", "the old mill at dusk", true)]
    [InlineData("Police Station", "police station at dusk", false)]
    public void From_StopListStart_Skipped(string text, string keyword, bool matched)
    {
        // Skipping the head stop word leaves two tokens ("old mill"); after "police" only one remains.
        NameCorpus.From([text]).Matches(keyword).ShouldBe(matched);
    }

    [Fact]
    public void From_TitleCaseHeadline_Skipped()
    {
        var corpus = NameCorpus.From(["The Night The Lights Went Out", "Nobody Knew Why She Left"]);

        corpus.Matches("the lights went out at midnight").ShouldBeFalse();
        corpus.Matches("nobody knew why she left that night").ShouldBeFalse();
    }

    [Fact]
    public void From_SentenceCaseText_Kept()
    {
        // The main name source is a sentence-case fact statement, whose capitalised share is far below the guard.
        var corpus = NameCorpus.From(["The body of Nguyễn Văn An was found near Đà Lạt."]);

        corpus.Matches("portrait of van an at dawn").ShouldBeTrue();
        corpus.Matches("pine forest above da lat at dawn").ShouldBeTrue();
    }

    [Fact]
    public void From_GivenNameAn_Kept()
    {
        // "an" is not in the stop list: it is the English article and a common Vietnamese given name, and the
        // folded "án" of a case word would otherwise drop a real first name. Were it listed, the run would keep
        // one token only and the pair below would be missing.
        var corpus = NameCorpus.From(["Vụ án An Nguyễn"]);

        corpus.Matches("portrait of an nguyen at dawn").ShouldBeTrue();
    }

    [Fact]
    public void From_EnglishName_John_Smith()
    {
        var corpus = NameCorpus.From(["John Smith was arrested in Ohio last week"]);

        corpus.Matches("portrait of john smith at the station").ShouldBeTrue();
        corpus.Matches("portrait of smith at the station").ShouldBeFalse();
    }

    [Fact]
    public void From_NullTextsAndNullElements_YieldEmpty()
    {
        // Hostile input: a null sequence and a null element contribute nothing and never throw.
        NameCorpus.From(null!).Matches("portrait of van an").ShouldBeFalse();
        NameCorpus.From([null!, "   ", string.Empty]).Matches("portrait of van an").ShouldBeFalse();
    }

    [Fact]
    public void From_VeryLongText_FinishesAndFindsTheName()
    {
        // The fold and the run scan are linear in the text, so a source title far over any real limit is not a
        // quadratic trap (the statement limits of 02 bound the normal case; a fetched title does not).
        var filler = string.Join(' ', Enumerable.Repeat("lorem ipsum dolor sit amet consectetur", 15_000));
        var corpus = NameCorpus.From([filler + " The victim was Nguyễn Văn An near Da Lat."]);

        corpus.Matches("portrait of van an at dawn").ShouldBeTrue();
        corpus.Matches("portrait of lorem ipsum dolor at dawn").ShouldBeFalse();
    }

    [Fact]
    public void Matches_TwoConsecutiveTokens_Flags()
    {
        var corpus = NameCorpus.From(["Vụ án Nguyễn Văn An"]);

        corpus.Matches("portrait of van an at the market").ShouldBeTrue();
        corpus.Matches("van at night by the river").ShouldBeFalse();
    }

    [Fact]
    public void Matches_NonConsecutiveTokens_NotFlagged()
    {
        var corpus = NameCorpus.From(["Vụ án Nguyễn Văn An"]);

        corpus.Matches("van walking away from an empty market").ShouldBeFalse();
        corpus.Matches("nguyen standing beside van").ShouldBeFalse();
    }

    [Fact]
    public void Matches_SingleToken_NotFlagged()
    {
        var corpus = NameCorpus.From(["Vụ án Nguyễn Văn An"]);

        corpus.Matches("van").ShouldBeFalse();
    }

    [Fact]
    public void Matches_CaseAndDiacriticInsensitive()
    {
        var corpus = NameCorpus.From(["Vụ án Nguyễn Văn An"]);

        corpus.Matches("PORTRAIT OF VĂN AN AT DAWN").ShouldBeTrue();
        corpus.Matches("Ảnh của Nguyễn Văn An").ShouldBeTrue();
    }

    [Fact]
    public void Matches_EmptyCorpus_NeverFlags()
    {
        NameCorpus.Empty.Matches("portrait of van an at the market").ShouldBeFalse();
        NameCorpus.From([]).Matches("portrait of van an at the market").ShouldBeFalse();
    }

    [Fact]
    public void Matches_NullEmptyAndBlankKeyword_NotFlagged()
    {
        var corpus = NameCorpus.From(["Vụ án Nguyễn Văn An"]);

        corpus.Matches(null).ShouldBeFalse();
        corpus.Matches(string.Empty).ShouldBeFalse();
        corpus.Matches("   ").ShouldBeFalse();
    }

    [Fact]
    public void Matches_MatchedRun_ReportsTheRunTokenCount()
    {
        // The D11 Evidence is "run:<n tokens>", the run behind the pair that matched.
        var corpus = NameCorpus.From(["Vụ án Nguyễn Văn An", "Nạn nhân cuối cùng ở Đà Lạt"]);

        corpus.Matches("portrait of van an at dawn", out var nameRun).ShouldBeTrue();
        nameRun.ShouldBe(3, "the run is the three tokens Nguyen Van An");
        corpus.Matches("pine forest above da lat", out var placeRun).ShouldBeTrue();
        placeRun.ShouldBe(2, "the run is the two tokens Da Lat");
        corpus.Matches("van at night", out var none).ShouldBeFalse();
        none.ShouldBe(0);
    }
}
