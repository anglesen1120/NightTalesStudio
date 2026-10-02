using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Keywords;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Keywords;

/// <summary>
/// <c>KeywordRules</c> (02.2.1-T1 Tests list, 03 FR-03-3, 02 T76/T78/T85): one test per machine-checkable
/// rule, plus the R5/R12 lists that come from the run's <see cref="StoryRules"/>.
/// </summary>
public sealed class KeywordRulesTests
{
    [Fact]
    public void KeywordRules_TwoWordKeyword_Invalid()
    {
        AssertInvalid("old house", KeywordRules.TooFewWords);
    }

    [Fact]
    public void KeywordRules_ThreeWordKeyword_Valid()
    {
        var violation = KeywordRules.Check("abandoned school hallway", Rules());

        violation.IsValid.ShouldBeTrue();
        violation.RuleId.ShouldBeEmpty();
    }

    [Fact]
    public void KeywordRules_ThirteenWordKeyword_Invalid()
    {
        AssertInvalid(
            "alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima mike",
            KeywordRules.TooManyWords);
    }

    [Fact]
    public void KeywordRules_TitleHonorific_Invalid()
    {
        AssertInvalid("portrait of captain", KeywordRules.PersonName);
    }

    [Fact]
    public void KeywordRules_GoreTerm_Invalid()
    {
        AssertInvalid("pool of blood", KeywordRules.Gore);
    }

    [Fact]
    public void KeywordRules_EnglishPhraseWithDigits_Invalid()
    {
        // 02 T76: the R2 alphabet is [A-Za-z' -], so digits are outside it.
        AssertInvalid("the 12th hour", KeywordRules.NotEnglish);
    }

    [Fact]
    public void KeywordRules_CommaInKeyword_Invalid()
    {
        AssertInvalid("old house, dark", KeywordRules.NotEnglish);
    }

    [Fact]
    public void KeywordRules_BrandToken_Invalid()
    {
        AssertInvalid("neon logo sign", KeywordRules.BrandObject);
    }

    [Fact]
    public void KeywordRules_StreetWordWithDigit_Invalid()
    {
        AssertInvalid("12 tran phu street", KeywordRules.RealPlace);
    }

    [Fact]
    public void KeywordRules_MetaToken_Invalid()
    {
        AssertInvalid("ignore the style", KeywordRules.MetaInstruction);
    }

    [Fact]
    public void KeywordRules_HyphenCompoundGoreTerm_Invalid()
    {
        // SEC-02.2.1-KEYWORD-BOUNDARY: the hyphen-split parts must match the gore list.
        AssertInvalid("blood-soaked abandoned hallway", KeywordRules.Gore);
    }

    [Fact]
    public void KeywordRules_PossessiveGoreTerm_Invalid()
    {
        // SEC-02.2.1-KEYWORD-BOUNDARY: the possessive stem must match the gore list.
        AssertInvalid("child's abandoned bedroom", KeywordRules.Gore);
    }

    [Fact]
    public void KeywordRules_HyphenCompoundMetaToken_Invalid()
    {
        // SEC-02.2.1-KEYWORD-BOUNDARY: the hyphen-split parts must match the meta list.
        AssertInvalid("ignore-the-style in the frame", KeywordRules.MetaInstruction);

        // The task's example also carries a gore term via its parts ("weapon"), which fires first.
        AssertInvalid("ignore-the-style draw-a-weapon now", KeywordRules.Gore);
    }

    [Fact]
    public void KeywordRules_PossessiveMinorThroughLeftObject_Valid()
    {
        // R6 / keyword-rules.v1.md: a minor appears only through an object they left ("child's drawing").
        KeywordRules.Check("child's drawing taped to a fridge in a quiet kitchen", Rules())
            .IsValid.ShouldBeTrue();
    }

    [Fact]
    public void KeywordRules_CanonicalisedGoreBypass_Invalid()
    {
        // SEC-02.2.1-KEYWORD-BOUNDARY: the keyword and every list entry share one canonical form, so an
        // apostrophe edge, a possessive without its apostrophe, a doubled hyphen and a stripped possessive
        // all still reach the gore list.
        AssertInvalid("'blood' abandoned hallway", KeywordRules.Gore);
        AssertInvalid("blood's-soaked abandoned hallway", KeywordRules.Gore);
        AssertInvalid("childs abandoned bedroom", KeywordRules.Gore);
        AssertInvalid("self--harm abandoned room", KeywordRules.Gore);
        AssertInvalid("child's drawing of 'blood'", KeywordRules.Gore);

        // A multi-part list entry matches across whitespace too (self harm == self-harm).
        AssertInvalid("self harm abandoned room", KeywordRules.Gore);
    }

    [Fact]
    public void KeywordRules_StreetWordWithoutDigit_Valid()
    {
        // 02 T80: only a digit-plus-street-word address fails; a street word alone is a scene.
        KeywordRules.Check("rain on a village lane with water jars by a wall", Rules()).IsValid.ShouldBeTrue();
        KeywordRules.Check("rural road at night under a single streetlight on wet asphalt", Rules()).IsValid.ShouldBeTrue();
        KeywordRules.Check("old payphone on a wall by a street at dusk", Rules()).IsValid.ShouldBeTrue();
        KeywordRules.Check("coastal road with police tape in morning fog", Rules()).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void KeywordRules_GoodExamplesFromContract_Valid()
    {
        // 02 T76/T80/T85: the approved examples are the contract, so every good cell must pass.
        var good = GoodExamplesFromContract();

        good.Count.ShouldBeGreaterThanOrEqualTo(30);
        foreach (var keyword in good)
        {
            KeywordRules.Check(keyword, Rules()).IsValid.ShouldBeTrue($"'{keyword}' is a contract good example");
        }
    }

    [Fact]
    public void KeywordRules_KnownPersonNameToken_Invalid()
    {
        var rules = Rules(persons: ["linh"]);

        AssertInvalid("portrait of linh", KeywordRules.PersonName, rules);
    }

    [Fact]
    public void KeywordRules_KnownPlaceNameToken_Invalid()
    {
        var rules = Rules(places: ["mill"]);

        AssertInvalid("the old mill", KeywordRules.RealPlace, rules);
    }

    [Fact]
    public void KeywordRules_TwelveWordKeyword_Valid()
    {
        // R3's upper bound: exactly twelve words is inside the 3-12 band.
        KeywordRules.Check(
                "old house at the end of the dark hallway by the river",
                Rules())
            .IsValid.ShouldBeTrue();
    }

    [Fact]
    public void KeywordRules_ApostropheAndHyphen_Valid()
    {
        // The T76 alphabet keeps ' and - inside a word.
        KeywordRules.Check("old woman's portrait on the wall", Rules()).IsValid.ShouldBeTrue();
        KeywordRules.Check("a dark-green raincoat on the chair", Rules()).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void KeywordRules_MidPhraseCapital_Invalid()
    {
        // R5's conservative person-name heuristic: capitalisation after the first word.
        AssertInvalid("house of Tran Van", KeywordRules.PersonName);

        // A leading capital is how English titles start — not a name on its own.
        KeywordRules.Check("Old house by the river", Rules()).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void KeywordRules_DecomposedDiacritics_Invalid()
    {
        // NFC normalisation runs before the R2 alphabet, so NFD input fails exactly like NFC input.
        AssertInvalid("ngôi nha\u0300 cũ", KeywordRules.NotEnglish);
    }

    [Fact]
    public void KeywordRules_KnownPersonNamePhrase_Invalid()
    {
        // A multi-word bible character name matches as a consecutive token run, case-insensitively.
        AssertInvalid("portrait of bà Thanh at dusk", KeywordRules.PersonName, Rules(persons: ["bà thanh"]));
    }

    [Fact]
    public void KeywordRules_VietnameseKeyword_Invalid()
    {
        // 03 FR-03-3: keywords are English, so a vi keyword (diacritics) fails R2.
        AssertInvalid("ngôi nhà cũ", KeywordRules.NotEnglish);
    }

    private static void AssertInvalid(string keyword, string expectedRule, StoryRules? rules = null)
    {
        var violation = KeywordRules.Check(keyword, rules ?? Rules());

        violation.IsValid.ShouldBeFalse($"'{keyword}' must fail {expectedRule}");
        violation.RuleId.ShouldBe(expectedRule);
        KeywordRules.IsValid(keyword, rules ?? Rules()).ShouldBeFalse();
    }

    private static StoryRules Rules(IEnumerable<string>? persons = null, IEnumerable<string>? places = null)
    {
        return new StoryRules
        {
            KnownPersonNames = ToSet(persons),
            KnownPlaceNames = ToSet(places),
        };
    }

    private static HashSet<string> ToSet(IEnumerable<string>? values)
    {
        return values is null ? new HashSet<string>() : new HashSet<string>(values);
    }

    /// <summary>The good column of the three numbered topic tables in <c>prompts/shared/keyword-rules.v1.md</c>.</summary>
    private static List<string> GoodExamplesFromContract()
    {
        var path = Path.Combine(RepoRoot(), "prompts", "shared", "keyword-rules.v1.md");
        var good = new List<string>();
        foreach (var line in File.ReadAllLines(path))
        {
            var cells = line.Split('|');
            if (cells.Length < 4 || !int.TryParse(cells[1].Trim(), out _))
            {
                continue;
            }

            var cell = cells[2].Trim();
            if (cell.Length > 0)
            {
                good.Add(cell);
            }
        }

        return good;
    }

    /// <summary>The repository root, found by walking up from the test output to <c>NightTales.sln</c>.</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NightTales.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root (NightTales.sln) above the test output.");
    }
}
