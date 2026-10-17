using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Stories.Structure;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Autopilot.Ideas;

/// <summary>
/// The per-candidate rules of 02.8.1 plan §3.2 (<see cref="IdeaValidator"/>): every limit as its own field
/// error, the boundary of each cap, the markup and beat rules, and the batch path where an all-invalid batch
/// yields no candidate for the run to persist.
/// </summary>
public sealed class IdeaValidatorTests
{
    private static readonly IdeaLimits Limits = new();

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
        var title = new string('t', Limits.TitleChars + 1);

        var errors = IdeaValidator.Validate(Candidate(title: title), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("title", IdeaValidator.TooLong, "100"));
    }

    [Fact]
    public void IdeaValidator_TitleCountsCodePointsNotUtf16Units()
    {
        // A 101-character title of astral characters is 101 characters to the varchar(100) column but 202
        // UTF-16 units to string.Length, so the rule must refuse it while the 100-character one stays valid.
        var atTheCap = string.Concat(Enumerable.Repeat("😀", Limits.TitleChars));
        var overTheCap = string.Concat(Enumerable.Repeat("😀", Limits.TitleChars + 1));

        IdeaValidator.Validate(Candidate(title: atTheCap), Limits).ShouldBeEmpty();
        IdeaValidator.Validate(Candidate(title: overTheCap), Limits)[0].Field.ShouldBe("title");
    }

    [Fact]
    public void IdeaValidator_LoglineExactly300_ReturnsNoErrors()
    {
        var logline = new string('l', Limits.LoglineChars);

        IdeaValidator.Validate(Candidate(logline: logline), Limits).ShouldBeEmpty();
    }

    [Fact]
    public void IdeaValidator_LoglineOver300_DropsCandidate()
    {
        var logline = new string('l', Limits.LoglineChars + 1);

        var errors = IdeaValidator.Validate(Candidate(logline: logline), Limits);

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
        IdeaValidator.Validate(Candidate(setting: new string('s', Limits.SettingChars)), Limits).ShouldBeEmpty();
    }

    [Fact]
    public void IdeaValidator_SettingOver120_DropsCandidate()
    {
        var errors = IdeaValidator.Validate(Candidate(setting: new string('s', Limits.SettingChars + 1)), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("setting", IdeaValidator.TooLong, "120"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IdeaValidator_BlankTitle_DropsCandidate(string? title)
    {
        var errors = IdeaValidator.Validate(Candidate(title: title!), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("title", IdeaValidator.Empty, null));
    }

    [Fact]
    public void IdeaValidator_BlankHookAndSetting_ReportBothFields()
    {
        var errors = IdeaValidator.Validate(Candidate(hook: " ", setting: ""), Limits);

        errors.Select(error => error.Field).ShouldBe(["hook", "setting"]);
        errors.ShouldAllBe(error => error.Code == IdeaValidator.Empty);
    }

    [Fact]
    public void IdeaValidator_AngleBracketInLogline_DropsCandidate()
    {
        var errors = IdeaValidator.Validate(Candidate(logline: "Căn nhà <script> bên sông"), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("logline", IdeaValidator.InvalidStructure, "angle_bracket"));
    }

    [Fact]
    public void IdeaValidator_AngleBracketInHook_DropsCandidate()
    {
        var errors = IdeaValidator.Validate(Candidate(hook: "Người kể > kẻ nghe"), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("hook", IdeaValidator.InvalidStructure, "angle_bracket"));
    }

    [Theory]
    [InlineData("Tiêu đề *in đậm*")]
    [InlineData("Tiêu đề _nghiêng_")]
    [InlineData("Tiêu đề `mã`")]
    [InlineData("Tiêu đề #mục")]
    public void IdeaValidator_EmphasisOrHeadingMarker_DropsCandidate(string title)
    {
        var errors = IdeaValidator.Validate(Candidate(title: title), Limits);

        errors.Count.ShouldBe(1);
        errors[0].Field.ShouldBe("title");
        errors[0].Code.ShouldBe(IdeaValidator.InvalidStructure);
        errors[0].Detail.ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("- mở đầu\n- kết thúc")]
    [InlineData("Mở đầu:\n* gợi ý")]
    [InlineData("Các bước:\n1. bước một")]
    public void IdeaValidator_ListMarker_DropsCandidate(string logline)
    {
        var errors = IdeaValidator.Validate(Candidate(logline: logline), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("logline", IdeaValidator.InvalidStructure, "list_marker"));
    }

    [Theory]
    [InlineData("Người kể chuyện (phần 2) trở về")]
    [InlineData("Căn nhà bên sông [không tên]")]
    [InlineData("Câu chuyện có thật, không hư cấu")]
    [InlineData("Số 3 đường Nguyễn Trãi")]
    public void IdeaValidator_OrdinaryProse_ReturnsNoErrors(string logline)
    {
        // Brackets, parentheses and a number followed by a space are prose, not markup: the narrower marker
        // set must not refuse them (a story segment is checked by the D3 rule, an idea title is not).
        IdeaValidator.Validate(Candidate(logline: logline), Limits).ShouldBeEmpty();
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
    public void IdeaValidator_BeatsWithoutTwist_DropsCandidate()
    {
        var errors = IdeaValidator.Validate(Candidate(beats: ["setup", "build_up", "clue"]), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("beats", IdeaValidator.BeatMissingTurn, "twist"));
    }

    [Theory]
    [InlineData("reveal")]
    [InlineData("twist")]
    public void IdeaValidator_BeatsWithOneTurn_ReturnsNoErrors(string turn)
    {
        IdeaValidator.Validate(Candidate(beats: ["setup", "clue", turn]), Limits).ShouldBeEmpty();
    }

    [Fact]
    public void IdeaValidator_BeatsImmediateRepetition_DropsCandidate()
    {
        var errors = IdeaValidator.Validate(Candidate(beats: ["setup", "clue", "clue", "twist"]), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("beats[2]", IdeaValidator.BeatRepeated, "clue"));
    }

    [Fact]
    public void IdeaValidator_RepeatedBeatNotAdjacent_ReturnsNoErrors()
    {
        // The rule is "no immediate repetition", not "no repetition": a setup…twist…setup outline is legal.
        IdeaValidator.Validate(Candidate(beats: ["setup", "twist", "setup", "reveal"]), Limits).ShouldBeEmpty();
    }

    [Fact]
    public void IdeaValidator_UnknownBeatName_DropsCandidate()
    {
        var errors = IdeaValidator.Validate(Candidate(beats: ["setup", "clue", "denouement"]), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError("beats[2]", IdeaValidator.BeatInvalid, "denouement"));
    }

    [Fact]
    public void IdeaValidator_BeatOutsideTheSixD3Names_DropsCandidate()
    {
        // "build-up" is not the D3 value ("build_up"), so the vocabulary is exact, not fuzzy.
        var errors = IdeaValidator.Validate(Candidate(beats: ["setup", "build-up", "twist"]), Limits);

        errors.Count.ShouldBe(1);
        errors[0].Code.ShouldBe(IdeaValidator.BeatInvalid);
    }

    [Fact]
    public void IdeaValidator_BeatsExactlyEight_ReturnsNoErrors()
    {
        IdeaValidator.Validate(
            Candidate(beats: ["setup", "build_up", "clue", "twist", "reveal", "aftermath", "clue", "twist"]),
            Limits)
            .ShouldBeEmpty();
    }

    [Fact]
    public void IdeaValidator_BeatsOverEight_DropsCandidate()
    {
        var beats = new[] { "setup", "build_up", "clue", "twist", "reveal", "aftermath", "clue", "twist", "reveal" };

        var errors = IdeaValidator.Validate(Candidate(beats: beats), Limits);

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
    public void IdeaValidator_EmptyStructureValue_DropsCandidate()
    {
        var errors = IdeaValidator.Validate(
            Candidate(tags: Tags(subGenre: "  ", narratorMode: "")),
            Limits);

        errors.Select(error => error.Field).ShouldBe(["structure.sub_genre", "structure.narrator_mode"]);
        errors.ShouldAllBe(error => error.Code == IdeaValidator.Empty);
    }

    [Fact]
    public void IdeaValidator_MalformedLimits_ReportsOneLimitError()
    {
        var errors = IdeaValidator.Validate(Candidate(), new IdeaLimits(MinBeats: 8, MaxBeats: 3));

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new IdeaFieldError(IdeaValidator.LimitsField, IdeaValidator.InvalidLimits, null));
    }

    [Fact]
    public void IdeaValidator_AllCandidatesInvalid_ReturnsEmpty()
    {
        // The caller path of plan §4 U1 step 4: every candidate has at least one violation, so the run has
        // nothing to persist and fails ideas_invalid_output.
        var candidates = new List<IdeaCandidate>
        {
            Candidate(title: new string('t', Limits.TitleChars + 1)),
            Candidate(logline: "Căn nhà <script>"),
            Candidate(beats: ["setup", "clue", "clue", "twist"]),
            Candidate(tags: Tags(setting: string.Empty)),
        };

        IdeaValidator.Valid(candidates, Limits).ShouldBeEmpty();
    }

    [Fact]
    public void IdeaValidator_SomeCandidatesInvalid_ReturnsOnlyTheValidOnes()
    {
        var valid = Candidate(title: "Tiêu đề hợp lệ");
        var invalid = Candidate(title: "Tiêu đề *lỗi*");

        var survivors = IdeaValidator.Valid([invalid, valid], Limits);

        survivors.Count.ShouldBe(1);
        survivors[0].ShouldBeSameAs(valid);
    }

    [Fact]
    public void IdeaValidator_NullCandidateOrLimits_Throws()
    {
        Should.Throw<ArgumentNullException>(() => IdeaValidator.Validate(null!, Limits));
        Should.Throw<ArgumentNullException>(() => IdeaValidator.Validate(Candidate(), null!));
        Should.Throw<ArgumentNullException>(() => IdeaValidator.Valid(null!, Limits));
    }

    /// <summary>A valid candidate; every argument overrides one field so a test names only its own violation.</summary>
    private static IdeaCandidate Candidate(
        string title = "Căn nhà bên sông",
        string logline = "Một người trở về căn nhà cũ và nghe tiếng gọi lúc nửa đêm.",
        string hook = "Tiếng gọi chỉ cất lên khi đèn tắt.",
        string setting = "Căn nhà gỗ hai tầng bên sông, đêm mưa.",
        StoryStructureTags? tags = null,
        IReadOnlyList<string>? beats = null) =>
        new(
            title,
            logline,
            hook,
            setting,
            tags ?? Tags(),
            beats ?? ["setup", "clue", "twist"]);

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
        [.. new[] { "setup", "clue", "twist" }.Take(count)];
}
