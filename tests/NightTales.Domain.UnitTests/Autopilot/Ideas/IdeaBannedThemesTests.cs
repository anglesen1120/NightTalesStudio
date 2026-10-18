using System.Text;
using NightTales.Domain.Autopilot.Ideas;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Autopilot.Ideas;

/// <summary>
/// The banned-theme backstop of 02.8.1-T2 (task Design "Policy" 1, Acceptance of
/// <c>ProposeAndPick_BannedThemeMatch_RejectsCandidate</c>): whole-word matching that keeps diacritics, so
/// the Vietnamese theme <c>tự tử</c> is banned and <c>tu tu</c> is not.
/// </summary>
/// <remarks>
/// The false positive the task's failure table names is a theme word inside another word; the false
/// negative is a normalisation that throws a diacritic away. Both have a case here.
/// </remarks>
public sealed class IdeaBannedThemesTests
{
    private const string Suicide = "tự tử";

    /// <summary>A theme matches as whole words only: <c>ma</c> is not <c>mau</c>.</summary>
    [Fact]
    public void IdeaBannedThemes_WholeWord_MatchesAndInsideAWordDoesNot()
    {
        var matched = IdeaBannedThemes.Match("Căn nhà bên sông có ma", ["ma"]);
        matched.Count.ShouldBe(1);
        matched[0].ShouldBe("ma");

        IdeaBannedThemes.Match("Cà mau thì không có gì", ["ma"]).ShouldBeEmpty();

        var phrase = IdeaBannedThemes.Match("Chuyện tự tử ở làng", [Suicide]);
        phrase.Count.ShouldBe(1);
        phrase[0].ShouldBe(Suicide);

        IdeaBannedThemes.Match("Chuyện tự tửu ở làng", [Suicide]).ShouldBeEmpty("tửu is not tử");
    }

    /// <summary>Diacritics are kept: <c>tu tu</c> must not ban <c>tự tử</c>, and vice versa.</summary>
    [Fact]
    public void IdeaBannedThemes_DiacriticsAreKept()
    {
        var matched = IdeaBannedThemes.Match("Chuyện TỰ TỬ ở làng", [Suicide]);
        matched.Count.ShouldBe(1, "case never decides a match");
        matched[0].ShouldBe(Suicide, "the theme is returned as the configuration wrote it");

        IdeaBannedThemes.Match("Chuyện tự tử ở làng", ["tu tu"]).ShouldBeEmpty();
        IdeaBannedThemes.Match("Chuyện tu tu ở làng", [Suicide]).ShouldBeEmpty();
    }

    /// <summary>A decomposed theme and a precomposed text are the same theme (and the other way round).</summary>
    [Fact]
    public void IdeaBannedThemes_NfcAndNfdFormsMatchEachOther()
    {
        var decomposed = Suicide.Normalize(NormalizationForm.FormD);
        decomposed.ShouldNotBe(Suicide);

        var precomposed = IdeaBannedThemes.Match("Chuyện " + Suicide + " ở làng", [decomposed]);
        precomposed.Count.ShouldBe(1);
        precomposed[0].ShouldBe(decomposed, "the matched theme is echoed in the form it was configured in");

        var composed = IdeaBannedThemes.Match("Chuyện " + decomposed + " ở làng", [Suicide]);
        composed.Count.ShouldBe(1);
        composed[0].ShouldBe(Suicide);
    }

    /// <summary>Blank and repeated themes, an empty text and an empty list never match anything.</summary>
    [Fact]
    public void IdeaBannedThemes_BlankRepeatedAndEmptyInputs()
    {
        IdeaBannedThemes.Match("Chuyện tự tử ở làng", []).ShouldBeEmpty();
        IdeaBannedThemes.Match("Chuyện tự tử ở làng", ["   "]).ShouldBeEmpty();
        IdeaBannedThemes.Match(string.Empty, [Suicide]).ShouldBeEmpty();
        IdeaBannedThemes.Match("Chuyện tự tử ở làng", [Suicide, Suicide]).Count.ShouldBe(1);
    }

    /// <summary>The text is punctuated and cased like model output; matching survives both.</summary>
    [Fact]
    public void IdeaBannedThemes_PunctuationAndCaseDoNotHideATheme()
    {
        var matched = IdeaBannedThemes.Match("“Tự tử!” — anh ta nói, rồi im.", [Suicide]);

        matched.Count.ShouldBe(1);
        matched[0].ShouldBe(Suicide);
    }

    /// <summary>
    /// A format character (Cf) or a combining mark (Mn) hidden inside a word never splits it (task failure-table
    /// evasion): <c>su\u200Bicide</c> is <c>suicide</c> and <c>kill\u0334er</c> is <c>killer</c>. The whole-word
    /// rule survives the absorption: the character cannot join two words into a theme either.
    /// </summary>
    [Fact]
    public void IdeaBannedThemes_InvisibleCharactersInsideAWordDoNotHideTheTheme()
    {
        var zeroWidthSpace = IdeaBannedThemes.Match("Chuyện su\u200Bicide ở làng", ["suicide"]);
        zeroWidthSpace.Count.ShouldBe(1, "a zero-width space (Cf) inside the word is ignored, not a separator");
        zeroWidthSpace[0].ShouldBe("suicide");

        var combiningMark = IdeaBannedThemes.Match("The kill\u0334er left", ["killer"]);
        combiningMark.Count.ShouldBe(1, "a combining mark (Mn) is absorbed by the word it follows");
        combiningMark[0].ShouldBe("killer");

        // A soft hyphen and a bidi mark are the same kind of ignorable character; a tag character too.
        IdeaBannedThemes.Match("mu\u00ADrder", ["murder"]).Count.ShouldBe(1);
        IdeaBannedThemes.Match("mur\u200Fder", ["murder"]).Count.ShouldBe(1);

        // Whole-word semantics are preserved: an invisible character cannot join two words into a theme,
        // and a theme that is a slice of a longer word still does not match.
        IdeaBannedThemes.Match("Cà ma\u200Bma", ["ma"]).ShouldBeEmpty("mama is still not ma");
        IdeaBannedThemes.Match("Chuyện su\u200Bicide ở làng", ["cide"]).ShouldBeEmpty("a slice of suicide is not suicide");
    }

    /// <summary>Normal Vietnamese text is untouched: both NFC and NFD still match (the marks compose, not absorb).</summary>
    [Fact]
    public void IdeaBannedThemes_NfcAndNfdVietnameseStillMatchWithTheJoinerRule()
    {
        IdeaBannedThemes.Match("Chuyện tự tử ở làng".Normalize(NormalizationForm.FormC), [Suicide]).Count.ShouldBe(1);
        IdeaBannedThemes.Match("Chuyện tự tử ở làng".Normalize(NormalizationForm.FormD), [Suicide]).Count.ShouldBe(1);
        IdeaBannedThemes.Match("Chuyện tự tử ở làng", [Suicide.Normalize(NormalizationForm.FormD)]).Count.ShouldBe(1);
    }
}
