using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Stories.Structure;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Autopilot.Ideas;

/// <summary>
/// 02.8.1 plan §9 (unit): every §3.2 rule of <see cref="IdeaValidator"/> is a field error the caller drops
/// the candidate for, and the batch path returns only the valid candidates. The character caps are counted
/// in Unicode code points, the same measure a Postgres <c>varchar</c> uses.
/// </summary>
public sealed class IdeaValidatorTests
{
    private static readonly IdeaLimits Limits = new();

    /// <summary>A valid three-beat outline (one <c>twist</c>), shared so no constant array is rebuilt per call.</summary>
    private static readonly string[] ShortOutline = ["setup", "clue", "twist"];

    /// <summary>A nine-beat outline (one over <see cref="IdeaLimits.MaxBeats"/>), for the too-many boundary.</summary>
    private static readonly string[] LongOutline =
        ["setup", "build_up", "clue", "twist", "reveal", "aftermath", "clue", "twist", "reveal"];

    [Fact]
    public void IdeaValidator_ValidCandidate_ReturnsNoErrors()
    {
        IdeaValidator.Validate(Candidate(), Limits).ShouldBeEmpty();
    }

    [Fact]
    public void IdeaValidator_TitleExactly100_ReturnsNoErrors()
    {
        var title = new string('t', Limits.TitleChars);

        IdeaValidator.Validate(Candidate(title: title), Limits).ShouldBeEmpty();
    }

    [Fact]
    public void IdeaValidator_TitleOver100_DropsCandidate()
    {
        var errors = IdeaValidator.Validate(Candidate(title: new string('t', Limits.TitleChars + 1)), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("title", IdeaValidator.TooLong, "100"));
    }

    [Fact]
    public void IdeaValidator_TitleCountsCodePoints_NotUtf16Units()
    {
        // One astral character is one code point (one Postgres varchar unit) but two UTF-16 chars: counting
        // string.Length would reject a title the column accepts.
        var title = new string('t', Limits.TitleChars - 1) + "\U0001F47B";

        IdeaValidator.Validate(Candidate(title: title), Limits).ShouldBeEmpty();
    }

    [Fact]
    public void IdeaValidator_LoglineExactly300_ReturnsNoErrors()
    {
        IdeaValidator.Validate(Candidate(logline: new string('l', Limits.LoglineChars)), Limits)
            .ShouldBeEmpty();
    }

    [Fact]
    public void IdeaValidator_LoglineOver300_DropsCandidate()
    {
        var errors = IdeaValidator.Validate(Candidate(logline: new string('l', Limits.LoglineChars + 1)), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("logline", IdeaValidator.TooLong, "300"));
    }

    [Fact]
    public void IdeaValidator_HookOver300_DropsCandidate()
    {
        var errors = IdeaValidator.Validate(Candidate(hook: new string('h', Limits.HookChars + 1)), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("hook", IdeaValidator.TooLong, "300"));
    }

    [Fact]
    public void IdeaValidator_SettingExactly120_ReturnsNoErrors()
    {
        IdeaValidator.Validate(Candidate(setting: new string('s', Limits.SettingChars)), Limits)
            .ShouldBeEmpty();
    }

    [Fact]
    public void IdeaValidator_SettingOver120_DropsCandidate()
    {
        var errors = IdeaValidator.Validate(Candidate(setting: new string('s', Limits.SettingChars + 1)), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("setting", IdeaValidator.TooLong, "120"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IdeaValidator_BlankTitle_DropsCandidate(string title)
    {
        var errors = IdeaValidator.Validate(Candidate(title: title), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("title", IdeaValidator.Empty, null));
    }

    [Theory]
    [InlineData("logline")]
    [InlineData("hook")]
    [InlineData("setting")]
    public void IdeaValidator_BlankField_DropsCandidate(string field)
    {
        var candidate = field switch
        {
            "logline" => Candidate(logline: " "),
            "hook" => Candidate(hook: " "),
            _ => Candidate(setting: " "),
        };

        var errors = IdeaValidator.Validate(candidate, Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError(field, IdeaValidator.Empty, null));
    }

    [Fact]
    public void IdeaValidator_AngleBracketInLogline_DropsCandidate()
    {
        var errors = IdeaValidator.Validate(Candidate(logline: "Một đêm <định mệnh> tại nhà sông."), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("logline", IdeaValidator.InvalidStructure, "angle_bracket"));
    }

    [Theory]
    [InlineData("Tiêu đề *lỗi*", "*")]
    [InlineData("Tiêu đề _lỗi_", "_")]
    [InlineData("Tiêu đề `lỗi`", "`")]
    public void IdeaValidator_EmphasisMarker_DropsCandidate(string title, string marker)
    {
        var errors = IdeaValidator.Validate(Candidate(title: title), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("title", IdeaValidator.InvalidStructure, marker));
    }

    [Fact]
    public void IdeaValidator_HeadingMarker_DropsCandidate()
    {
        var errors = IdeaValidator.Validate(Candidate(title: "# Chương một"), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("title", IdeaValidator.InvalidStructure, "#"));
    }

    [Theory]
    [InlineData("- Một dòng", "list_marker")]
    [InlineData("* Một dòng", "list_marker")]
    [InlineData("+ Một dòng", "list_marker")]
    [InlineData("1. Một dòng", "list_marker")]
    [InlineData("12. Một dòng", "list_marker")]
    // A later line can only be reached through a line break, and every line break is a control character, so
    // the charset rule reports the break itself before the bullet on the next line is ever the detail.
    [InlineData("Dòng đầu\n- Dòng hai", IdeaValidator.ControlChar)]
    public void IdeaValidator_ListMarker_DropsCandidate(string title, string detail)
    {
        var errors = IdeaValidator.Validate(Candidate(title: title), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("title", IdeaValidator.InvalidStructure, detail));
    }

    [Fact]
    public void IdeaValidator_ParenthesesBracketsAndUrls_AreAllowed()
    {
        // The idea rule is narrower than the story draft's: brackets, parentheses and URLs are not markdown
        // emphasis and would refuse ordinary Vietnamese prose (02.8.1 §3.2 names only the markers).
        var candidate = Candidate(
            title: "Đêm (phần 2)",
            logline: "Một câu chuyện [đã kể] về www.demkhuya.vn");

        IdeaValidator.Validate(candidate, Limits).ShouldBeEmpty();
    }

    [Fact]
    public void IdeaValidator_BeatsExactlyThree_ReturnsNoErrors()
    {
        IdeaValidator.Validate(Candidate(beats: ShortOutline), Limits).ShouldBeEmpty();
    }

    [Fact]
    public void IdeaValidator_BeatsWithoutTwist_DropsCandidate()
    {
        var errors = IdeaValidator.Validate(
            Candidate(beats: ["setup", "build_up", "clue"]),
            Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("beats", IdeaValidator.BeatMissingTurn, "twist"));
    }

    [Fact]
    public void IdeaValidator_RevealInsteadOfTwist_ReturnsNoErrors()
    {
        IdeaValidator.Validate(Candidate(beats: ["setup", "clue", "reveal"]), Limits).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void IdeaValidator_BeatsFewerThanThree_DropsCandidate(int count)
    {
        var errors = IdeaValidator.Validate(Candidate(beats: Beats(count)), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(count == 0
            ? new IdeaFieldError("beats", IdeaValidator.Empty, null)
            : new IdeaFieldError("beats", IdeaValidator.TooFew, "3"));
    }

    [Fact]
    public void IdeaValidator_BeatsAtEightAndNonAdjacentRepeat_ReturnsNoErrors()
    {
        // Eight is inside the band, and a beat may repeat as long as two identical beats are not adjacent.
        IdeaValidator.Validate(
            Candidate(beats: ["setup", "build_up", "clue", "twist", "reveal", "aftermath", "clue", "twist"]),
            Limits)
            .ShouldBeEmpty();
    }

    [Fact]
    public void IdeaValidator_BeatsOverEight_DropsCandidate()
    {
        var errors = IdeaValidator.Validate(Candidate(beats: LongOutline), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("beats", IdeaValidator.TooMany, "8"));
    }

    [Fact]
    public void IdeaValidator_NoBeats_DropsCandidate()
    {
        var errors = IdeaValidator.Validate(Candidate(beats: []), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("beats", IdeaValidator.Empty, null));
    }

    [Fact]
    public void IdeaValidator_BeatsImmediateRepetition_DropsCandidate()
    {
        var errors = IdeaValidator.Validate(
            Candidate(beats: ["setup", "clue", "clue", "twist"]),
            Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("beats[2]", IdeaValidator.BeatRepeated, "clue"));
    }

    [Fact]
    public void IdeaValidator_UnknownBeat_DropsCandidate()
    {
        var errors = IdeaValidator.Validate(
            Candidate(beats: ["setup", "dance", "twist"]),
            Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("beats[1]", IdeaValidator.BeatInvalid, "dance"));
    }

    [Fact]
    public void IdeaValidator_BlankBeat_DropsCandidate()
    {
        var errors = IdeaValidator.Validate(
            Candidate(beats: ["setup", " ", "twist"]),
            Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("beats[1]", IdeaValidator.BeatInvalid, " "));
    }

    [Fact]
    public void IdeaValidator_NullStructure_DropsCandidate()
    {
        var candidate = Candidate() with { Structure = null! };

        var errors = IdeaValidator.Validate(candidate, Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("structure", IdeaValidator.Empty, null));
    }

    [Fact]
    public void IdeaValidator_EmptyStructureValues_DropsCandidate()
    {
        var candidate = Candidate(
            tags: new StoryStructureTags(" ", "riverside_house", "night", "twin", "", []));

        var errors = IdeaValidator.Validate(candidate, Limits);

        errors.Count.ShouldBe(2);
        errors[0].ShouldBe(new IdeaFieldError("structure.sub_genre", IdeaValidator.Empty, null));
        errors[1].ShouldBe(new IdeaFieldError("structure.narrator_mode", IdeaValidator.Empty, null));
    }

    [Fact]
    public void IdeaValidator_MultipleViolations_ReportsEveryFieldInOrder()
    {
        var candidate = Candidate(
            title: "Ok",
            logline: " ",
            hook: new string('h', Limits.HookChars + 1),
            setting: "<x>");

        var errors = IdeaValidator.Validate(candidate, Limits);

        errors.Count.ShouldBe(3);
        errors[0].ShouldBe(new IdeaFieldError("logline", IdeaValidator.Empty, null));
        errors[1].ShouldBe(new IdeaFieldError("hook", IdeaValidator.TooLong, "300"));
        errors[2].ShouldBe(new IdeaFieldError("setting", IdeaValidator.InvalidStructure, "angle_bracket"));
    }

    /// <summary>The four text fields the character rule covers.</summary>
    private static readonly string[] TextFields = ["title", "logline", "hook", "setting"];

    /// <summary>The five structure values the character rule covers, as their JSON paths.</summary>
    private static readonly string[] StructureFields =
    [
        "structure.sub_genre", "structure.setting", "structure.opening", "structure.twist_type",
        "structure.narrator_mode",
    ];

    /// <summary>Every field the character rule covers: the four text fields and the five structure values.</summary>
    private static readonly string[] CharacterFields = [.. TextFields, .. StructureFields];

    /// <summary>
    /// Every character an idea field must refuse: C0/C1 controls (NUL, tab, LF, CR, DEL, NEL), the line and
    /// paragraph separators, format characters (zero-width space and joiner, the right-to-left override, the
    /// soft hyphen, the byte-order mark) and one lone high and one lone low surrogate.
    /// </summary>
    private static readonly string[] Unprintable =
    [
        "\0", "\t", "\n", "\r", "\u007F", "\u0085", "\u2028", "\u2029", "\u00AD", "\u200B", "\u200D", "\u202E",
        "\uFEFF", "\uD800", "\uDC00",
    ];

    /// <summary>The cross product of <see cref="CharacterFields"/> and <see cref="Unprintable"/>.</summary>
    /// <returns>One row per field and character.</returns>
    public static TheoryData<string, string> UnprintableInEveryField()
    {
        var data = new TheoryData<string, string>();
        foreach (var field in CharacterFields)
        {
            foreach (var character in Unprintable)
            {
                data.Add(field, character);
            }
        }

        return data;
    }

    /// <summary>Each structure value against each rule: a URL, an angle bracket and two markup markers.</summary>
    /// <returns>One row per field and violation.</returns>
    public static TheoryData<string, string, string> StructureValueViolations()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var field in StructureFields)
        {
            data.Add(field, "http://x", IdeaValidator.Url);
            data.Add(field, "www.x.com", IdeaValidator.Url);
            data.Add(field, "<b>", IdeaValidator.AngleBracket);
            data.Add(field, "*x*", "*");
            data.Add(field, "#x", "#");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(UnprintableInEveryField))]
    public void IdeaValidator_UnprintableCharacter_DropsCandidate(string field, string character)
    {
        // Embedded in prose, so the value is never whitespace-only: the drop is the character rule's, not the
        // blank rule's.
        var errors = ErrorsFor(field, "Đêm" + character + "dài");

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError(field, IdeaValidator.InvalidStructure, IdeaValidator.ControlChar));
    }

    [Theory]
    [InlineData("Đêm định mệnh tại nhà sông")] // NFC
    [InlineData("\u0110e\u0302m \u0111i\u0323nh")] // NFD: combining diacritics (Mn) are allowed
    [InlineData("Đêm nay có ma \U0001F47B gõ cửa")] // an astral emoji, one valid surrogate pair
    [InlineData("Đêm nay \u2764\uFE0F gõ cửa")] // a variation selector (Mn) stays allowed
    public void IdeaValidator_PrintableProse_ReturnsNoErrors(string title)
    {
        IdeaValidator.Validate(Candidate(title: title), Limits).ShouldBeEmpty();
    }

    [Fact]
    public void IdeaValidator_EmojiJoinerSequence_DropsCandidate()
    {
        // The documented decision: every Cf character is refused, the emoji zero-width joiner U+200D included,
        // so a joined sequence is not a way around the rule.
        var errors = IdeaValidator.Validate(Candidate(title: "Đêm \U0001F468\u200D\U0001F469 gõ cửa"), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("title", IdeaValidator.InvalidStructure, IdeaValidator.ControlChar));
    }

    [Fact]
    public void IdeaValidator_StructureValueExactlyForty_ReturnsNoErrors()
    {
        var value = new string('g', Limits.StructureValueChars);

        IdeaValidator.Validate(Candidate(tags: Tags(subGenre: value)), Limits).ShouldBeEmpty();
    }

    [Fact]
    public void IdeaValidator_StructureValueOverForty_DropsCandidate()
    {
        var errors = IdeaValidator.Validate(
            Candidate(tags: Tags(subGenre: new string('g', Limits.StructureValueChars + 1))),
            Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("structure.sub_genre", IdeaValidator.TooLong, "40"));
    }

    [Theory]
    [InlineData("other:thể loại mới")]
    [InlineData("other:found footage")]
    public void IdeaValidator_OtherStructureValue_ReturnsNoErrors(string value)
    {
        // The D3 `other:<text>` shape is vocabulary, which 02.8.4-T1 owns: here it only has to stay inside the
        // character, markup, URL and length rules.
        IdeaValidator.Validate(Candidate(tags: Tags(subGenre: value)), Limits).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(StructureValueViolations))]
    public void IdeaValidator_StructureValueViolation_DropsCandidate(string field, string value, string detail)
    {
        var errors = ErrorsFor(field, value);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError(field, IdeaValidator.InvalidStructure, detail));
    }

    [Fact]
    public void IdeaValidator_SomeCandidatesInvalid_ReturnsOnlyTheValidOnes()
    {
        var valid = Candidate();
        var invalid = Candidate(title: "Tiêu đề *lỗi*");

        var survivors = IdeaValidator.Valid([invalid, valid], Limits);

        survivors.Count.ShouldBe(1);
        survivors[0].ShouldBeSameAs(valid);
    }

    [Fact]
    public void IdeaValidator_AllCandidatesInvalid_ReturnsEmpty()
    {
        // The caller's ideas_invalid_output path: every candidate carries at least one violation.
        var candidates = new[]
        {
            Candidate(title: new string('t', Limits.TitleChars + 1)),
            Candidate(beats: ["setup", "clue", "climax"]),
        };

        IdeaValidator.Valid(candidates, Limits).ShouldBeEmpty();
    }

    [Fact]
    public void IdeaValidator_InvertedLimits_ReturnsOneLimitsError()
    {
        var limits = new IdeaLimits { MinBeats = 9, MaxBeats = 3 };

        var errors = IdeaValidator.Validate(Candidate(), limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError(IdeaValidator.LimitsField, IdeaValidator.InvalidLimits, null));
    }

    [Fact]
    public void IdeaValidator_NullArguments_Throw()
    {
        Should.Throw<ArgumentNullException>(() => IdeaValidator.Validate(null!, Limits));
        Should.Throw<ArgumentNullException>(() => IdeaValidator.Validate(Candidate(), null!));
        Should.Throw<ArgumentNullException>(() => IdeaValidator.Valid(null!, Limits));
    }

    [Fact]
    public void IdeaValidator_StructureValuesOutsideTheVocabulary_AreAcceptedHere()
    {
        // The D3 tag→vocabulary mapping (`other:<text>`) is 02.2.1-T1's shared mapper, not this layer (plan §3.2):
        // the validator's only structure rule is "present and non-blank", so an unknown-but-non-empty tag is not a
        // candidate drop and the mapping's own validation stays the single source of truth.
        var candidate = Candidate(
            tags: new StoryStructureTags(
                "not_a_vocab_sub_genre",
                "not_a_vocab_setting",
                "not_a_vocab_opening",
                "not_a_vocab_twist",
                "first_person",
                []));

        IdeaValidator.Validate(candidate, Limits).ShouldBeEmpty();
    }

    [Fact]
    public void IdeaValidator_HookExactly300_ReturnsNoErrors()
    {
        IdeaValidator.Validate(Candidate(hook: new string('h', Limits.HookChars)), Limits)
            .ShouldBeEmpty();
    }

    [Fact]
    public void IdeaValidator_TitleAtTheCapInVietnamese_IsAccepted()
    {
        // 10 x 10 precomposed Vietnamese characters: one code point each, exactly at the varchar(100) width, so
        // every diacritic is a character of the column and none of them costs a second one.
        var title = string.Concat(Enumerable.Repeat("chuyện cũ ", 10));

        title.Length.ShouldBe(100);
        IdeaValidator.Validate(Candidate(title: title), Limits).ShouldBeEmpty();
    }

    [Fact]
    public void IdeaValidator_DecomposedVietnameseTitle_CountsEveryCodePoint()
    {
        // The same 40 characters in NFD are 120 code points (each diacritic is a combining mark of its own), which
        // is what a Postgres varchar(100) would refuse: the cap is code points, never the composed spelling.
        var composed = string.Concat(Enumerable.Repeat("ệ", 40));
        var decomposed = composed.Normalize(System.Text.NormalizationForm.FormD);
        decomposed.Length.ShouldBe(120, "the fixture really is decomposed");

        IdeaValidator.Validate(Candidate(title: composed), Limits).ShouldBeEmpty();
        var errors = IdeaValidator.Validate(Candidate(title: decomposed), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("title", IdeaValidator.TooLong, "100"));
    }

    private static IdeaCandidate Candidate(
        string? title = null,
        string? logline = null,
        string? hook = null,
        string? setting = null,
        StoryStructureTags? tags = null,
        IReadOnlyList<string>? beats = null) =>
        new(
            title ?? "Đêm định mệnh tại nhà sông",
            logline ?? "Một người trở về căn nhà cũ và nghe thấy tiếng gọi lúc nửa đêm.",
            hook ?? "Tiếng gõ cửa vang lên đúng ba lần, như mọi đêm.",
            setting ?? "Căn nhà gỗ bên dòng sông quê",
            tags ?? Tags(),
            beats ?? ShortOutline);

    /// <summary>The validator's errors for <paramref name="value"/> placed in the named field.</summary>
    /// <param name="field">One of <see cref="CharacterFields"/> or <see cref="StructureFields"/>.</param>
    /// <param name="value">The value to place in that field.</param>
    /// <returns>The errors of the candidate built with that value in that field.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="field"/> is not a field the validator covers.</exception>
    private static IReadOnlyList<IdeaFieldError> ErrorsFor(string field, string value) => field switch
    {
        "title" => IdeaValidator.Validate(Candidate(title: value), Limits),
        "logline" => IdeaValidator.Validate(Candidate(logline: value), Limits),
        "hook" => IdeaValidator.Validate(Candidate(hook: value), Limits),
        "setting" => IdeaValidator.Validate(Candidate(setting: value), Limits),
        "structure.sub_genre" => IdeaValidator.Validate(Candidate(tags: Tags(subGenre: value)), Limits),
        "structure.setting" => IdeaValidator.Validate(Candidate(tags: Tags(setting: value)), Limits),
        "structure.opening" => IdeaValidator.Validate(Candidate(tags: Tags(opening: value)), Limits),
        "structure.twist_type" => IdeaValidator.Validate(Candidate(tags: Tags(twistType: value)), Limits),
        "structure.narrator_mode" => IdeaValidator.Validate(Candidate(tags: Tags(narratorMode: value)), Limits),
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not a field the validator covers."),
    };

    /// <summary>A complete D3 structure block; every argument overrides one value.</summary>
    private static StoryStructureTags Tags(
        string subGenre = "haunted_house",
        string setting = "riverside_house",
        string opening = "night",
        string twistType = "twin",
        string narratorMode = "first_person") =>
        new(subGenre, setting, opening, twistType, narratorMode, []);

    /// <summary><paramref name="count"/> beats of a valid outline, for the too-few boundary.</summary>
    private static IReadOnlyList<string> Beats(int count) =>
        [.. ShortOutline.Take(count)];
}
