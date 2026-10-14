using System.Globalization;
using System.Text.Json;
using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Length;
using NightTales.Domain.Stories.Review;
using NightTales.Domain.Usage;
using NightTales.TestKit.Review;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories.Review;

/// <summary>
/// 02.6.1-T2: the frozen seeded-defect set under <c>prompts/fixtures/seeded-defects/</c> is read as shipped and
/// checked against its contract — every file parses, every planted type is one of the thirteen objective
/// <see cref="ReviewIssueType"/>s (never a taste type, <c>revision_drift</c> or <c>factual_care_missing</c>), every
/// objective type is covered by at least one seeded defect, and the draft itself is a schema-valid <c>story.v1</c>
/// document.
/// </summary>
public sealed class SeededDefectFixturesTests
{
    /// <summary>The frozen set is 22 drafts (02.6.1-T2 §Set composition: 17 seeded + 5 clean).</summary>
    private const int ExpectedDrafts = 22;

    private static readonly IReadOnlyList<ReviewIssueType> ObjectiveTypes =
        [.. Enum.GetValues<ReviewIssueType>().Where(ReviewIssueTypes.IsObjective)];

    [Fact]
    public void SeededDefectFixtures_AllParseAndTypesAreObjective()
    {
        var drafts = SeededDraft.LoadAll();

        drafts.Count.ShouldBe(ExpectedDrafts);

        // Ids are unique and equal to the file names: Load already rejects a mismatch, so a duplicate id would be a
        // second file for one fixture.
        drafts.Select(draft => draft.Id).Distinct(StringComparer.Ordinal).Count().ShouldBe(ExpectedDrafts);

        foreach (var draft in drafts)
        {
            draft.Brief.Trim().ShouldNotBeEmpty($"fixture '{draft.Id}' has no brief");
            draft.Draft.Title.Trim().ShouldNotBeEmpty($"fixture '{draft.Id}' has no title");
            draft.Draft.Paragraphs.ShouldNotBeEmpty($"fixture '{draft.Id}' has no paragraph");

            foreach (var defect in draft.SeedDefects)
            {
                // The real closed list, never a second hard-coded copy: the wire name must parse and be objective.
                ReviewIssueTypes.TryParse(defect.Type, out var type)
                    .ShouldBeTrue($"fixture '{draft.Id}' carries a defect type outside the closed review list: '{defect.Type}'");
                ReviewIssueTypes.IsObjective(type)
                    .ShouldBeTrue($"fixture '{draft.Id}' carries a taste defect, which is not a defect: '{defect.Type}'");
                defect.Segment.ShouldMatch("^(title|hook|ending|meta|p:\\d+)$", customMessage: $"fixture '{draft.Id}' has an invalid segment address '{defect.Segment}'");
                defect.Note.Trim().ShouldNotBeEmpty($"fixture '{draft.Id}' has a defect with no note");
            }

            // A true-crime draft that plants fact_ref_invalid/fact_not_in_sheet must carry the accepted facts, or the
            // reviewer has nothing to check the refs against.
            if (draft.SeedDefects.Any(defect => defect.Type is "fact_ref_invalid" or "fact_not_in_sheet"))
            {
                draft.ApprovedFacts.ShouldNotBeEmpty($"true-crime fixture '{draft.Id}' plants a fact defect but carries no facts");
            }

            AssertDraftIsStructurallyValid(draft);
        }

        // Exactly one clean true-crime draft, so factual_care is proven not to over-block (02.6.1-T2 §Set composition).
        drafts.Count(draft => draft.Clean && draft.Topic == Topic.TrueCrime).ShouldBe(1);
    }

    [Fact]
    public void SeededDefectFixtures_EveryObjectiveTypeCovered()
    {
        var drafts = SeededDraft.LoadAll();
        var seeded = drafts.Where(draft => !draft.Clean).ToList();

        seeded.Count.ShouldBe(17);
        seeded.Count(draft => draft.SeedDefects.Count is < 1 or > 3)
            .ShouldBe(0, "every seeded draft must plant 1-3 defects");

        var covered = seeded.SelectMany(draft => draft.SeedDefects).Select(defect => defect.Type).ToHashSet(StringComparer.Ordinal);

        // Every objective type in the T1 enum appears in at least one seeded defect.
        foreach (var type in ObjectiveTypes)
        {
            covered.ShouldContain(ReviewIssueTypes.Wire(type), $"no fixture seeds an objective defect of type '{ReviewIssueTypes.Wire(type)}'");
        }

        // The closed list has seventeen members: thirteen objective and four taste. The two names below are
        // never seeded — factual_care_missing is a separate server const the model cannot return, and
        // revision_drift is platform-emitted when a revision drifts from the approved draft.
        covered.ShouldNotContain(ReviewIssueTypes.FactualCareMissing);
        covered.ShouldNotContain("revision_drift");
    }

    [Fact]
    public void SeededDefectFixtures_FiveCleanDrafts()
    {
        var drafts = SeededDraft.LoadAll();

        var clean = drafts.Where(draft => draft.Clean).ToList();
        clean.Count.ShouldBe(5);
        clean.ShouldAllBe(draft => draft.SeedDefects.Count == 0);

        drafts.Count(draft => !draft.Clean).ShouldBe(17);
    }

    [Fact]
    public void SeededDefectFixtures_MatchFrozenExpectedManifest()
    {
        var expected = LoadExpectedManifest();
        var drafts = SeededDraft.LoadAll();

        drafts.Count.ShouldBe(expected.Drafts);
        drafts.Count(draft => draft.Clean).ShouldBe(expected.CleanDrafts);
        drafts.Count(draft => !draft.Clean).ShouldBe(expected.SeededDrafts);
        drafts.Sum(draft => draft.SeedDefects.Count).ShouldBe(expected.SeededDefects);

        var byType = drafts
            .SelectMany(draft => draft.SeedDefects)
            .GroupBy(defect => defect.Type, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        byType.Count.ShouldBe(expected.DefectsByType.Count, "a seeded defect type is not listed in the frozen manifest");
        foreach (var pair in expected.DefectsByType)
        {
            byType[pair.Key].ShouldBe(
                pair.Value,
                $"seeded '{pair.Key}' count drifted from the frozen manifest; re-freeze it if the fixture edit is intended");
        }

        var frozen = expected.PerDraft.ToDictionary(draft => draft.Id, StringComparer.Ordinal);
        drafts.Count.ShouldBe(frozen.Count);

        foreach (var draft in drafts)
        {
            frozen.ShouldContainKey(draft.Id, $"fixture '{draft.Id}' is missing from the frozen manifest");
            var frozenDraft = frozen[draft.Id];

            draft.Clean.ShouldBe(frozenDraft.Clean, $"fixture '{draft.Id}' clean flag drifted from the frozen manifest");
            Defects(draft).ShouldBe(FrozenDefects(frozenDraft), $"fixture '{draft.Id}' defects drifted from the frozen manifest; re-freeze it if the edit is intended");
        }
    }

    [Fact]
    public void SeededDefectFixtures_PlantedMarkupDefectIsPresent()
    {
        var drafts = SeededDraft.LoadAll();
        var checkedMarkup = 0;
        var checkedRefs = 0;

        foreach (var fixture in drafts)
        {
            foreach (var defect in fixture.SeedDefects)
            {
                if (defect.Type is not ("markdown_or_url_in_text" or "fact_ref_invalid" or "fact_not_in_sheet"))
                {
                    continue;
                }

                var (text, factRefs) = SegmentAt(fixture, defect.Segment);

                switch (defect.Type)
                {
                    case "markdown_or_url_in_text":
                        // The same detector the D3 validator runs over segment text, never a second regex.
                        StoryText.FindMarkup(text).ShouldNotBeNull(
                            $"fixture '{fixture.Id}' plants a markup defect at '{defect.Segment}', but the segment text carries no URL or markdown marker");
                        checkedMarkup++;
                        break;

                    case "fact_ref_invalid":
                        factRefs.ShouldNotBeEmpty(
                            $"fixture '{fixture.Id}' plants 'fact_ref_invalid' at '{defect.Segment}', so the segment must cite a fact ref");
                        factRefs.ShouldContain(
                            id => !fixture.ApprovedFacts.Any(fact => string.Equals(fact.Id, id, StringComparison.Ordinal)),
                            $"fixture '{fixture.Id}' plants 'fact_ref_invalid' at '{defect.Segment}', but every cited id is in the sheet");
                        checkedRefs++;
                        break;

                    case "fact_not_in_sheet":
                        factRefs.ShouldBeEmpty(
                            $"fixture '{fixture.Id}' plants 'fact_not_in_sheet' at '{defect.Segment}', so the segment must cite no fact_ref");
                        checkedRefs++;
                        break;
                }
            }
        }

        // Guard against a vacuous pass if the three planted shapes are removed from the frozen set.
        checkedMarkup.ShouldBeGreaterThan(0, "no fixture plants a markdown_or_url_in_text defect");
        checkedRefs.ShouldBeGreaterThan(0, "no fixture plants a fact defect");
    }

    /// <summary>
    /// The text and fact refs of a defect's D22 segment address (<c>title</c>, <c>hook</c>, <c>ending</c> or
    /// <c>p:N</c>). The three planted shapes above sit on narration segments; a defect at <c>meta</c> is not
    /// addressed here and would fail loudly rather than be silently skipped.
    /// </summary>
    private static (string Text, IReadOnlyList<string> FactRefs) SegmentAt(SeededDraft fixture, string segment) => segment switch
    {
        "title" => (fixture.Draft.Title, []),
        "hook" => (fixture.Draft.Hook.Text, fixture.Draft.Hook.FactRefs),
        "ending" => (fixture.Draft.Ending.Text, fixture.Draft.Ending.FactRefs),
        _ when segment.StartsWith("p:", StringComparison.Ordinal) =>
            (fixture.Draft.Paragraphs[int.Parse(segment[2..], CultureInfo.InvariantCulture)].Text,
                fixture.Draft.Paragraphs[int.Parse(segment[2..], CultureInfo.InvariantCulture)].FactRefs),
        _ => throw new InvalidOperationException(
            $"fixture '{fixture.Id}' plants a defect at '{segment}', which SeededDefectFixtures_PlantedMarkupDefectIsPresent does not address."),
    };

    /// <summary>The distinct fixture defects ordered by type then segment, so the two sides compare without depending on file order.</summary>
    private static (string Type, string Segment)[] Defects(SeededDraft draft) =>
        [.. draft.SeedDefects
            .Select(defect => (defect.Type, defect.Segment))
            .OrderBy(pair => pair.Type, StringComparer.Ordinal)
            .ThenBy(pair => pair.Segment, StringComparer.Ordinal)];

    private static (string Type, string Segment)[] FrozenDefects(ExpectedDraft draft) =>
        [.. draft.Defects
            .Select(pair => (Type: pair[0], Segment: pair[1]))
            .OrderBy(pair => pair.Type, StringComparer.Ordinal)
            .ThenBy(pair => pair.Segment, StringComparer.Ordinal)];

    /// <summary>
    /// Reads <c>tests/NightTales.TestKit/Fixtures/seeded-defects-expected.json</c> the way every other TestKit fixture is
    /// located: by walking up from the test output to <c>NightTales.sln</c> (<see cref="InjectionFixture"/> does the
    /// same). There is no <c>Content</c>/<c>CopyToOutputDirectory</c> rule in any test project, so the manifest is read
    /// from its repository path.
    /// </summary>
    private static ExpectedManifest LoadExpectedManifest()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NightTales.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root (NightTales.sln) above the test output.");

        var path = Path.Combine(root, "tests", "NightTales.TestKit", "Fixtures", "seeded-defects-expected.json");

        return JsonSerializer.Deserialize<ExpectedManifest>(File.ReadAllText(path), ManifestOptions)
            ?? throw new InvalidOperationException("The frozen seeded-defect manifest is empty.");
    }

    /// <summary>The frozen manifest shape; every key is snake_case (02.6.1-T2 Failure table: "Fixture drift").</summary>
    private sealed record ExpectedManifest(
        int Drafts,
        int CleanDrafts,
        int SeededDrafts,
        int SeededDefects,
        IReadOnlyDictionary<string, int> DefectsByType,
        IReadOnlyList<ExpectedDraft> PerDraft);

    /// <summary>One frozen draft: its id, its clean flag and its <c>[type, segment]</c> pairs.</summary>
    private sealed record ExpectedDraft(string Id, bool Clean, IReadOnlyList<string[]> Defects);

    private static readonly JsonSerializerOptions ManifestOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// The blocking <see cref="StoryDraftValidator"/> rule ids a planted defect type legitimately produces in the
    /// draft. Every other blocking rule would be an unlisted objective defect, which would make the measured
    /// precision meaningless. A clean draft must produce no blocking rule at all.
    /// </summary>
    private static readonly Dictionary<string, string[]> ImpliedBlockingRules = new(StringComparer.Ordinal)
    {
        // "outside the tier's token or segment band": a short fixture trips the segment count as well.
        ["length_out_of_range"] = [StoryDraftValidator.LengthOutOfRange, StoryDraftValidator.SegmentCountOutOfRange],
        // "a fact_refs entry names a fact that is not in the sheet's id list".
        ["fact_ref_invalid"] = [StoryDraftValidator.FactRefUnknown],
        // "markdown, a list marker or a URL leaked into narration text".
        ["markdown_or_url_in_text"] = [StoryDraftValidator.TextHasMarkup],
        // "a segment's image keywords break the keyword rules".
        ["keyword_rule_violation"] = [StoryDraftValidator.KeywordInvalid, StoryDraftValidator.KeywordCountOutOfRange],
    };

    /// <summary>
    /// Runs the real D3 validator over a fixture draft, exempting only the blocking rules the fixture's own planted
    /// defects imply (<see cref="ImpliedBlockingRules"/>): the length/segment bands for a <c>length_out_of_range</c>
    /// seed, <c>fact_ref_unknown</c> for <c>fact_ref_invalid</c>, <c>text_has_markup</c> for
    /// <c>markdown_or_url_in_text</c> and the keyword rules for <c>keyword_rule_violation</c>. Every other blocking
    /// rule is an unlisted objective defect.
    /// </summary>
    private static void AssertDraftIsStructurallyValid(SeededDraft fixture)
    {
        var rules = new StoryRules
        {
            Language = fixture.Language,
            Length = LengthTargetResolver.Resolve(fixture.Tier, fixture.Language, PriceLimits.D4Defaults),
            AllowedFactRefs = fixture.ApprovedFacts.Select(fact => fact.Id).ToHashSet(StringComparer.Ordinal),
            StructureVocab = new HashSet<string>(StringComparer.Ordinal),
        };

        var result = StoryDraftValidator.Validate(fixture.Draft, rules);
        var blocking = result.BlockingRuleIds.ToList();

        if (fixture.Clean)
        {
            blocking.ShouldBeEmpty($"clean fixture '{fixture.Id}' must validate with no blocking rule, but got: {string.Join(", ", blocking)}");
            return;
        }

        var allowed = fixture.SeedDefects
            .SelectMany(defect => ImpliedBlockingRules.TryGetValue(defect.Type, out var implied) ? implied : Array.Empty<string>())
            .ToArray();

        foreach (var rule in blocking)
        {
            allowed.ShouldContain(
                rule,
                $"fixture '{fixture.Id}' has an unlisted objective defect (blocking rule '{rule}'); fix the draft or declare it in defects");
        }
    }
}
