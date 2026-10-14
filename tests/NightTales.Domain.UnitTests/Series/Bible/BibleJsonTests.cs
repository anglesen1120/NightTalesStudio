using System.Text;
using System.Text.Json;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Structure;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Series.Bible;

/// <summary>
/// The wire shape of the two documents <c>series_bibles</c> stores (02.6.3 plan §3.1, §3.3): snake_case
/// property names, every member written, and a proposal whose op kinds round-trip as their model-facing names.
/// </summary>
/// <remarks>
/// `bible.v1` is a contract with the prompt files and the web client, and `bible-proposal.v1` is the strict enum
/// the proposal model is constrained to, so both are pinned here rather than in the endpoint tests.
/// </remarks>
public sealed class BibleJsonTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);

    private static readonly Guid EpisodeId = Guid.Parse("0197a000-0000-7000-8000-0000000000a1");

    [Fact]
    public void BibleJson_ContentDocument_HasExactlyTheBibleV1Members()
    {
        var content = FullContent();

        using var document = JsonDocument.Parse(BibleJson.Serialize(content));

        TopLevelNames(document.RootElement).ShouldBe([
            "schema_version",
            "narrator",
            "recurring_characters",
            "places",
            "rules",
            "episode_summaries",
        ]);

        var narrator = document.RootElement.GetProperty("narrator");
        TopLevelNames(narrator).ShouldBe(["name", "persona", "voice_notes"]);

        var character = document.RootElement.GetProperty("recurring_characters")[0];
        TopLevelNames(character).ShouldBe(["id", "name", "description", "first_episode_id"]);

        var place = document.RootElement.GetProperty("places")[0];
        TopLevelNames(place).ShouldBe(["id", "name", "description"]);

        var rule = document.RootElement.GetProperty("rules")[0];
        TopLevelNames(rule).ShouldBe(["id", "text"]);

        var summary = document.RootElement.GetProperty("episode_summaries")[0];
        TopLevelNames(summary).ShouldBe(["episode_id", "title", "summary", "structure", "approved_at"]);
    }

    [Fact]
    public void BibleJson_ContentDocument_KeepsTheServerSchemaVersionAndDerivedSummary()
    {
        var json = BibleJson.Serialize(FullContent());

        json.ShouldContain("\"schema_version\":\"bible.v1\"");

        using var document = JsonDocument.Parse(json);
        var summary = document.RootElement.GetProperty("episode_summaries")[0];
        summary.GetProperty("episode_id").GetGuid().ShouldBe(EpisodeId);
        summary.GetProperty("approved_at").GetDateTimeOffset().ShouldBe(Now);

        // The structure block is the D3 vocabulary of the story version, snake_case like everything else.
        var structure = summary.GetProperty("structure");
        TopLevelNames(structure).ShouldBe(["sub_genre", "setting", "opening", "twist_type", "narrator_mode", "beats"]);
        structure.GetProperty("beats")[0].GetString().ShouldBe("setup");
    }

    [Fact]
    public void BibleJson_ContentDocument_RoundTripsVerbatim()
    {
        var content = FullContent();
        var json = BibleJson.Serialize(content);

        var restored = BibleJson.DeserializeContent(json);

        // A record with list members has no value equality, so the canonical document is the comparison.
        BibleJson.Serialize(restored).ShouldBe(json);
    }

    [Fact]
    public void BibleJson_EmptyContent_IsTheDocumentTheBackfillWrites()
    {
        var json = BibleJson.Serialize(SeriesBibleContent.Empty);

        json.ShouldBe(
            "{\"schema_version\":\"bible.v1\",\"narrator\":{\"name\":null,\"persona\":\"\",\"voice_notes\":\"\"},"
            + "\"recurring_characters\":[],\"places\":[],\"rules\":[],\"episode_summaries\":[]}");
        BibleJson.DeserializeContent(json).Narrator.Persona.ShouldBe(string.Empty);
        BibleJson.DeserializeContent(json).RecurringCharacters.ShouldBeEmpty();
    }

    [Fact]
    public void BibleJson_Utf8ByteCount_MeasuresTheCanonicalDocumentsBytes()
    {
        var content = FullContent();
        var json = BibleJson.Serialize(content);

        // The canonical serialiser escapes every non-ASCII character (the non-relaxed encoder of plan §3.3), so a
        // Vietnamese document's canonical form is pure ASCII and its UTF-8 byte count equals its length — the
        // size the `MaxContentBytes` budget is counted over.
        json.ShouldAllBe(character => character < 128);
        BibleJson.Utf8ByteCount(content).ShouldBe(Encoding.UTF8.GetByteCount(json));

        // The budget grows with the document: one more entry costs its whole JSON, and one more ASCII character
        // costs exactly one byte.
        var bigger = content with { Rules = [.. content.Rules, new BibleRule("rl2", "Không nói tên người chết hai lần.")] };
        BibleJson.Utf8ByteCount(bigger).ShouldBeGreaterThan(BibleJson.Utf8ByteCount(content));
        BibleJson.Utf8ByteCount(content with { Narrator = content.Narrator with { Persona = content.Narrator.Persona + "x" } })
            .ShouldBe(BibleJson.Utf8ByteCount(content) + 1);
    }

    [Fact]
    public void BibleProposal_Serialisation_RoundTrips()
    {
        var proposal = new BibleProposal(
            [
                new BibleOp(BibleOpKind.AddSummary, "Một câu chuyện.", "Tập một", null, null, "vì sao"),
                new BibleOp(BibleOpKind.AddCharacter, null, "Bà Tư", "Người giữ nhà.", null, null),
                new BibleOp(BibleOpKind.AddPlace, null, "Căn nhà bên sông", "Nhà gỗ hai tầng.", null, null),
                new BibleOp(BibleOpKind.AddRule, null, null, null, "Không nói tên thật.", null),
            ],
            3,
            Now);

        var json = BibleJson.SerializeProposal(proposal);

        using var document = JsonDocument.Parse(json);
        TopLevelNames(document.RootElement).ShouldBe(["ops", "based_on_version", "proposed_at"]);
        json.ShouldContain("\"kind\":\"add_summary\"");
        json.ShouldContain("\"kind\":\"add_character\"");
        json.ShouldContain("\"kind\":\"add_place\"");
        json.ShouldContain("\"kind\":\"add_rule\"");
        json.ShouldContain("\"based_on_version\":3");

        var op = document.RootElement.GetProperty("ops")[0];
        TopLevelNames(op).ShouldBe(["kind", "summary", "name", "description", "text", "reason"]);

        var restored = BibleJson.DeserializeProposal(json);

        restored.ShouldNotBeNull();
        BibleJson.SerializeProposal(restored!).ShouldBe(json);
        restored!.Ops.Count.ShouldBe(4);
        restored.Ops[0].Kind.ShouldBe(BibleOpKind.AddSummary);
        restored.Ops[0].Summary.ShouldBe("Một câu chuyện.");
        restored.Ops[0].Name.ShouldBe("Tập một", "the title of a summary op travels in `name`");
        restored.Ops[3].Text.ShouldBe("Không nói tên thật.");
    }

    [Fact]
    public void BibleJson_ProposalDocument_WithoutAPendingProposal_IsNull()
    {
        BibleJson.DeserializeProposal(null).ShouldBeNull();
        BibleJson.DeserializeProposal(string.Empty).ShouldBeNull();
    }

    [Fact]
    public void BibleJson_OpKindOutsideTheClosedList_Throws()
    {
        // A stored or model-returned document from another build must fail loudly, never load as a silent op.
        var json = "{\"ops\":[{\"kind\":\"delete_character\",\"summary\":null,\"name\":\"Bà Tư\",\"description\":null,"
            + "\"text\":null,\"reason\":null}],\"based_on_version\":1,\"proposed_at\":\"2026-10-14T09:30:00+00:00\"}";

        Should.Throw<JsonException>(() => BibleJson.DeserializeProposal(json));
    }

    private static IEnumerable<string> TopLevelNames(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name);

    private static SeriesBibleContent FullContent() => new(
        SeriesBibleContent.V1,
        new BibleNarrator("Người kể chuyện", "Kể chuyện đêm khuya cho người lớn.", "Giọng trầm, chậm."),
        [new BibleCharacter("ch1", "Bà Tư", "Người giữ căn nhà cũ.", EpisodeId)],
        [new BiblePlace("pl1", "Căn nhà bên sông", "Nhà gỗ hai tầng.")],
        [new BibleRule("rl1", "Không nói tên người chết.")],
        [new BibleEpisodeSummary(
            EpisodeId,
            "Tiếng gõ lúc nửa đêm",
            "Một người trở về căn nhà cũ và nghe thấy tiếng gõ.",
            new StoryStructureTags("haunted_house", "rural_house", "night", "revenant", "first_person", ["setup", "clue", "twist", "reveal"]),
            Now)]);
}
