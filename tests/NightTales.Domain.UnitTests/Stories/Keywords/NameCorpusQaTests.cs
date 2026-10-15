using System.Globalization;
using System.Text;
using NightTales.Domain.Stories.Keywords;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Keywords;

/// <summary>
/// QA of <c>NameCorpus</c> (03.2.2-T1): the hostile-input and boundary rows the task file's Tests list does not
/// cover — NFC/NFD and homoglyph forms, zero-width and ICU-refused code units, a Turkish current culture,
/// full-width letters, a 1 MB text; the two-consecutive-token match at every pair position of a three-token run,
/// punctuation, digits and apostrophes; the stop list's repeated head skip; and the reach of the CR-18 Title Case
/// guard, including the missed-name false negative reported by this QA pass.
/// </summary>
public sealed class NameCorpusQaTests
{
    /// <summary>The one vi/ASCII fixture: its run is <c>Nguyen Van An</c>.</summary>
    private static NameCorpus CaseCorpus => NameCorpus.From(["Vụ án Nguyễn Văn An"]);

    [Fact]
    public void Fold_NfcNfdAndHomoglyphForms_Agree()
    {
        const string nfc = "Đặng Thị Hồng";
        var nfd = nfc.Normalize(NormalizationForm.FormD);

        nfd.ShouldNotBe(nfc, "the fixture must really be decomposed, or the row proves nothing");
        NameCorpus.Fold(nfd).ShouldBe("dang thi hong");
        NameCorpus.Fold(nfc).ShouldBe(NameCorpus.Fold(nfd));

        // "đ" (U+0111) and a "d" carrying the combining dot below (U+0323) are the same letter to a reader.
        NameCorpus.FoldTokens("d\u0323").ShouldBe(["d"]);
        NameCorpus.FoldTokens("\u0111").ShouldBe(["d"]);
        NameCorpus.Fold("\u0110").ShouldBe("d");

        // A base letter with marks, and a text of marks only.
        NameCorpus.Fold("a\u0301\u0323\u0301").ShouldBe("a");
        NameCorpus.Fold("\u0301\u0323\u0301").ShouldBeEmpty();
        NameCorpus.FoldTokens("\u0301").ShouldBeEmpty();
    }

    [Fact]
    public void Fold_TurkishCurrentCulture_DoesNotChangeTheFold()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // In tr-TR a culture-sensitive ToLower() turns "I" into "ı"; the fold must stay invariant.
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            NameCorpus.Fold("ISTANBUL").ShouldBe("istanbul", "the fold is invariant, not current-culture");
            NameCorpus.Fold("\u0130STANBUL")
                .ShouldBe("istanbul", "U+0130 decomposes to I + U+0307 and the mark is dropped before casing");
            CaseCorpus.Matches("PORTRAIT OF VAN AN AT DAWN").ShouldBeTrue();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Fold_ZeroWidthNoncharacterAndSurrogateUnits_AreSafe()
    {
        // A format char (ZWSP, ZWNJ, BOM, soft hyphen) separates tokens instead of merging or hiding them.
        NameCorpus.FoldTokens("Nguyễn\u200BVăn An").ShouldBe(["Nguyen", "Van", "An"]);
        NameCorpus.FoldTokens("Nguyễn\u200DVăn An").ShouldBe(["Nguyen", "Van", "An"]);
        NameCorpus.FoldTokens("Nguyễn\uFEFFVăn An").ShouldBe(["Nguyen", "Van", "An"]);
        NameCorpus.Fold("Nguyễn\u00ADVăn An").ShouldBe("nguyen van an");

        // ICU refuses the noncharacters and the unpaired surrogates; the fold must report, never throw (02 D2).
        NameCorpus.Fold("Nguyễn\uFFFEVăn An").ShouldBe("nguyen van an");
        NameCorpus.Fold("Nguyễn\uFFFFVăn An").ShouldBe("nguyen van an");
        NameCorpus.Fold("Nguyễn\uFDD0Văn An").ShouldBe("nguyen van an");
        NameCorpus.Fold("Nguyễn\U0001FFFEVăn An").ShouldBe("nguyen van an", "U+1FFFE is a supplementary noncharacter");
        NameCorpus.Fold("Nguyễn\uD800Văn An").ShouldBe("nguyen van an");
        NameCorpus.Fold("Nguyễn\uDC00Văn An").ShouldBe("nguyen van an");
        NameCorpus.Fold("\uFFFF").ShouldBeEmpty();
        NameCorpus.Fold("\U0001FFFE").ShouldBeEmpty();
    }

    [Fact]
    public void Fold_EmojiAndSupplementaryLetter_SeparateTokens()
    {
        // An emoji is two UTF-16 units, neither a letter or a digit: it separates, and a run survives it.
        NameCorpus.FoldTokens("Nguyễn \U0001F600 Văn An").ShouldBe(["Nguyen", "Van", "An"]);
        NameCorpus.Fold("a\U0001F600b").ShouldBe("a b");

        var withEmoji = NameCorpus.From(["Nguyễn \U0001F600 Văn An"]);

        withEmoji.Matches("portrait of van an at dawn").ShouldBeTrue("the emoji does not break the run");
    }

    [Fact]
    public void Fold_FullWidthLetters_AreNotFoldedToAscii()
    {
        // Residual, reported: full-width Latin is a letter, so it is kept and lower-cased as-is, never folded
        // to ASCII — a keyword written in full-width letters is not matched against an ASCII corpus.
        NameCorpus.FoldTokens("\uFF36\uFF41\uFF4E \uFF21\uFF4E").ShouldBe(["\uFF36\uFF41\uFF4E", "\uFF21\uFF4E"]);
        NameCorpus.Fold("\uFF36\uFF41\uFF4E \uFF21\uFF4E").ShouldBe("\uFF56\uFF41\uFF4E \uFF41\uFF4E");
        CaseCorpus.Matches("\uFF36\uFF41\uFF4E \uFF41\uFF4E at dawn").ShouldBeFalse();
    }

    [Fact]
    public void Fold_OneMegabyteTextAndSingleLongToken_Finish()
    {
        var singleToken = new string('a', 1_000_000);
        NameCorpus.Fold(singleToken).Length.ShouldBe(1_000_000, "one token of 1 M letters folds to itself");

        var filler = string.Join(' ', Enumerable.Repeat("lorem ipsum dolor sit amet", 40_000));
        filler.Length.ShouldBeGreaterThan(1_000_000);

        var corpus = NameCorpus.From([filler + " The victim was Nguyen Van An near the river."]);

        corpus.Matches("portrait of van an at dawn").ShouldBeTrue();
        corpus.Matches("portrait of lorem ipsum at dawn").ShouldBeFalse();
    }

    [Fact]
    public void From_NullAndEmptyInputs_NeverThrowAndNeverMatch()
    {
        NameCorpus.From(null!).Matches("portrait of van an").ShouldBeFalse();
        NameCorpus.From([]).Matches("portrait of van an").ShouldBeFalse();
        NameCorpus.From([null!, string.Empty, "   ", "\t\n"]).Matches("portrait of van an").ShouldBeFalse();

        CaseCorpus.Matches(null).ShouldBeFalse();
        CaseCorpus.Matches(string.Empty).ShouldBeFalse();
        CaseCorpus.Matches(" ").ShouldBeFalse();
        CaseCorpus.Matches("\u200B").ShouldBeFalse("a zero-width keyword folds to no token at all");
    }

    [Fact]
    public void Matches_EveryPairPositionOfAThreeTokenRun()
    {
        CaseCorpus.Matches("nguyen van").ShouldBeTrue("a two-token keyword matches the pair (the 02 rules reject it)");
        CaseCorpus.Matches("van an").ShouldBeTrue("the bigram alone matches");
        CaseCorpus.Matches("an van").ShouldBeFalse("the pair is ordered");
        CaseCorpus.Matches("portrait of nguyen van at dawn").ShouldBeTrue("tokens 1-2 of the run");
        CaseCorpus.Matches("portrait of van an at dawn").ShouldBeTrue("tokens 2-3 of the run");
        CaseCorpus.Matches("portrait of nguyen an at dawn").ShouldBeFalse("tokens 1 and 3 are not consecutive");
    }

    [Fact]
    public void Matches_StopListHead_IsSkippedRepeatedly()
    {
        NameCorpus.From(["The Old Mill"]).Matches("an old mill at dusk").ShouldBeTrue("the head stop word is skipped");
        NameCorpus.From(["In The Old Mill"]).Matches("an old mill at dusk").ShouldBeTrue("every head stop word is skipped");
        NameCorpus.From(["Police Station"]).Matches("police station at dawn").ShouldBeFalse();
        NameCorpus.From(["The Police Station"]).Matches("police station at dawn").ShouldBeFalse();
        NameCorpus.From(["Court House"]).Matches("court house at dawn").ShouldBeFalse();

        // A lower-case stop word splits the run, so the real name after it survives (the "Vu an Nguyen Van An" row).
        NameCorpus.From(["Vu an Nguyen Van An"]).Matches("portrait of van an at dawn").ShouldBeTrue();
        NameCorpus.From(["Vu An Nguyen Van An"]).Matches("portrait of van an at dawn").ShouldBeTrue();
    }

    [Fact]
    public void Matches_PunctuationDigitsAndApostrophes()
    {
        CaseCorpus.Matches("van-an at dawn").ShouldBeTrue("a hyphen separates into the same two tokens");
        CaseCorpus.Matches("van, an at dawn").ShouldBeTrue("a comma separates the same way");
        CaseCorpus.Matches("portrait of van an 24 at dawn").ShouldBeTrue("a digit is a token of its own");
        CaseCorpus.Matches("van an's hideout at dawn").ShouldBeTrue("the possessive adds a token after the pair");

        var obrien = NameCorpus.From(["Report on O'Brien"]);

        obrien.Matches("portrait of o brien at dawn").ShouldBeTrue("the apostrophe separates the two name parts");
        NameCorpus.From(["Her name was Nguyễn's"])
            .Matches("portrait of nguyen at dawn")
            .ShouldBeFalse("a possessive leaves one token: no run");
    }

    [Fact]
    public void From_Cr18Guard_BoundaryAndMissedName()
    {
        // The CR-18 guard counts tokens of >= 3 letters: 3 of 5 long tokens capitalised is exactly 60 %, so the
        // whole text is skipped as a headline and its name is lost. Reported as a defect (QA 2026-10-15): the
        // provider loads exactly these short sentence-case vi titles, and D11 makes them a name source.
        var skipped = NameCorpus.From(["Cảnh sát bắt Nguyễn Văn An"]);

        skipped.Matches("portrait of van an at dawn").ShouldBeFalse();
        skipped.Matches("portrait of nguyen van at dawn").ShouldBeFalse();

        // One more long token (3 of 6 = 50 %) stays under the guard: the same name is found.
        var kept = NameCorpus.From(["Cảnh sát bắt tên Nguyễn Văn An"]);

        kept.Matches("portrait of van an at dawn").ShouldBeTrue();
    }

    [Fact]
    public void From_GenericHeadlines_ThreeLongTokensAreBelowTheGuard()
    {
        // Noted, not a defect: a three-token English headline has fewer than the guard's four long tokens, so
        // every pair of it becomes a "name" and generic copy is flagged (D11 [DECISION]: more false positives
        // over missed names).
        NameCorpus.From(["Black Friday Deal"]).Matches("black friday at the market").ShouldBeTrue();
        NameCorpus.From(["The Old Mill"]).Matches("an old mill at dusk").ShouldBeTrue();

        // Six long tokens, all capitalised: the guard does skip the headline.
        NameCorpus.From(["The Night The Lights Went Out"])
            .Matches("the lights went out at midnight")
            .ShouldBeFalse();
    }
}
