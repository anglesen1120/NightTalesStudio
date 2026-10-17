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
    [InlineData("- Một dòng")]
    [InlineData("* Một dòng")]
    [InlineData("+ Một dòng")]
    [InlineData("1. Một dòng")]
    [InlineData("12. Một dòng")]
    [InlineData("Dòng đầu\n- Dòng hai")]
    public void IdeaValidator_ListMarker_DropsCandidate(string title)
    {
        var errors = IdeaValidator.Validate(Candidate(title: title), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("title", IdeaValidator.InvalidStructure, "list_marker"));
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
