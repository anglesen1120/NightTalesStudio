#pragma warning disable CA1716 // `Structure` mirrors the source namespace under test (02.8.4-T1 plan QA pass).
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Stories.Structure;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Structure;

/// <summary>
/// Exploratory QA of <c>VarietyGuard</c> (02.8.4-T1): the boundary and adversarial cases the task file's
/// failure-handling table implies but the implementation's own suite does not pin — the cap's strict comparison,
/// zero/negative windows, odd model values (unmapped <c>other:</c> prefixes, over-long text, casing and
/// whitespace), order independence, and a single dimension that breaks both verification rules at once.
/// </summary>
/// <remarks>
/// These tests assert the behaviour the shipped guard actually has, including where that behaviour is a documented
/// risk (the ordinal, case-sensitive comparison lets a differently cased duplicate through R1/R2/R3/R4). Every
/// assertion below is a probe of the contract of 02.8.4-T1 §"Rules", never a re-implementation of it.
/// </remarks>
public sealed class VarietyGuardQaTests
{
    private static readonly VarietyOptions Options = VarietyOptions.Default;

    /// <summary>
    /// The guard's comparison is ordinal and case sensitive (VarietyBatchAssessment remarks): a value differing only
    /// in case is a different value, so it is neither counted into the cap nor recognised as the same triple.
    /// </summary>
    [Fact]
    public void VarietyGuardQa_CaseDifference_IsADifferentValue()
    {
        List<EpisodeStructureRef> history = Window(openings: NightHeavyOpenings(), twists: ["return"]);

        // "night" is over the cap (4 of 10 → 5/11 = 45 %), the candidate's "Night" is a value of its own.
        VarietyBatchAssessment batch = VarietyGuard.AssessBatch(history, Options);
        batch.SuggestedForbidden.Opening.ShouldContain("night");

        VarietyDecision lower = VarietyGuard.Evaluate(Candidate(opening: "night"), batch, Options);
        VarietyDecision upper = VarietyGuard.Evaluate(Candidate(opening: "Night"), batch, Options);

        lower.Penalty.ShouldBe(0.5m, "the exact stored value is the one over the cap");
        upper.Penalty.ShouldBe(0m, "a differently cased value is a different value for the ordinal comparison");
        upper.SoftViolations.ShouldBeEmpty();
    }

    /// <summary>
    /// A value is trimmed before it is compared, one direction only: whitespace around the stored value never hides
    /// a repeat, while a case difference does (see <see cref="VarietyGuardQa_CaseDifference_IsADifferentValue"/>).
    /// </summary>
    [Fact]
    public void VarietyGuardQa_Whitespace_IsTrimmedBeforeComparison()
    {
        List<EpisodeStructureRef> history = Window(openings: NightHeavyOpenings(), twists: ["return"]);

        VarietyDecision padded = VarietyGuard.Evaluate(Candidate(opening: "  night  "), VarietyGuard.AssessBatch(history, Options), Options);

        padded.Penalty.ShouldBe(0.5m, "the stored value is trimmed on read, so padding does not evade the cap");
        padded.SoftViolations.ShouldHaveSingleItem().ShouldBe("opening:night");
    }

    /// <summary>
    /// A window of zero or less is an empty window, not an exception: R5 then answers no requirements and no
    /// rejections, so a misconfigured value degrades to "the guard is off" instead of failing a paid run.
    /// </summary>
    /// <param name="windowK">The window size under test.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void VarietyGuardQa_NonPositiveWindow_RejectsNothing(int windowK)
    {
        var options = new VarietyOptions(windowK, Options.TemplateWindowK, Options.TagShareMax, Options.RotationMinCount, true);

        VarietyBatchAssessment batch = VarietyGuard.AssessBatch(
            Window(openings: NightHeavyOpenings(), twists: ["return"]),
            options);

        batch.WindowSize.ShouldBe(0);
        batch.SuggestedRequired.ShouldBe(StructureRequirements.None);
        batch.SuggestedForbidden.IsEmpty.ShouldBeTrue();

        VarietyDecision decision = VarietyGuard.Evaluate(Candidate(opening: "night"), batch, options);
        decision.RejectReason.ShouldBeNull();
        decision.Penalty.ShouldBe(0m);
        decision.SoftViolations.ShouldBeEmpty();
    }

    /// <summary>
    /// The share cap is strict (<c>&gt;</c>): a value that lands exactly on <c>TagShareMax</c> is legal, and one
    /// step further is forbidden. Nine history entries make the arithmetic land on the boundary exactly
    /// (3 + 1 over 9 + 1 is 40 %, 4 + 1 over 10 is 50 %).
    /// </summary>
    [Fact]
    public void VarietyGuardQa_TagShareExactlyAtTheCap_IsNotForbidden()
    {
        string[] onTheCap = ["night", "night", "night", "storm", "letter", "recording", "funeral", "storm", "letter"];
        VarietyBatchAssessment exact = VarietyGuard.AssessBatch(Window(openings: onTheCap, twists: ["return"]), Options);

        exact.WindowSize.ShouldBe(9);
        exact.OpeningCounts["night"].ShouldBe(3);
        exact.SuggestedForbidden.Opening.ShouldNotContain("night", "4 of 10 is exactly 40 %: the comparison is strict");
        VarietyGuard.Evaluate(Candidate(opening: "night"), exact, Options).Penalty.ShouldBe(0m);

        string[] overTheCap = ["night", "night", "night", "night", "storm", "letter", "recording", "funeral", "storm", "letter"];
        VarietyBatchAssessment over = VarietyGuard.AssessBatch(Window(openings: overTheCap, twists: ["return"]), Options);

        over.OpeningCounts["night"].ShouldBe(4);
        over.SuggestedForbidden.Opening.ShouldContain("night", "5 of 11 is over 40 %");
        VarietyGuard.Evaluate(Candidate(opening: "night"), over, Options).Penalty.ShouldBe(0.5m);
    }

    /// <summary>
    /// The assessment and the verdict depend on the window's multiset, never on the order the entries happen to be
    /// read in: the counts are ordinal, the forbidden lists are sorted, and the rotation's tie-break is the ordinal
    /// comparison, so a rotated history yields the identical steering and the identical penalty.
    /// </summary>
    [Fact]
    public void VarietyGuardQa_RotatedHistoryOrder_ProducesTheSameVerdict()
    {
        List<EpisodeStructureRef> history = Window(openings: NightHeavyOpenings(), twists: ["return"]);
        List<EpisodeStructureRef> rotated =
        [
            history[^1],
            .. history.Take(history.Count - 1),
        ];

        VarietyBatchAssessment first = VarietyGuard.AssessBatch(history, Options);
        VarietyBatchAssessment second = VarietyGuard.AssessBatch(rotated, Options);
        IdeaCandidate candidate = Candidate(opening: "night");

        VarietyDecision firstDecision = VarietyGuard.Evaluate(candidate, first, Options);
        VarietyDecision secondDecision = VarietyGuard.Evaluate(candidate, second, Options);

        first.WindowSize.ShouldBe(second.WindowSize);
        first.SuggestedRequired.ShouldBe(second.SuggestedRequired);
        first.SuggestedForbidden.SubGenre.ShouldBe(second.SuggestedForbidden.SubGenre);
        first.SuggestedForbidden.Setting.ShouldBe(second.SuggestedForbidden.Setting);
        first.SuggestedForbidden.Opening.ShouldBe(second.SuggestedForbidden.Opening);
        first.SuggestedForbidden.TwistType.ShouldBe(second.SuggestedForbidden.TwistType);
        first.OpeningCounts.Keys.Order(StringComparer.Ordinal).ShouldBe(second.OpeningCounts.Keys.Order(StringComparer.Ordinal));
        foreach (string value in first.OpeningCounts.Keys)
        {
            first.OpeningCounts[value].ShouldBe(second.OpeningCounts[value]);
        }

        firstDecision.RejectReason.ShouldBe(secondDecision.RejectReason);
        firstDecision.Penalty.ShouldBe(secondDecision.Penalty);
        firstDecision.SoftViolations.ShouldBe(secondDecision.SoftViolations);

        first.SuggestedForbidden.Opening.ShouldBe(
            first.SuggestedForbidden.Opening.Order(StringComparer.Ordinal).ToArray(),
            "the forbidden lists are published in ordinal order, so a caller can compare them");
    }

    /// <summary>
    /// Odd values a model can produce — an empty string, a bare <c>other:</c>, a doubled prefix and an over-long
    /// term — neither throw nor silently pass a rule they should fail: the guard only ever compares them as opaque
    /// strings, and the two prefixes stay distinct from the stored term they name.
    /// </summary>
    [Fact]
    public void VarietyGuardQa_OddModelValues_DoNotThrowAndDoNotBypassARule()
    {
        List<EpisodeStructureRef> history = Window(openings: NightHeavyOpenings(), twists: ["return"]);
        VarietyBatchAssessment batch = VarietyGuard.AssessBatch(history, Options);

        foreach (string odd in new[] { "", "   ", "other:", "other:other:night", new string('x', 200) })
        {
            VarietyDecision decision = VarietyGuard.Evaluate(Candidate(opening: odd), batch, Options);

            decision.RejectReason.ShouldBeNull($"{odd} is not the stored 'night' triple");
            decision.Penalty.ShouldBe(0m, $"{odd} is compared as its own value, so it is not the forbidden 'night'");
        }

        // The one value that *is* the stored one is caught, whatever shape the model wrapped it in.
        VarietyGuard.Evaluate(Candidate(opening: "other:night"), batch, Options).Penalty.ShouldBe(
            0m,
            "other:night is not night: the D3 mapper writes the prefix, the guard never strips it (documented)");
    }

    /// <summary>
    /// One dimension can break both verification rules at once: a written value the plan forbids is reported as the
    /// gate failure <em>and</em> the plan's required value for that dimension is reported missing, while a blank
    /// written value only raises the advisory and <c>narrator_mode</c> is never an issue.
    /// </summary>
    [Fact]
    public void VarietyGuardQa_VerifyWritten_ReportsBothRulesOnOneDimension()
    {
        var plan = new StructurePlan(
            new StructureRequirements("alpha", null, null, null),
            new StructureForbidden(["beta"], [], [], []));

        IReadOnlyList<VarietyIssue> issues = VarietyGuard.VerifyWritten(
            new StoryStructureTags("beta", string.Empty, string.Empty, string.Empty, "third_person", []),
            plan);

        issues.Count.ShouldBe(2);
        issues.ShouldContain(issue =>
            issue.Rule == VarietyGuard.IssueForbiddenUsed && issue.Dimension == VarietyGuard.DimensionSubGenre && issue.Detail == "beta");
        issues.ShouldContain(issue =>
            issue.Rule == VarietyGuard.IssueRequiredMissing && issue.Dimension == VarietyGuard.DimensionSubGenre && issue.Detail == "alpha");
        issues.ShouldAllBe(issue => issue.Dimension != "narrator_mode");

        VarietyGuard.VerifyWritten(
            new StoryStructureTags("alpha", string.Empty, string.Empty, string.Empty, "third_person", []),
            plan).ShouldBeEmpty("the written value is the required one and nothing is forbidden");

        VarietyGuard.VerifyWritten(
            new StoryStructureTags(string.Empty, string.Empty, string.Empty, string.Empty, "third_person", []),
            StructurePlan.Empty).ShouldBeEmpty("an unconstrained plan is never violated");
    }

    /// <summary>A history whose openings make "night" the value over the cap.</summary>
    /// <returns>The ten openings.</returns>
    private static string[] NightHeavyOpenings() =>
        ["night", "night", "night", "night", "storm", "letter", "recording", "funeral", "storm", "letter"];

    /// <summary>
    /// A window of episodes whose three non-asserted dimensions are held constant and far from any candidate the
    /// QA tests build, so only the opening (and the twist when a caller passes one) can fire a rule.
    /// </summary>
    /// <param name="openings">One opening per entry, in the stored order (newest first).</param>
    /// <param name="twists">The twist every entry carries.</param>
    /// <returns>The entries, newest first.</returns>
    private static List<EpisodeStructureRef> Window(string[] openings, string[] twists)
    {
        var history = new List<EpisodeStructureRef>(openings.Length);
        for (int index = 0; index < openings.Length; index++)
        {
            string[] outline = ["setup", $"stage{index}", "twist"];
            history.Add(new EpisodeStructureRef(
                new Guid(index, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]),
                $"Episode {index}",
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(-index),
                new StoryStructureTags(
                    "ghost_train",
                    "bus depot",
                    openings[index],
                    twists[index % twists.Length],
                    "third_person",
                    outline),
                IdeaStructure.Signature(outline)));
        }

        return history;
    }

    /// <summary>
    /// A candidate whose fixed dimensions are absent from every window above, so only the dimension under test can
    /// reject or penalise it.
    /// </summary>
    /// <param name="opening">The opening the test drives.</param>
    /// <returns>The candidate.</returns>
    private static IdeaCandidate Candidate(string opening) =>
        new(
            "The title",
            "The logline",
            "The hook",
            "The setting in words",
            new StoryStructureTags("hollow_manor", "river bank", opening, "mystery_reveal", "third_person", []),
            []);
}
