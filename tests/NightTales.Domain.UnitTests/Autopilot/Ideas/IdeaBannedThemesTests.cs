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
}
