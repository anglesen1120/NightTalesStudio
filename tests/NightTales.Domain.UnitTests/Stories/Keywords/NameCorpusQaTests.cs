using System.Globalization;
using System.Text;
using NightTales.Domain.Stories.Keywords;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Keywords;

/// <summary>
/// QA of <c>NameCorpus</c> (03.2.2-T1): the hostile-input and boundary rows the task file's Tests list does not
/// cover — NFC/NFD and homoglyph forms, zero-width and ICU-refused code units, a Turkish current culture,
/// full-width letters, a 1 MB text and the input caps; the two-consecutive-token match at every pair position of
/// a three-token run, punctuation, digits and apostrophes; the stop list's repeated head skip; and the reach of
/// the CR-18 Title Case guard, including the short mostly-capitalised title whose name is lost — a documented
/// residual recorded as an open item for the TL, not a defect of this task.
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
        NameCorpus.Fold(singleToken).Length.ShouldBe(1_000_000, "Fold folds exactly what it is given; the caps are on From");

        // From cuts each text at MaxTextChars before folding (P-1), so a 1 MB title stays linear and bounded.
        var filler = string.Join(' ', Enumerable.Repeat("lorem ipsum dolor sit amet", 40_000));
        filler.Length.ShouldBeGreaterThan(1_000_000);

        var corpus = NameCorpus.From(["The victim was Nguyen Van An near the river. " + filler]);

        corpus.Matches("portrait of van an at dawn").ShouldBeTrue("the run sits inside the first MaxTextChars");
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
    public void From_Cr18Guard_ShortMostlyCapitalisedTitle_IsADocumentedResidual()
    {
        // Documented residual (open item for the TL, not a defect of this task): the CR-18 guard counts tokens of
        // >= 3 letters, so 3 of 5 long tokens capitalised is exactly 60 % and the whole TITLE is skipped as a
        // headline and its name lost. The provider loads exactly these short sentence-case vi titles, and D11
        // makes them a name source. Titles keep this behaviour; the two-argument overload is what keeps the
        // majority-capitalised fact STATEMENTS (see NameCorpusTests).
        var skipped = NameCorpus.From(["Cảnh sát bắt Nguyễn Văn An"]);

        skipped.Matches("portrait of van an at dawn").ShouldBeFalse();
        skipped.Matches("portrait of nguyen van at dawn").ShouldBeFalse();

        // One more long token (3 of 6 = 50 %) stays under the guard: the same name is found.
        var kept = NameCorpus.From(["Cảnh sát bắt tên Nguyễn Văn An"]);

        kept.Matches("portrait of van an at dawn").ShouldBeTrue();

        // The same shape as a statement is not skipped at all (CR-18d).
        NameCorpus.From([], ["Cảnh sát bắt Nguyễn Văn An"]).Matches("portrait of van an at dawn").ShouldBeTrue();
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

    [Fact]
    public void From_TextLongerThanTheCap_IsCutAtMaxTextChars()
    {
        // P-1: each text is cut at MaxTextChars (2,000 UTF-16 units) before folding, so the corpus of one call is
        // bounded whatever the caller hands in. A 1 MB text is cut and the run inside the cap is still found.
        var filler = new string('x', 1_000_000);
        filler.Length.ShouldBeGreaterThan(NameCorpus.MaxTextChars);

        var inside = NameCorpus.From(["The victim was Nguyễn Văn An near Da Lat. " + filler]);

        inside.Matches("portrait of van an at dawn").ShouldBeTrue("the run sits in the first MaxTextChars units");
        inside.Matches("pine forest above da lat at dawn").ShouldBeTrue();

        // Documented residual: a name placed beyond MaxTextChars is never collected.
        var beyond = NameCorpus.From([filler + " The victim was Nguyễn Văn An near Da Lat."]);

        beyond.Matches("portrait of van an at dawn").ShouldBeFalse("the name lies past the cap and is cut away");

        // A text exactly at the cap is not cut at all.
        const string tail = " Nguyễn Văn An is";
        var exact = new string('x', NameCorpus.MaxTextChars - tail.Length) + tail;

        exact.Length.ShouldBe(NameCorpus.MaxTextChars);
        NameCorpus.From([exact]).Matches("portrait of van an at dawn").ShouldBeTrue("an at-cap text keeps its run");
    }

    [Fact]
    public void From_TextsBeyondMaxTexts_AreIgnored()
    {
        // P-1: at most MaxTexts texts are read per call, so the text at index MaxTexts is never folded.
        var texts = new List<string>(NameCorpus.MaxTexts + 1) { "Vụ án Nguyễn Văn An" };

        for (var index = 1; index < NameCorpus.MaxTexts; index++)
        {
            // Lower-case filler: no run, so it cannot mask a pair the cap should have dropped.
            texts.Add("mystery at the pier");
        }

        texts.Add("a suspect named Trần Văn Hùng was seen");
        texts.Count.ShouldBe(NameCorpus.MaxTexts + 1);

        var corpus = NameCorpus.From(texts);

        corpus.Matches("portrait of van an at dawn").ShouldBeTrue("the first text is read");
        corpus.Matches("portrait of tran van hung at dawn")
            .ShouldBeFalse("the text at index MaxTexts is past the cap and ignored");

        // The same ignored text is collected when it sits inside the cap.
        texts[NameCorpus.MaxTexts] = "mystery at the pier";
        texts[0] = "a suspect named Trần Văn Hùng was seen";

        NameCorpus.From(texts).Matches("portrait of tran van hung at dawn").ShouldBeTrue();

        // The budget is shared by the two-argument overload: titles first, then statements.
        var titles = Enumerable.Repeat("mystery at the pier", NameCorpus.MaxTexts).ToList();

        NameCorpus.From(titles, ["a suspect named Trần Văn Hùng was seen"])
            .Matches("portrait of tran van hung at dawn")
            .ShouldBeFalse("the statement is past the shared budget of MaxTexts texts");
    }

    [Fact]
    public void From_AstralCharacterStraddlingTheCap_IsDroppedWholeAndDoesNotThrow()
    {
        // P-1: the cut is on a surrogate-pair boundary, so an astral character whose high surrogate is the last
        // unit inside the cap is dropped whole — never left as a lone surrogate (nothing throws either way).
        var straddling = new string('x', NameCorpus.MaxTextChars - 1) + "\U0001F600" + " Nguyễn Văn An";
        straddling.Length.ShouldBeGreaterThan(NameCorpus.MaxTextChars);

        NameCorpus.From([straddling])
            .Matches("portrait of van an at dawn")
            .ShouldBeFalse("the cut lands before the pair and the name past it is cut away");

        // One unit shorter puts the boundary after the pair: the cut is not shifted and the name inside it is found.
        var insideName = "Nguyễn Văn An " + new string('x', NameCorpus.MaxTextChars - 14) + "\U0001F600";

        NameCorpus.From([insideName])
            .Matches("portrait of van an at dawn")
            .ShouldBeTrue("the whole pair is kept, so the cap is not pulled back a unit");
    }
}
