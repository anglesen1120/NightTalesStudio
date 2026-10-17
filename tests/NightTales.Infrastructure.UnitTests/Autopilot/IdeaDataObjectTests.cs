using System.Text.Json;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Structure;
using NightTales.Infrastructure.OpenAI;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Autopilot;

/// <summary>
/// 02.8.1-T1 plan §7.1: the one D6 data object of an idea call at its boundaries — the control keys that always
/// travel, the optional keys an empty or unconstrained value omits rather than sends empty, the id-free history
/// fallback, and the 400-character note ceiling that never splits a surrogate pair.
/// </summary>
public sealed class IdeaDataObjectTests
{
    [Fact]
    public void Build_TheMinimalRequest_KeepsOnlyTheControlKeys()
    {
        var json = IdeaDataObjectJson.Build(Request());

        var root = JsonDocument.Parse(json).RootElement;

        root.EnumerateObject().Select(property => property.Name).ShouldBe(["task", "language", "topic", "count"]);
        root.GetProperty("task").GetString().ShouldBe("generate_ideas");
        root.GetProperty("language").GetString().ShouldBe("vi");
        root.GetProperty("topic").GetString().ShouldBe("true_crime");
        root.GetProperty("count").GetInt32().ShouldBe(5);
    }

    [Fact]
    public void Build_BlankToneAndEmptyLists_AreOmitted()
    {
        var json = IdeaDataObjectJson.Build(Request(tone: "   ", bannedThemes: [" ", ""], notes: [new ResearchNoteRef("user_note", "  ")]));

        json.ShouldNotContain("tone");
        json.ShouldNotContain("banned_themes");
        json.ShouldNotContain("research_notes");
    }

    [Fact]
    public void Build_UnconstrainedStructureDimensions_AreOmitted()
    {
        // Plan §7.1: "absent means unconstrained", so a dimension the caller did not steer never travels as an
        // empty string or an empty array.
        var json = IdeaDataObjectJson.Build(
            Request(
                requiredTags: new StructureRequirements("haunted_house", null, "  ", null),
                forbiddenTags: new StructureForbidden(["slasher"], [], ["  "], [])));

        var root = JsonDocument.Parse(json).RootElement;
        var required = root.GetProperty("required_tags");
        required.EnumerateObject().Select(property => property.Name).ShouldBe(["sub_genre"]);
        var forbidden = root.GetProperty("forbidden_tags");
        forbidden.EnumerateObject().Select(property => property.Name).ShouldBe(["sub_genre"]);
    }

    [Fact]
    public void Build_StructureSetToNothing_OmitsBothKeys()
    {
        var json = IdeaDataObjectJson.Build(
            Request(
                requiredTags: new StructureRequirements(" ", null, null, null),
                forbiddenTags: new StructureForbidden([], [], [], [])));

        json.ShouldNotContain("required_tags");
        json.ShouldNotContain("forbidden_tags");
    }

    [Fact]
    public void Build_History_TrimsTitles_AndDropsBlanks()
    {
        var json = IdeaDataObjectJson.Build(
            Request(
                recentEpisodes:
                [
                    new EpisodeSummaryRef("  History title  ", "History summary."),
                    new EpisodeSummaryRef("   ", "A summary without a title."),
                    new EpisodeSummaryRef("Only a title.", null),
                ]));

        var episodes = JsonDocument.Parse(json).RootElement.GetProperty("recent_episodes");
        episodes.GetArrayLength().ShouldBe(2);
        episodes[0].GetProperty("title").GetString().ShouldBe("History title");
        episodes[0].GetProperty("summary").GetString().ShouldBe("History summary.");
        episodes[1].EnumerateObject().Select(property => property.Name).ShouldBe(["title"]);
        episodes[1].GetProperty("title").GetString().ShouldBe("Only a title.");
    }

    [Fact]
    public void Build_BibleRules_AreTrimmedAndBlanksDropped_AndABlankOnlyBibleIsAbsent()
    {
        var rules = JsonDocument.Parse(
            IdeaDataObjectJson.Build(Request(bible: Bible(["Never name a village.", "   ", "  Kill nobody."]))))
            .RootElement.GetProperty("bible").GetProperty("rules");
        rules.EnumerateArray().Select(rule => rule.GetString()).ShouldBe(["Never name a village.", "Kill nobody."]);

        // A snapshot that carries nothing the prompt can use is no bible at all: the key is omitted and the
        // history fallback takes over.
        var empty = JsonDocument.Parse(
            IdeaDataObjectJson.Build(Request(bible: Bible(["   "]), recentEpisodes: [new EpisodeSummaryRef("A title", null)])))
            .RootElement;
        empty.TryGetProperty("bible", out _).ShouldBeFalse();
        empty.GetProperty("recent_episodes")[0].GetProperty("title").GetString().ShouldBe("A title");
    }

    [Fact]
    public void Build_NoCandidate_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => IdeaDataObjectJson.Build(Request(count: 0)));
    }

    [Fact]
    public void Build_NullRequest_Throws()
    {
        Should.Throw<ArgumentNullException>(() => IdeaDataObjectJson.Build(null!));
    }

    [Fact]
    public void TruncateNote_WithinTheCeiling_KeepsItWhole()
    {
        var note = new string('n', IdeaDataObjectJson.MaxResearchNoteChars);

        IdeaDataObjectJson.TruncateNote(note, IdeaDataObjectJson.MaxResearchNoteChars).ShouldBe(note);
        IdeaDataObjectJson.TruncateNote("short", IdeaDataObjectJson.MaxResearchNoteChars).ShouldBe("short");
    }

    [Fact]
    public void TruncateNote_LongerThanTheCeiling_KeepsTheOpening()
    {
        var note = string.Concat("keep ", new string('n', IdeaDataObjectJson.MaxResearchNoteChars));

        IdeaDataObjectJson.TruncateNote(note, IdeaDataObjectJson.MaxResearchNoteChars).Length
            .ShouldBe(IdeaDataObjectJson.MaxResearchNoteChars);
        IdeaDataObjectJson.TruncateNote(note, IdeaDataObjectJson.MaxResearchNoteChars).ShouldStartWith("keep ");
    }

    [Fact]
    public void TruncateNote_HighSurrogateAtTheCut_IsDropped()
    {
        // A cut between the halves of one astral character would emit a lone surrogate, which is not a character.
        var note = new string('a', 3) + "😀" + "tail";

        var cut = IdeaDataObjectJson.TruncateNote(note, 4);

        cut.ShouldBe("aaa");
        char.IsHighSurrogate(cut[^1]).ShouldBeFalse();
    }

    [Fact]
    public void TruncateNote_AtThePairBoundary_KeepsTheWholeCharacter()
    {
        // The ceiling lands after the pair (`😀` occupies [3,4]), so the character is not split and is kept.
        var note = new string('a', 3) + "😀" + "tail";

        IdeaDataObjectJson.TruncateNote(note, 5).ShouldBe(new string('a', 3) + "😀");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TruncateNote_NonPositiveCeiling_Throws(int ceiling)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => IdeaDataObjectJson.TruncateNote("note", ceiling));
    }

    private static BibleSnapshot Bible(IReadOnlyList<string>? rules = null) =>
        new(
            1,
            null,
            [],
            [],
            rules ?? [],
            [],
            Truncated: false);

    private static IdeaGenerationRequest Request(
        string? tone = null,
        IReadOnlyList<string>? bannedThemes = null,
        BibleSnapshot? bible = null,
        IReadOnlyList<EpisodeSummaryRef>? recentEpisodes = null,
        StructureRequirements? requiredTags = null,
        StructureForbidden? forbiddenTags = null,
        IReadOnlyList<ResearchNoteRef>? notes = null,
        int count = 5) =>
        new(
            new RunContext(
                Guid.Parse("0197a000-0000-7000-8000-000000000001"),
                Guid.Parse("0197a000-0000-7000-8000-000000000002"),
                Guid.Parse("0197a000-0000-7000-8000-000000000003"),
                null,
                new ReservationId(Guid.Parse("0197a000-0000-7000-8000-000000000005")),
                GenerationTrigger.User,
                1,
                "1"),
            "vi",
            "true_crime",
            tone,
            bannedThemes ?? [],
            bible,
            recentEpisodes ?? [],
            requiredTags,
            forbiddenTags,
            notes ?? [],
            count);
}
