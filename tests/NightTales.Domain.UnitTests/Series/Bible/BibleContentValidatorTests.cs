using System.Globalization;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Structure;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Series.Bible;

/// <summary>
/// 02.6.3 plan §9 (unit): every limit of §3.2 is enforced as a field error, and the document byte budget is
/// counted over the canonical JSON — the same bytes the row stores (§3.3).
/// </summary>
public sealed class BibleContentValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);

    private static readonly Guid EpisodeOne = Guid.Parse("0197a000-0000-7000-8000-0000000000a1");
    private static readonly Guid EpisodeTwo = Guid.Parse("0197a000-0000-7000-8000-0000000000a2");

    private static readonly BibleLimits Limits = new();

    private static SeriesBibleContent Content(
        BibleNarrator? narrator = null,
        IReadOnlyList<BibleCharacter>? characters = null,
        IReadOnlyList<BiblePlace>? places = null,
        IReadOnlyList<BibleRule>? rules = null,
        IReadOnlyList<BibleEpisodeSummary>? summaries = null) =>
        new(
            SeriesBibleContent.V1,
            narrator ?? new BibleNarrator("Người kể chuyện", "Kể chuyện đêm khuya cho người lớn.", string.Empty),
            characters ?? [],
            places ?? [],
            rules ?? [],
            summaries ?? []);

    private static BibleEpisodeSummary Summary(
        Guid episodeId,
        string title = "Tập một: căn nhà bên sông",
        string summary = "Một người trở về căn nhà cũ và nghe thấy tiếng gọi lúc nửa đêm.") =>
        new(episodeId, title, summary, StoryStructureTags.Empty, Now);

    private static HashSet<Guid> Approved(params Guid[] episodeIds) => new HashSet<Guid>(episodeIds);

    [Fact]
    public void BibleContentValidator_ValidContent_ReturnsNoErrors()
    {
        var content = Content(
            characters: [new BibleCharacter("ch1", "Bà Tư", "Người giữ căn nhà cũ.", EpisodeOne)],
            places: [new BiblePlace("pl1", "Căn nhà bên sông", "Nhà gỗ hai tầng, cửa luôn mở.")],
            rules: [new BibleRule("rl1", "Người chết không nói tên thật.")],
            summaries: [Summary(EpisodeOne)]);

        var errors = BibleContentValidator.Validate(content, Limits, Approved(EpisodeOne, EpisodeTwo));

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void BibleContentValidator_TooManyCharacters_ReturnsTooManyError()
    {
        var characters = Enumerable.Range(0, Limits.MaxCharacters + 1)
            .Select(index => new BibleCharacter($"ch{index + 1}", $"Nhân vật {index + 1}", string.Empty, null))
            .ToList();

        var errors = BibleContentValidator.Validate(Content(characters: characters), Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new BibleFieldError("recurring_characters", "too_many", Limits.MaxCharacters.ToString(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void BibleContentValidator_CharacterCountAtMax_ReturnsNoErrors()
    {
        var characters = Enumerable.Range(0, Limits.MaxCharacters)
            .Select(index => new BibleCharacter($"ch{index + 1}", $"Nhân vật {index + 1}", string.Empty, null))
            .ToList();

        BibleContentValidator.Validate(Content(characters: characters), Limits).ShouldBeEmpty();
    }

    [Fact]
    public void BibleContentValidator_TooManyPlacesAndRules_ReturnsOneErrorPerArray()
    {
        var places = Enumerable.Range(0, Limits.MaxPlaces + 1)
            .Select(index => new BiblePlace($"pl{index + 1}", $"Địa điểm {index + 1}", string.Empty))
            .ToList();
        var rules = Enumerable.Range(0, Limits.MaxRules + 1)
            .Select(index => new BibleRule($"rl{index + 1}", $"Luật {index + 1}"))
            .ToList();

        var errors = BibleContentValidator.Validate(Content(places: places, rules: rules), Limits);

        errors.Select(error => error.Field).ShouldBe(["places", "rules"]);
        errors.ShouldAllBe(error => error.Code == "too_many");
    }

    [Fact]
    public void BibleContentValidator_SummaryOver200_ReturnsTooLongError()
    {
        var summary = Summary(EpisodeOne, summary: new string('a', Limits.SummaryChars + 1));

        var errors = BibleContentValidator.Validate(Content(summaries: [summary]), Limits, Approved(EpisodeOne));

        errors.Count.ShouldBe(1);
        errors[0].Field.ShouldBe("episode_summaries[0].summary");
        errors[0].Code.ShouldBe("too_long");
        errors[0].Detail.ShouldBe(Limits.SummaryChars.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void BibleContentValidator_SummaryAt200_ReturnsNoErrors()
    {
        var summary = Summary(EpisodeOne, summary: new string('a', Limits.SummaryChars));

        BibleContentValidator.Validate(Content(summaries: [summary]), Limits, Approved(EpisodeOne)).ShouldBeEmpty();
    }

    [Fact]
    public void BibleContentValidator_EveryLengthAtTheLimit_ReturnsNoErrors()
    {
        var content = Content(
            narrator: new BibleNarrator(
                new string('n', Limits.NarratorNameChars),
                new string('p', Limits.NarratorPersonaChars),
                new string('v', Limits.VoiceNotesChars)),
            characters: [new BibleCharacter("ch1", new string('c', Limits.NameChars), new string('d', Limits.DescriptionChars), null)],
            places: [new BiblePlace("pl1", new string('c', Limits.NameChars), new string('d', Limits.DescriptionChars))],
            rules: [new BibleRule("rl1", new string('r', Limits.RuleChars))],
            summaries: [Summary(EpisodeOne, new string('t', Limits.SummaryTitleChars), new string('s', Limits.SummaryChars))]);

        BibleContentValidator.Validate(content, Limits, Approved(EpisodeOne)).ShouldBeEmpty();
    }

    [Fact]
    public void BibleContentValidator_EveryLengthOneOverTheLimit_ReturnsOneErrorPerField()
    {
        var content = Content(
            narrator: new BibleNarrator(
                new string('n', Limits.NarratorNameChars + 1),
                new string('p', Limits.NarratorPersonaChars + 1),
                new string('v', Limits.VoiceNotesChars + 1)),
            characters: [new BibleCharacter("ch1", new string('c', Limits.NameChars + 1), new string('d', Limits.DescriptionChars + 1), null)],
            places: [new BiblePlace("pl1", new string('c', Limits.NameChars + 1), new string('d', Limits.DescriptionChars + 1))],
            rules: [new BibleRule("rl1", new string('r', Limits.RuleChars + 1))],
            summaries: [Summary(EpisodeOne, new string('t', Limits.SummaryTitleChars + 1), new string('s', Limits.SummaryChars + 1))]);

        var errors = BibleContentValidator.Validate(content, Limits, Approved(EpisodeOne));

        errors.Select(error => error.Field).ShouldBe([
            "narrator.name",
            "narrator.persona",
            "narrator.voice_notes",
            "recurring_characters[0].name",
            "recurring_characters[0].description",
            "places[0].name",
            "places[0].description",
            "rules[0].text",
            "episode_summaries[0].title",
            "episode_summaries[0].summary",
        ]);
        errors.ShouldAllBe(error => error.Code == "too_long");
    }

    [Fact]
    public void BibleContentValidator_DuplicateRuleAfterNormalization_ReturnsDuplicate()
    {
        var content = Content(rules: [
            new BibleRule("rl1", "Không  nói  tên."),
            new BibleRule("rl2", "không nói tên"),
        ]);

        var errors = BibleContentValidator.Validate(content, Limits);

        errors.Count.ShouldBe(1);
        errors[0].Field.ShouldBe("rules[1].text");
        errors[0].Code.ShouldBe("duplicate");
        errors[0].Detail.ShouldBe("rules[0]");
    }

    [Fact]
    public void BibleContentValidator_DistinctRulesWithDifferentDiacritics_ReturnsNoErrors()
    {
        var content = Content(rules: [
            new BibleRule("rl1", "Không nói tên người chết."),
            new BibleRule("rl2", "Không nói tên người sống."),
        ]);

        BibleContentValidator.Validate(content, Limits).ShouldBeEmpty();
    }

    [Fact]
    public void BibleContentValidator_DuplicateCharacterName_ReturnsDuplicate()
    {
        var content = Content(characters: [
            new BibleCharacter("ch1", "Bà Tư", string.Empty, null),
            new BibleCharacter("ch2", "BÀ TƯ ", string.Empty, null),
        ]);

        var errors = BibleContentValidator.Validate(content, Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new BibleFieldError("recurring_characters[1].name", "duplicate", "recurring_characters[0]"));
    }

    [Fact]
    public void BibleContentValidator_TwoSummariesForOneEpisode_ReturnsDuplicate()
    {
        var content = Content(summaries: [Summary(EpisodeOne), Summary(EpisodeOne, title: "Tập một lần nữa")]);

        var errors = BibleContentValidator.Validate(content, Limits, Approved(EpisodeOne));

        errors.Count.ShouldBe(1);
        errors[0].Field.ShouldBe("episode_summaries[1].episode_id");
        errors[0].Code.ShouldBe("duplicate");
    }

    [Fact]
    public void BibleContentValidator_AngleBracketInName_ReturnsError()
    {
        var content = Content(characters: [new BibleCharacter("ch1", "<b>Bà Tư</b>", "Kể cả trong mô tả <i>.", null)]);

        var errors = BibleContentValidator.Validate(content, Limits);

        errors.Select(error => error.Field).ShouldBe(["recurring_characters[0].name", "recurring_characters[0].description"]);
        errors.ShouldAllBe(error => error.Code == "invalid_structure");
    }

    [Fact]
    public void BibleContentValidator_PlainTextWithoutMarkup_ReturnsNoErrors()
    {
        var content = Content(
            characters: [new BibleCharacter("ch1", "Bà Tư (chị Hai)", "Nói: “Đừng mở cửa.” — 40 tuổi.", null)],
            rules: [new BibleRule("rl1", "Không mở cửa sau 23 giờ: chuông sẽ reo.")]);

        BibleContentValidator.Validate(content, Limits).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("\u0001")]
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("\u200b")]
    [InlineData("\u202e")]
    [InlineData("\ufeff")]
    public void BibleContentValidator_ControlOrFormatCharacterInText_ReturnsInvalidStructure(string injected)
    {
        var content = Content(characters: [new BibleCharacter("ch1", "Bà Tư" + injected, string.Empty, null)]);

        var errors = BibleContentValidator.Validate(content, Limits);

        errors.Count.ShouldBe(1);
        errors[0].Field.ShouldBe("recurring_characters[0].name");
        errors[0].Code.ShouldBe("invalid_structure");
    }

    [Fact]
    public void BibleContentValidator_LoneSurrogateInText_ThrowsBeforeItCanReportInvalidStructure()
    {
        // RECORDED (02.6.3-T1 QA, defect reported): ICU normalisation throws ArgumentException for a lone
        // surrogate, so Validate throws instead of reporting `invalid_structure` — the one input of this file that
        // does not come back as an error list. The case is unreachable over HTTP, because System.Text.Json refuses
        // the escape while it reads the string and the route answers the malformed-body 400 (see the QA
        // integration test PutBible_LoneSurrogateInAName_IsRefusedAsAMalformedBody), so this is a robustness gap in
        // the Domain rather than a wire bug.
        //
        // A lone surrogate cannot be written as a source literal — the compiler replaces it with U+FFFD, which is a
        // valid character — so the one string-shaped case the theory above cannot reach is built at run time.
        var injected = new string((char)0xD800, 1);
        var content = Content(characters: [new BibleCharacter("ch1", "Bà Tư" + injected, string.Empty, null)]);

        var thrown = Should.Throw<ArgumentException>(() => BibleContentValidator.Validate(content, Limits));

        thrown.Message.ShouldContain("invalid Unicode code points");
    }

    [Fact]
    public void BibleContentValidator_EmptyPersona_ReturnsEmptyError()
    {
        var content = Content(narrator: new BibleNarrator("Bà Tư", "   ", string.Empty));

        var errors = BibleContentValidator.Validate(content, Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new BibleFieldError("narrator.persona", "empty", null));
    }

    [Fact]
    public void BibleContentValidator_NullName_AddsNoError_AndAnEmptyPersonaIsTheOneRefusal()
    {
        // `narrator.name` is optional (§3.2), so a null name alone is valid...
        BibleContentValidator.Validate(Content(narrator: new BibleNarrator(null, "Kể chuyện đêm.", string.Empty)), Limits)
            .ShouldBeEmpty();

        // ...while the empty bible of a fresh series carries an empty persona, the one field §3.2 requires to be
        // non-empty: that single rule is what the first save of an untouched bible is refused by.
        var content = Content(narrator: new BibleNarrator(null, string.Empty, string.Empty));

        var errors = BibleContentValidator.Validate(content, Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new BibleFieldError("narrator.persona", "empty", null));
    }

    [Fact]
    public void BibleContentValidator_UnknownEpisodeId_ReturnsUnknownEpisode()
    {
        var errors = BibleContentValidator.Validate(Content(summaries: [Summary(Guid.Empty)]), Limits, Approved(EpisodeOne));

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new BibleFieldError("episode_summaries[0].episode_id", "unknown_episode", null));
    }

    [Fact]
    public void BibleContentValidator_EpisodeIdNotApproved_ReturnsNotApprovedEpisode()
    {
        var errors = BibleContentValidator.Validate(Content(summaries: [Summary(EpisodeTwo)]), Limits, Approved(EpisodeOne));

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new BibleFieldError("episode_summaries[0].episode_id", "not_approved_episode", null));
    }

    [Fact]
    public void BibleContentValidator_UnapprovedEpisodeWithUnknownStructure_ReportsTheEpisodeOnce()
    {
        // One error per bad summary, never two: `structure`/`approved_at` stay server-derived (task design).
        var summary = new BibleEpisodeSummary(EpisodeTwo, "Tập hai", "Nội dung.", StoryStructureTags.Empty, Now);

        var errors = BibleContentValidator.Validate(Content(summaries: [summary]), Limits, Approved(EpisodeOne));

        errors.Count.ShouldBe(1);
        errors[0].Code.ShouldBe("not_approved_episode");
    }

    [Fact]
    public void BibleContentValidator_SummaryStructureAndApprovedAt_AreNotValidated()
    {
        // A PUT body carries no structure/approved_at: the placeholders of `BibleContentV1.ToDomain()` must not
        // produce an error of their own, or every client save would fail server-side (plan §4 U1).
        var summary = new BibleEpisodeSummary(
            EpisodeOne, "Tập một", "Nội dung.", StoryStructureTags.Empty, default);

        BibleContentValidator.Validate(Content(summaries: [summary]), Limits, Approved(EpisodeOne)).ShouldBeEmpty();
    }

    [Fact]
    public void BibleContentValidator_ContentByteBudget_IsCountedOverTheCanonicalJson()
    {
        var approved = new HashSet<Guid>();
        var summaries = new List<BibleEpisodeSummary>();

        // `episode_summaries` is the only array without a count cap, so it is what can reach the document budget.
        while (true)
        {
            var episodeId = Guid.Parse($"0197a000-0000-7000-8000-{approved.Count:D12}");
            approved.Add(episodeId);
            summaries.Add(Summary(
                episodeId,
                new string('t', Limits.SummaryTitleChars),
                new string('s', Limits.SummaryChars)));

            if (BibleJson.Utf8ByteCount(Content(summaries: summaries)) > Limits.MaxContentBytes)
            {
                break;
            }
        }

        var over = Content(summaries: summaries);
        var under = Content(summaries: summaries.Take(summaries.Count - 1).ToList());

        BibleJson.Utf8ByteCount(under).ShouldBeLessThanOrEqualTo(Limits.MaxContentBytes);
        BibleJson.Utf8ByteCount(over).ShouldBeGreaterThan(Limits.MaxContentBytes);

        BibleContentValidator.Validate(under, Limits, approved).ShouldBeEmpty();

        var errors = BibleContentValidator.Validate(over, Limits, approved);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new BibleFieldError("body", "too_long", Limits.MaxContentBytes.ToString(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void BibleContentValidator_ContentAtExactlyTheByteBudget_PassesAndOneByteMoreFails()
    {
        var content = Content(
            rules: [new BibleRule("rl1", "Không nói tên thật.")],
            summaries: [Summary(EpisodeOne)]);
        var size = BibleJson.Utf8ByteCount(content);

        // §3.2 caps the document at MaxContentBytes, so exactly that many bytes is legal and one byte more is
        // not. Deriving the limit from the document pins the comparison itself, without constructing a
        // 65,536-byte fixture.
        BibleContentValidator.Validate(content, Limits with { MaxContentBytes = size }, Approved(EpisodeOne))
            .ShouldBeEmpty();

        var errors = BibleContentValidator.Validate(
            content,
            Limits with { MaxContentBytes = size - 1 },
            Approved(EpisodeOne));

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new BibleFieldError("body", "too_long", (size - 1).ToString(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void BibleContentValidator_SchemaVersionIsNotPartOfTheLimits()
    {
        // `schema_version` is server-owned: the validator ignores what a body claims (§4 U1), the handler resets it.
        var content = new SeriesBibleContent(
            "bible.v99",
            new BibleNarrator(null, "Kể chuyện đêm khuya.", string.Empty),
            [], [], [], []);

        BibleContentValidator.Validate(content, Limits).ShouldBeEmpty();
    }

    [Fact]
    public void BibleContentValidator_EmptyBibleOfAFreshSeries_FailsOnlyTheNonNullPersonaRule()
    {
        // The row created with a series holds `SeriesBibleContent.Empty` (plan §3.3 backfill and §4 U1), whose
        // persona is the empty string, while §3.2 requires a non-empty persona: the first save is what supplies
        // one, so the fresh row is the single state that does not satisfy the rules.
        var errors = BibleContentValidator.Validate(SeriesBibleContent.Empty, Limits);

        errors.Count.ShouldBe(1);
        errors[0].ShouldBe(new BibleFieldError("narrator.persona", "empty", null));
        BibleJson.Utf8ByteCount(SeriesBibleContent.Empty).ShouldBeLessThanOrEqualTo(Limits.MaxContentBytes);
    }
}
