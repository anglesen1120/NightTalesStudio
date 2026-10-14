using System.Text.Json;
using NightTales.Application.Features.SeriesBible;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Structure;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.SeriesBible;

/// <summary>
/// The <c>bible.v1</c> wire shapes of 02.6.3-T1 (plan §3.1, §4 U1/U2): the write document
/// <see cref="BibleContentV1"/> a PUT body carries — which never advertises the two server-derived summary
/// members — and the read document <see cref="BibleContentResponseV1"/> that answers the same document with
/// <c>structure</c> and <c>approved_at</c> filled in.
/// </summary>
public sealed class BibleContentV1MappingTests
{
    private static readonly Guid EpisodeId = Guid.Parse("5e4b5e60-4e5f-4a71-8283-9e0f1a2b3c4d");

    [Fact]
    public void ToDomain_EmptyBody_IsTheEmptyBible()
    {
        var content = new BibleContentV1(null, null, null, null, null, null).ToDomain();

        content.SchemaVersion.ShouldBe(SeriesBibleContent.V1);
        content.Narrator.Persona.ShouldBeEmpty();
        content.Narrator.Name.ShouldBeNull();
        content.Narrator.VoiceNotes.ShouldBeEmpty();
        content.RecurringCharacters.ShouldBeEmpty();
        content.Places.ShouldBeEmpty();
        content.Rules.ShouldBeEmpty();
        content.EpisodeSummaries.ShouldBeEmpty();
    }

    [Fact]
    public void ToDomain_ClientSchemaVersion_IsIgnored()
    {
        var content = new BibleContentV1(
            "bible.v9",
            new BibleNarratorV1("n", "p", "v"),
            null,
            null,
            null,
            null).ToDomain();

        content.SchemaVersion.ShouldBe(SeriesBibleContent.V1);
    }

    [Fact]
    public void ToDomain_NullElement_KeepsItsIndexWithAnEmptyItem()
    {
        var content = new BibleContentV1(
            null,
            new BibleNarratorV1(null, "p", null),
            [null!, new BibleCharacterV1("ch1", "Name", "Description", EpisodeId)],
            [null!],
            [null!],
            [null!]).ToDomain();

        // A JSON null element becomes an empty item, so the validator still reports its own index
        // (recurring_characters[0].name) instead of the mapping throwing.
        content.RecurringCharacters.Count.ShouldBe(2);
        content.RecurringCharacters[0].Id.ShouldBeEmpty();
        content.RecurringCharacters[1].Id.ShouldBe("ch1");
        content.RecurringCharacters[1].Name.ShouldBe("Name");
        content.RecurringCharacters[1].Description.ShouldBe("Description");
        content.RecurringCharacters[1].FirstEpisodeId.ShouldBe(EpisodeId);
        content.Places.ShouldHaveSingleItem().Name.ShouldBeEmpty();
        content.Rules.ShouldHaveSingleItem().Text.ShouldBeEmpty();
        content.EpisodeSummaries.ShouldHaveSingleItem().EpisodeId.ShouldBe(Guid.Empty);
    }

    [Fact]
    public void ToDomain_SummaryCarriesOnlyTheTitleAndTheText()
    {
        var content = new BibleContentV1(
            null,
            new BibleNarratorV1(null, "p", null),
            null,
            null,
            null,
            [new BibleEpisodeSummaryV1(EpisodeId, "Title", "Text")]).ToDomain();

        var summary = content.EpisodeSummaries.ShouldHaveSingleItem();
        summary.EpisodeId.ShouldBe(EpisodeId);
        summary.Title.ShouldBe("Title");
        summary.Summary.ShouldBe("Text");
        // The two server-derived members are placeholders here: the handler replaces them, and a summary the
        // validator has to refuse is refused for its episode id, not for this structure.
        summary.ApprovedAt.ShouldBe(default);
        summary.Structure.ShouldBeEquivalentTo(StoryStructureTags.Empty);
    }

    [Fact]
    public void WireNames_AreTheContract()
    {
        var content = new BibleContentV1(
            SeriesBibleContent.V1,
            new BibleNarratorV1("name", "persona", "notes"),
            [new BibleCharacterV1("ch1", "Name", "Description", EpisodeId)],
            [new BiblePlaceV1("pl1", "Place", "Description")],
            [new BibleRuleV1("rl1", "Rule")],
            [new BibleEpisodeSummaryV1(EpisodeId, "Title", "Text")]);

        var json = JsonSerializer.Serialize(content, JsonSerializerOptions.Web);

        json.ShouldContain("\"schema_version\":\"bible.v1\"");
        json.ShouldContain("\"voice_notes\":\"notes\"");
        json.ShouldContain("\"recurring_characters\":[");
        json.ShouldContain("\"first_episode_id\":");
        json.ShouldContain("\"episode_summaries\":[");
        json.ShouldNotContain("structure");
        json.ShouldNotContain("approved_at");
    }

    [Fact]
    public void FromDomain_StoredContent_CarriesTheDerivedSummaryMembers()
    {
        var structure = new StoryStructureTags(
            "haunted_house",
            "rural_house",
            "night",
            "revenant",
            "first_person",
            ["setup", "twist"]);
        var approvedAt = new DateTimeOffset(2026, 10, 4, 21, 3, 11, TimeSpan.Zero);
        var stored = new SeriesBibleContent(
            SeriesBibleContent.V1,
            new BibleNarrator("Narrator", "Persona", "Notes"),
            [new BibleCharacter("ch1", "Name", "Description", EpisodeId)],
            [new BiblePlace("pl1", "Place", "Description")],
            [new BibleRule("rl1", "Rule")],
            [new BibleEpisodeSummary(EpisodeId, "Title", "Text", structure, approvedAt)]);

        var wire = BibleContentResponseV1.FromDomain(stored);

        wire.SchemaVersion.ShouldBe(SeriesBibleContent.V1);
        wire.Narrator.Persona.ShouldBe("Persona");
        wire.RecurringCharacters.ShouldHaveSingleItem().Id.ShouldBe("ch1");
        wire.Places.ShouldHaveSingleItem().Id.ShouldBe("pl1");
        wire.Rules.ShouldHaveSingleItem().Text.ShouldBe("Rule");

        var summary = wire.EpisodeSummaries.ShouldHaveSingleItem();
        summary.EpisodeId.ShouldBe(EpisodeId);
        summary.Title.ShouldBe("Title");
        summary.Summary.ShouldBe("Text");
        summary.ApprovedAt.ShouldBe(approvedAt);
        summary.Structure.SubGenre.ShouldBe("haunted_house");
        summary.Structure.Setting.ShouldBe("rural_house");
        summary.Structure.Opening.ShouldBe("night");
        summary.Structure.TwistType.ShouldBe("revenant");
        summary.Structure.NarratorMode.ShouldBe("first_person");
        summary.Structure.Beats.ShouldBe(["setup", "twist"]);
    }

    [Fact]
    public void ResponseJson_CarriesTheReadDocumentNames()
    {
        var stored = new SeriesBibleContent(
            SeriesBibleContent.V1,
            new BibleNarrator("Narrator", "Persona", "Notes"),
            [],
            [],
            [],
            [new BibleEpisodeSummary(EpisodeId, "Title", "Text", StoryStructureTags.Empty, default)]);

        var json = JsonSerializer.Serialize(BibleContentResponseV1.FromDomain(stored), JsonSerializerOptions.Web);

        json.ShouldContain("\"schema_version\":\"bible.v1\"");
        json.ShouldContain("\"episode_summaries\":[");
        json.ShouldContain("\"episode_id\":");
        json.ShouldContain("\"approved_at\":");
        json.ShouldContain("\"structure\":{");
        json.ShouldContain("\"sub_genre\":");
        json.ShouldContain("\"narrator_mode\":");
        json.ShouldContain("\"beats\":[");
    }
}
