using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Stories.Structure;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Autopilot.Ideas;

/// <summary>
/// The structure payload of an idea (02.8.1 plan §3.1/§3.3): the outline signature the variety guard keys on
/// and the canonical <c>jsonb</c> round trip of the <c>structure</c> and <c>novelty</c> documents.
/// </summary>
public sealed class IdeaStructureTests
{
    [Fact]
    public void IdeaStructure_Signature_JoinsBeatsWithGreaterThan()
    {
        IdeaStructure.Signature(["setup", "clue", "twist"]).ShouldBe("setup>clue>twist");
    }

    [Fact]
    public void IdeaStructure_Signature_OfOneBeat_IsThatBeat()
    {
        IdeaStructure.Signature(["twist"]).ShouldBe("twist");
    }

    [Fact]
    public void IdeaStructure_Signature_OfNoBeats_IsEmpty()
    {
        IdeaStructure.Signature([]).ShouldBeEmpty();
    }

    [Fact]
    public void IdeaStructure_Signature_NullBeats_Throws() =>
        Should.Throw<ArgumentNullException>(() => IdeaStructure.Signature(null!));

    [Fact]
    public void IdeaStructure_FromCandidate_CarriesTheTagsAndTheSignature()
    {
        var candidate = Candidate();

        var structure = IdeaStructure.From(candidate);

        // The schema's structure block carries no beats, so the candidate's outline is the idea's only beat
        // sequence and the tags are normalised to it (one `beats` key in the stored document).
        structure.Tags.ShouldBeEquivalentTo(candidate.Structure with { Beats = candidate.Beats });
        structure.Tags.Beats.ShouldBe(candidate.Beats);
        structure.Beats.ShouldBe(candidate.Beats);
        structure.BeatSignature.ShouldBe("setup>clue>twist>reveal");
    }

    [Fact]
    public void IdeaStructure_FromCandidate_WithoutBeats_IsAnEmptySignature() =>
        IdeaStructure.From(Candidate(beats: [])).BeatSignature.ShouldBeEmpty();

    [Fact]
    public void IdeaStructure_FromCandidate_NullCandidate_Throws() =>
        Should.Throw<ArgumentNullException>(() => IdeaStructure.From(null!));

    [Fact]
    public void IdeaJson_Structure_RoundTrips()
    {
        var structure = IdeaStructure.From(Candidate());

        var json = IdeaJson.Serialize(structure);
        var read = IdeaJson.DeserializeStructure(json);

        // Field by field rather than ShouldBeEquivalentTo: the stored document's `beats` deserialise to a
        // List while the candidate's outline is a read-only array, and the object-equivalence comparer
        // reports those two as different objects even though every element is equal.
        read.Tags.SubGenre.ShouldBe(structure.Tags.SubGenre);
        read.Tags.Setting.ShouldBe(structure.Tags.Setting);
        read.Tags.Opening.ShouldBe(structure.Tags.Opening);
        read.Tags.TwistType.ShouldBe(structure.Tags.TwistType);
        read.Tags.NarratorMode.ShouldBe(structure.Tags.NarratorMode);
        read.Tags.Beats.ShouldBe(structure.Tags.Beats);
        read.Beats.ShouldBe(structure.Beats);
        read.BeatSignature.ShouldBe("setup>clue>twist>reveal");
        IdeaJson.Serialize(read).ShouldBe(json, "stored == round-tripped");
    }

    [Fact]
    public void IdeaJson_Structure_IsFlat_WithThePlanKeys()
    {
        var json = IdeaJson.Serialize(IdeaStructure.From(Candidate()));

        json.ShouldStartWith(
            "{\"sub_genre\":\"haunted_house\",\"setting\":\"riverside_house\",\"opening\":\"night\","
            + "\"twist_type\":\"twin\",\"narrator_mode\":\"first_person\",\"beats\":",
            Case.Sensitive);
        // The `>` of the signature is escaped by the default (HTML-safe) encoder, which is the stored
        // text's business rather than the value's: the document still reads back as `setup>clue>twist>reveal`.
        json.ShouldEndWith("\"beat_signature\":\"setup\\u003Eclue\\u003Etwist\\u003Ereveal\"}", Case.Sensitive);
        IdeaJson.DeserializeStructure(json).BeatSignature.ShouldBe("setup>clue>twist>reveal");
        json.ShouldNotContain("\"tags\"", Case.Sensitive, "the document is flat (plan §3.1), never nested");
    }

    [Fact]
    public void IdeaJson_Structure_WritesSnakeCaseTagsAndBeats()
    {
        var json = IdeaJson.Serialize(IdeaStructure.From(Candidate()));

        json.ShouldContain("\"sub_genre\":\"haunted_house\"", Case.Sensitive);
        json.ShouldContain("\"narrator_mode\":\"first_person\"", Case.Sensitive);
        json.ShouldContain("\"beats\":[\"setup\",\"clue\",\"twist\",\"reveal\"]", Case.Sensitive);
    }

    [Fact]
    public void IdeaJson_Novelty_RoundTrips()
    {
        var novelty = Novelty();

        var json = IdeaJson.Serialize(novelty);

        json.ShouldContain("\"max_overlap_kind\":\"episode\"", Case.Sensitive);
        json.ShouldContain("\"hook_strength\":0.6", Case.Sensitive);
        json.ShouldContain("\"pending_ideas\":2", Case.Sensitive);
        IdeaJson.DeserializeNovelty(json).ShouldBeEquivalentTo(novelty);
    }

    [Fact]
    public void IdeaJson_Novelty_WithoutJudge_WritesNullAndReadsBack()
    {
        // The degraded judge path of plan §4 U2: the key is written, the value is null, so one stored shape
        // serves both the calibration reader and the scorer.
        var novelty = Novelty() with { Judge = null };

        var json = IdeaJson.Serialize(novelty);

        json.ShouldContain("\"judge\":null", Case.Sensitive);
        IdeaJson.DeserializeNovelty(json)!.Judge.ShouldBeNull();
    }

    [Fact]
    public void IdeaJson_DeserializeNovelty_OfNull_IsNull() => IdeaJson.DeserializeNovelty(null).ShouldBeNull();

    [Fact]
    public void IdeaJson_Serialize_NullPayloads_Throw()
    {
        Should.Throw<ArgumentNullException>(() => IdeaJson.Serialize((IdeaStructure)null!));
        Should.Throw<ArgumentNullException>(() => IdeaJson.Serialize((IdeaNoveltyDetail)null!));
    }

    private static IdeaCandidate Candidate(IReadOnlyList<string>? beats = null) =>
        new(
            "Căn nhà bên sông",
            "Một người trở về căn nhà cũ và nghe tiếng gọi lúc nửa đêm.",
            "Tiếng gọi chỉ cất lên khi đèn tắt.",
            "Căn nhà gỗ hai tầng bên sông, đêm mưa.",
            new StoryStructureTags("haunted_house", "riverside_house", "night", "twin", "first_person", []),
            beats ?? ["setup", "clue", "twist", "reveal"]);

    private static IdeaNoveltyDetail Novelty() =>
        new(
            new IdeaNoveltyLexical(0.22m, "episode", Guid.Parse("0197a000-0000-7000-8000-0000000000e1")),
            new IdeaNoveltyJudge(0.15m, 0.8m, 0.6m, "Ý tưởng mới so với các tập trước.", null),
            new IdeaNoveltyCorpus(100, 2, 10));
}
