#pragma warning disable CA1716 // `Structure` mirrors the source namespace under test (02.8.4-T1 plan).
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Stories.Structure;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Structure;

/// <summary>
/// <c>VarietyGuard</c> (02.8.4-T1 Tests table): one test per rule of the guard's contract, R1–R5, the two
/// verification rules and the configuration switch, plus the edge cases the task file's failure-handling table
/// names (cold start, unmapped <c>other:</c> tags, empty beat signatures, window boundaries, determinism).
/// </summary>
/// <remarks>
/// The fixtures are built inline from four five-value vocabularies rotated over ten episodes, so every value of
/// the default window occurs exactly twice: no dimension is over the R2 share cap and no rule fires by accident.
/// A test that wants a rule to fire overrides one episode with <see cref="Episode"/>; a test that wants a clean
/// candidate passes the values it asserts on to <see cref="Candidate"/>.
/// </remarks>
public sealed class VarietyGuardTests
{
    private static readonly VarietyOptions Options = VarietyOptions.Default;

    private static readonly string[] SubGenres =
        ["haunted_house", "cursed_object", "ghost_train", "mirror_world", "hollow_manor"];

    private static readonly string[] Settings = ["old house", "bus depot", "school yard", "river bank", "attic"];

    private static readonly string[] Openings = ["night", "storm", "letter", "recording", "funeral"];

    private static readonly string[] Twists = ["revenge", "mistaken identity", "possession", "sacrifice", "return"];

    [Fact]
    public void VarietyGuard_TripleRepeatWithin10_RejectsWithTripleRepeat()
    {
        List<EpisodeStructureRef> history = Window(10);

        // Episode 3 carries a triple no other window entry has: (haunted_house, storm, possession).
        history[3] = Episode(3, "haunted_house", "river bank", "storm", "possession");

        VarietyDecision decision = Evaluate(
            Candidate(subGenre: "haunted_house", setting: "old house", opening: "storm", twist: "possession"),
            history);

        decision.RejectReason.ShouldBe(IdeaRejectionReason.VarietyTripleRepeat);
        decision.Penalty.ShouldBe(0m);
    }

    [Fact]
    public void VarietyGuard_TripleOutside10_NotRejected()
    {
        List<EpisodeStructureRef> history = Window(10);

        // The same triple as the entry above, but at position 11 — outside WindowK = 10.
        history.Add(Episode(10, "haunted_house", "river bank", "storm", "possession"));

        VarietyDecision decision = Evaluate(
            Candidate(subGenre: "haunted_house", setting: "old house", opening: "storm", twist: "possession"),
            history);

        decision.RejectReason.ShouldBeNull();
        decision.Penalty.ShouldBe(0m);
    }

    [Fact]
    public void VarietyGuard_TemplateRepeatWithin5_RejectsWithTemplateRepeat()
    {
        List<EpisodeStructureRef> history = Window(10);
        history[2] = Episode(2, "ghost_train", "school yard", "letter", "possession", beats: TemplateOutline());

        VarietyDecision decision = Evaluate(
            Candidate(subGenre: "cursed_object", opening: "letter", twist: "possession", beats: TemplateOutline()),
            history);

        decision.RejectReason.ShouldBe(IdeaRejectionReason.VarietyTemplateRepeat);
    }

    [Fact]
    public void VarietyGuard_TemplateRepeatOutside5_NotRejected()
    {
        List<EpisodeStructureRef> history = Window(10);

        // Position 8 is beyond TemplateWindowK = 5: signature, opening and twist all match, but too old.
        history[7] = Episode(7, "ghost_train", "school yard", "letter", "possession", beats: TemplateOutline());

        VarietyDecision decision = Evaluate(
            Candidate(subGenre: "mirror_world", opening: "letter", twist: "possession", beats: TemplateOutline()),
            history);

        decision.RejectReason.ShouldBeNull();
        decision.Penalty.ShouldBe(0m);
    }

    [Fact]
    public void VarietyGuard_SameOpeningAndTwistButDifferentBeats_NotTemplateRepeat()
    {
        List<EpisodeStructureRef> history = Window(10);
        history[2] = Episode(2, "ghost_train", "school yard", "letter", "possession", beats: TemplateOutline());

        VarietyDecision decision = Evaluate(
            Candidate(
                subGenre: "cursed_object",
                opening: "letter",
                twist: "possession",
                beats: ["setup", "build_up", "clue", "twist", "reveal"]),
            history);

        decision.RejectReason.ShouldBeNull();
        decision.Penalty.ShouldBe(0m);
    }

    [Fact]
    public void VarietyGuard_TagAt4Of10_PenaltyAndForbidden()
    {
        string[] openings =
            ["night", "night", "night", "night", "storm", "letter", "recording", "funeral", "storm", "letter"];
        List<EpisodeStructureRef> history =
            [.. Enumerable.Range(0, 10).Select(index => Cyclic(index, opening: openings[index]))];

        VarietyBatchAssessment batch = VarietyGuard.AssessBatch(history, Options);
        VarietyDecision decision = VarietyGuard.Evaluate(
            Candidate(subGenre: "hollow_manor", setting: "attic", opening: "night", twist: "return"),
            batch,
            Options);

        batch.SuggestedForbidden.Opening.ShouldContain("night");
        decision.Penalty.ShouldBe(0.5m);
        decision.RejectReason.ShouldBeNull();
        decision.SoftViolations.ShouldHaveSingleItem().ShouldBe("opening:night");
        VarietyGuard.ToPlan(batch, decision).Forbidden.Opening.ShouldContain("night");
    }

    [Fact]
    public void VarietyGuard_TwoSoftViolations_PenaltyCappedAt075()
    {
        string[] openings =
            ["night", "night", "night", "night", "storm", "letter", "recording", "funeral", "storm", "letter"];
        string[] twists =
            ["revenge", "revenge", "revenge", "revenge", "possession", "sacrifice", "return", "mistaken identity",
             "possession", "sacrifice"];
        List<EpisodeStructureRef> history =
        [
            .. Enumerable.Range(0, 10)
                .Select(index => Cyclic(index, opening: openings[index], twist: twists[index])),
        ];

        VarietyDecision decision = Evaluate(
            Candidate(subGenre: "hollow_manor", setting: "attic", opening: "night", twist: "revenge"),
            history);

        decision.Penalty.ShouldBe(0.75m);
        decision.RejectReason.ShouldBeNull();
        decision.SoftViolations.ShouldBe(["opening:night", "twist:revenge"]);
    }

    [Fact]
    public void VarietyGuard_RotationPicksLeastUsedValue()
    {
        string[] openings =
            ["bravo", "charlie", "delta", "echo", "delta", "alpha", "echo", "charlie", "delta", "echo"];
        List<EpisodeStructureRef> history =
            [.. Enumerable.Range(0, 10).Select(index => Cyclic(index, opening: openings[index]))];

        VarietyBatchAssessment batch = VarietyGuard.AssessBatch(history, Options);

        // `alpha` and `bravo` are both seen once; the ordinal tie-break picks `alpha` even though `bravo`
        // appears first in the history (so insertion order can never decide it).
        batch.SuggestedRequired.Opening.ShouldBe("alpha");
    }

    [Fact]
    public void VarietyGuard_RotationSkipsValueAtOrAboveCap()
    {
        List<EpisodeStructureRef> history =
            [.. Enumerable.Range(0, 10).Select(index => Cyclic(index, opening: "night"))];

        VarietyBatchAssessment batch = VarietyGuard.AssessBatch(history, Options);

        batch.SuggestedForbidden.Opening.ShouldContain("night");
        batch.SuggestedRequired.Opening.ShouldBeNull();
    }

    [Fact]
    public void VarietyGuard_EmptyHistory_NoRequirementsNoRejections()
    {
        VarietyBatchAssessment batch = VarietyGuard.AssessBatch([], Options);

        batch.WindowSize.ShouldBe(0);
        batch.Recent.ShouldBeEmpty();
        batch.LastFive.ShouldBeEmpty();
        batch.SuggestedRequired.ShouldBe(StructureRequirements.None);
        batch.SuggestedForbidden.IsEmpty.ShouldBeTrue();

        VarietyDecision decision = VarietyGuard.Evaluate(Candidate(), batch, Options);

        decision.Penalty.ShouldBe(0m);
        decision.RejectReason.ShouldBeNull();
        decision.SoftViolations.ShouldBeEmpty();
    }

    [Fact]
    public void VarietyGuard_VerifyWritten_ForbiddenTagUsed_ReturnsGateFailure()
    {
        var plan = new StructurePlan(StructureRequirements.None, new StructureForbidden([], [], ["night"], []));
        var written = new StoryStructureTags("haunted_house", "old house", "night", "revenge", "third_person", []);

        IReadOnlyList<VarietyIssue> issues = VarietyGuard.VerifyWritten(written, plan);

        VarietyIssue issue = issues.ShouldHaveSingleItem();
        issue.Rule.ShouldBe(VarietyGuard.IssueForbiddenUsed);
        issue.Dimension.ShouldBe(VarietyGuard.DimensionOpening);
        issue.Detail.ShouldBe("night");
    }

    [Fact]
    public void VarietyGuard_VerifyWritten_MissingRequiredTag_ReturnsAdvisory()
    {
        var plan = new StructurePlan(new StructureRequirements(null, null, "letter", null), StructureForbidden.None);
        var written = new StoryStructureTags(
            "haunted_house", "old house", "other:strange lights", "revenge", "third_person", []);

        IReadOnlyList<VarietyIssue> issues = VarietyGuard.VerifyWritten(written, plan);

        VarietyIssue issue = issues.ShouldHaveSingleItem();
        issue.Rule.ShouldBe(VarietyGuard.IssueRequiredMissing);
        issue.Dimension.ShouldBe(VarietyGuard.DimensionOpening);
        issue.Detail.ShouldBe("letter");
    }

    [Fact]
    public void VarietyGuard_NarratorModeNotARotationDimension()
    {
        List<EpisodeStructureRef> history =
            [.. Enumerable.Range(0, 10).Select(index => Cyclic(index, narratorMode: "first_person"))];

        VarietyBatchAssessment batch = VarietyGuard.AssessBatch(history, Options);

        string?[] rotations =
        [
            batch.SuggestedRequired.SubGenre,
            batch.SuggestedRequired.Setting,
            batch.SuggestedRequired.Opening,
            batch.SuggestedRequired.TwistType,
        ];
        rotations.ShouldNotContain("first_person");
        batch.SuggestedForbidden.SubGenre.ShouldNotContain("first_person");
        batch.SuggestedForbidden.Setting.ShouldNotContain("first_person");
        batch.SuggestedForbidden.Opening.ShouldNotContain("first_person");
        batch.SuggestedForbidden.TwistType.ShouldNotContain("first_person");

        IdeaCandidate firstPerson = Candidate(
            subGenre: "cursed_object", setting: "attic", opening: "night", twist: "possession",
            narratorMode: "first_person");
        IdeaCandidate thirdPerson = firstPerson with { Structure = firstPerson.Structure with { NarratorMode = "third_person" } };

        VarietyDecision first = VarietyGuard.Evaluate(firstPerson, batch, Options);
        VarietyDecision third = VarietyGuard.Evaluate(thirdPerson, batch, Options);
        first.Penalty.ShouldBe(third.Penalty);
        first.RejectReason.ShouldBe(third.RejectReason);
        first.SoftViolations.ShouldBe(third.SoftViolations);

        VarietyGuard
            .VerifyWritten(
                new StoryStructureTags("haunted_house", "old house", "night", "revenge", "first_person", []),
                StructurePlan.Empty)
            .ShouldBeEmpty();
    }

    [Fact]
    public void VarietyGuard_Disabled_NoRejectionsNoPenalty()
    {
        string[] openings =
            ["night", "night", "night", "night", "storm", "letter", "recording", "funeral", "storm", "letter"];
        string[] twists =
            ["revenge", "revenge", "revenge", "revenge", "possession", "sacrifice", "return", "mistaken identity",
             "possession", "sacrifice"];
        List<EpisodeStructureRef> history =
        [
            .. Enumerable.Range(0, 10)
                .Select(index => Cyclic(index, opening: openings[index], twist: twists[index])),
        ];
        IdeaCandidate candidate = Candidate(
            subGenre: "haunted_house", setting: "old house", opening: "night", twist: "revenge");

        VarietyDecision enabled = Evaluate(candidate, history);
        enabled.RejectReason.ShouldBe(IdeaRejectionReason.VarietyTripleRepeat);
        enabled.Penalty.ShouldBe(0.75m);

        VarietyOptions disabled = Options with { Enabled = false };
        VarietyBatchAssessment batch = VarietyGuard.AssessBatch(history, disabled);
        VarietyDecision decision = VarietyGuard.Evaluate(candidate, batch, disabled);

        decision.Penalty.ShouldBe(0m);
        decision.RejectReason.ShouldBeNull();
        decision.SoftViolations.ShouldBeEmpty();

        // The switch turns off rejection and penalty only: the steering the prompt receives is still produced.
        batch.SuggestedForbidden.Opening.ShouldContain("night");
        batch.SuggestedForbidden.TwistType.ShouldContain("revenge");
        batch.SuggestedRequired.SubGenre.ShouldBe("cursed_object");
    }

    [Fact]
    public void VarietyGuard_OtherPrefixedValue_IsNeverRequired()
    {
        // `other:aaa ritual` is seen once, exactly like `tunnel`, and sorts before it ordinally: without the
        // exclusion the rotation would pick the unstable unmapped value.
        string[] subGenres =
            ["other:aaa ritual", "torn_house", "torn_house", "vellum", "vellum", "zephyr", "zephyr", "wallow",
             "wallow", "tunnel"];
        List<EpisodeStructureRef> history =
            [.. Enumerable.Range(0, 10).Select(index => Cyclic(index, subGenre: subGenres[index]))];

        VarietyBatchAssessment batch = VarietyGuard.AssessBatch(history, Options);

        batch.SubGenreCounts.ContainsKey("other:aaa ritual").ShouldBeTrue();
        batch.SuggestedRequired.SubGenre.ShouldBe("tunnel");
        batch.SuggestedForbidden.SubGenre.ShouldNotContain("other:aaa ritual");
    }

    [Fact]
    public void VarietyGuard_OtherPrefixedValue_IsOverCapButNotForbidden()
    {
        string[] openings =
        [
            "other:bbb chant", "other:bbb chant", "other:bbb chant", "other:bbb chant", "other:bbb chant",
            "night", "night", "night", "storm", "storm",
        ];
        List<EpisodeStructureRef> history =
            [.. Enumerable.Range(0, 10).Select(index => Cyclic(index, opening: openings[index]))];

        VarietyBatchAssessment batch = VarietyGuard.AssessBatch(history, Options);

        // Five of ten is 6/11 = 54 %, over the 40 % cap — but an unmapped value is never suggested.
        batch.OpeningCounts["other:bbb chant"].ShouldBe(5);
        batch.SuggestedForbidden.Opening.ShouldBeEmpty();
    }

    [Fact]
    public void VarietyGuard_OtherPrefixedValue_IsComparedAsIsByTripleRepeat()
    {
        List<EpisodeStructureRef> history = Window(10);
        history[0] = Episode(0, "other:aaa ritual", Settings[0], Openings[0], Twists[0]);

        VarietyDecision decision = Evaluate(
            Candidate(subGenre: "other:aaa ritual", setting: "attic", opening: Openings[0], twist: Twists[0]),
            history);

        decision.RejectReason.ShouldBe(IdeaRejectionReason.VarietyTripleRepeat);
        decision.Penalty.ShouldBe(0m);
    }

    [Fact]
    public void VarietyGuard_RotationTieBreak_PicksOrdinallySmallestValue()
    {
        string[] openings =
            ["zulu", "zulu", "bravo", "bravo", "night", "night", "storm", "storm", "letter", "letter"];
        List<EpisodeStructureRef> history =
            [.. Enumerable.Range(0, 10).Select(index => Cyclic(index, opening: openings[index]))];

        VarietyBatchAssessment first = VarietyGuard.AssessBatch(history, Options);
        VarietyBatchAssessment second = VarietyGuard.AssessBatch(history, Options);

        first.SuggestedRequired.Opening.ShouldBe("bravo");
        second.SuggestedRequired.ShouldBe(first.SuggestedRequired);
        second.SuggestedForbidden.SubGenre.ShouldBe(first.SuggestedForbidden.SubGenre);
        second.SuggestedForbidden.Setting.ShouldBe(first.SuggestedForbidden.Setting);
        second.SuggestedForbidden.Opening.ShouldBe(first.SuggestedForbidden.Opening);
        second.SuggestedForbidden.TwistType.ShouldBe(first.SuggestedForbidden.TwistType);
        second.WindowSize.ShouldBe(first.WindowSize);
    }

    [Fact]
    public void VarietyGuard_EmptyAndWhitespaceTagValues_AreIgnored()
    {
        string?[] openings = ["", "", "", "", "", "", "   ", "   ", "night", "night"];
        List<EpisodeStructureRef> history =
            [.. Enumerable.Range(0, 10).Select(index => Cyclic(index, opening: openings[index]))];

        VarietyBatchAssessment batch = VarietyGuard.AssessBatch(history, Options);

        batch.OpeningCounts.Count.ShouldBe(1);
        batch.OpeningCounts.ContainsKey(string.Empty).ShouldBeFalse();
        batch.OpeningCounts.ContainsKey("   ").ShouldBeFalse();
        batch.OpeningCounts["night"].ShouldBe(2);
        batch.SuggestedRequired.Opening.ShouldBe("night");
        batch.SuggestedForbidden.Opening.ShouldBeEmpty();

        // An empty value can never complete the R1 triple, so it can never be a repeat on its own.
        VarietyDecision decision = VarietyGuard.Evaluate(
            Candidate(
                subGenre: SubGenres[0], setting: Settings[0], opening: string.Empty, twist: Twists[0]),
            batch,
            Options);

        decision.RejectReason.ShouldBeNull();
        decision.Penalty.ShouldBe(0m);
    }

    [Fact]
    public void VarietyGuard_HistoryLongerThanWindow_OnlyCountsTheWindow()
    {
        string[] openings =
        [
            "night", "night", "night", "night", "night", "night", "night", "night", "night", "night",
            "storm", "storm", "storm", "storm", "storm",
        ];
        List<EpisodeStructureRef> history =
            [.. Enumerable.Range(0, 15).Select(index => Cyclic(index, opening: openings[index]))];

        VarietyBatchAssessment batch = VarietyGuard.AssessBatch(history, Options);

        batch.WindowSize.ShouldBe(10);
        batch.Recent.Count.ShouldBe(10);
        batch.LastFive.Count.ShouldBe(5);
        batch.OpeningCounts.ContainsKey("storm").ShouldBeFalse();
        batch.OpeningCounts["night"].ShouldBe(10);
        batch.SuggestedForbidden.Opening.ShouldContain("night");
    }

    [Fact]
    public void VarietyGuard_ToPlan_UnionsSoftViolationValuesIntoForbidden()
    {
        VarietyBatchAssessment clean = VarietyGuard.AssessBatch(Window(10), Options);
        clean.SuggestedForbidden.IsEmpty.ShouldBeTrue();

        var decision = new VarietyDecision(
            0.5m, null, ["opening:night", "opening:attic", "opening:night", "twist:other:x"]);
        StructurePlan plan = VarietyGuard.ToPlan(clean, decision);

        plan.Required.ShouldBe(clean.SuggestedRequired);
        plan.Forbidden.Opening.ShouldBe(["attic", "night"]);
        plan.Forbidden.TwistType.ShouldBe(["other:x"]);
        plan.Forbidden.SubGenre.ShouldBeEmpty();
        plan.Forbidden.Setting.ShouldBeEmpty();

        string[] openings =
            ["night", "night", "night", "night", "storm", "letter", "recording", "funeral", "storm", "letter"];
        VarietyBatchAssessment capped = VarietyGuard.AssessBatch(
            [.. Enumerable.Range(0, 10).Select(index => Cyclic(index, opening: openings[index]))],
            Options);
        StructurePlan merged = VarietyGuard.ToPlan(capped, new VarietyDecision(0.5m, null, ["opening:storm"]));

        merged.Forbidden.Opening.ShouldBe(["night", "storm"]);
    }

    [Fact]
    public void VarietyGuard_TemplateRepeat_TakesPrecedenceOverTripleRepeat()
    {
        List<EpisodeStructureRef> history = Window(10);
        history[2] = Episode(2, "ghost_train", "school yard", "letter", "possession", beats: TemplateOutline());

        // R1 and R3 both match episode 2; the more specific R3 is the reason that survives.
        VarietyDecision decision = Evaluate(
            Candidate(
                subGenre: "ghost_train", setting: "school yard", opening: "letter", twist: "possession",
                beats: TemplateOutline()),
            history);

        decision.RejectReason.ShouldBe(IdeaRejectionReason.VarietyTemplateRepeat);
    }

    [Fact]
    public void VarietyGuard_EmptyBeatSignature_IsNeverATemplateRepeat()
    {
        List<EpisodeStructureRef> history = Window(10);
        history[2] = Episode(2, "ghost_train", "school yard", "letter", "possession", beats: TemplateOutline());

        Evaluate(
            Candidate(subGenre: "cursed_object", opening: "letter", twist: "possession", beats: []),
            history).RejectReason.ShouldBeNull();
        Evaluate(
            Candidate(subGenre: "cursed_object", opening: "letter", twist: "possession", beats: [""]),
            history).RejectReason.ShouldBeNull();
        Evaluate(
            Candidate(subGenre: "cursed_object", opening: "letter", twist: "possession", beats: ["   "]),
            history).RejectReason.ShouldBeNull();
    }

    [Fact]
    public void VarietyGuard_StructurePlan_EmptyIsEmpty()
    {
        StructurePlan.Empty.IsEmpty.ShouldBeTrue();
        StructureRequirements.None.ShouldBe(new StructureRequirements(null, null, null, null));
        StructureForbidden.None.IsEmpty.ShouldBeTrue();
        new StructurePlan(new StructureRequirements("haunted_house", null, null, null), StructureForbidden.None)
            .IsEmpty.ShouldBeFalse();
        new StructurePlan(StructureRequirements.None, new StructureForbidden([], ["attic"], [], []))
            .IsEmpty.ShouldBeFalse();
        VarietyGuard.ToPlan(VarietyGuard.AssessBatch([], Options), VarietyDecision.None).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void VarietyGuard_SingleStructuredEpisode_IsStillColdStart()
    {
        List<EpisodeStructureRef> only = [Episode(0, "haunted_house", "old house", "night", "revenge")];

        VarietyBatchAssessment batch = VarietyGuard.AssessBatch(only, Options);
        batch.Recent.ShouldBeEmpty();
        batch.SuggestedRequired.ShouldBe(StructureRequirements.None);
        batch.SuggestedForbidden.IsEmpty.ShouldBeTrue();
        VarietyGuard
            .Evaluate(
                Candidate(subGenre: "haunted_house", setting: "old house", opening: "night", twist: "revenge"),
                batch,
                Options)
            .RejectReason.ShouldBeNull();

        // Entries that carry no structure at all count for nothing either.
        List<EpisodeStructureRef> structureless =
            [.. Enumerable.Range(0, 3).Select(index => Episode(index, "", "", "", "", beats: []))];
        VarietyBatchAssessment emptyBatch = VarietyGuard.AssessBatch(structureless, Options);

        emptyBatch.SuggestedRequired.ShouldBe(StructureRequirements.None);
        emptyBatch.SuggestedForbidden.IsEmpty.ShouldBeTrue();
    }

    /// <summary>The default window: ten episodes whose values rotate over the five-value vocabularies.</summary>
    private static List<EpisodeStructureRef> Window(int count) =>
        [.. Enumerable.Range(0, count).Select(index => Cyclic(index))];

    /// <summary>An episode that takes the rotating vocabulary value of its position in each dimension.</summary>
    private static EpisodeStructureRef Cyclic(
        int index,
        string? subGenre = null,
        string? opening = null,
        string? twist = null,
        string narratorMode = "third_person") =>
        Episode(
            index,
            subGenre ?? SubGenres[index % SubGenres.Length],
            Settings[index % Settings.Length],
            opening ?? Openings[index % Openings.Length],
            twist ?? Twists[index % Twists.Length],
            narratorMode: narratorMode);

    /// <summary>One history entry; its beat signature is derived exactly as the history reader derives it.</summary>
    private static EpisodeStructureRef Episode(
        int index,
        string subGenre,
        string setting,
        string opening,
        string twist,
        string[]? beats = null,
        string narratorMode = "third_person")
    {
        string[] outline = beats ?? Outline(index);

        return new EpisodeStructureRef(
            new Guid(index, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]),
            $"Episode {index}",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(-index),
            new StoryStructureTags(subGenre, setting, opening, twist, narratorMode, outline),
            IdeaStructure.Signature(outline));
    }

    /// <summary>An outline unique to one position, so R3 can only fire when a test asks for it.</summary>
    private static string[] Outline(int index) => ["setup", $"stage{index}", "twist"];

    /// <summary>The outline the template-repeat fixtures share (signature <c>setup&gt;clue&gt;twist</c>).</summary>
    private static string[] TemplateOutline() => ["setup", "clue", "twist"];

    /// <summary>A candidate: no beats by default, so its (empty) signature never matches a template.</summary>
    private static IdeaCandidate Candidate(
        string subGenre = "hollow_manor",
        string setting = "attic",
        string opening = "funeral",
        string twist = "return",
        string narratorMode = "third_person",
        string[]? beats = null)
    {
        string[] outline = beats ?? [];

        return new IdeaCandidate(
            "The title",
            "The logline",
            "The hook",
            "The setting in words",
            new StoryStructureTags(subGenre, setting, opening, twist, narratorMode, outline),
            outline);
    }

    /// <summary>Assesses the history and evaluates the candidate against it in one step.</summary>
    private static VarietyDecision Evaluate(
        IdeaCandidate candidate,
        IReadOnlyList<EpisodeStructureRef> history,
        VarietyOptions? options = null)
    {
        VarietyOptions opt = options ?? Options;

        return VarietyGuard.Evaluate(candidate, VarietyGuard.AssessBatch(history, opt), opt);
    }
}
