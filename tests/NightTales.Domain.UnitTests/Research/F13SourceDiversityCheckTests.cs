using NightTales.Application.Common.Errors;
using NightTales.Domain.Research;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Research;

/// <summary>
/// The F13 source-diversity gate of approving a fact sheet (02.5.4-T1, README F13): the fact count is checked
/// first, then the cited sources must resolve to at least two distinct fetched documents — one page cited
/// twice is one source, a user note is no source, and an unfetched, deleted or unknown source never counts.
/// </summary>
/// <remarks>
/// Pure unit tests: the facts and the metadata of the episode's sources are arguments.
/// </remarks>
public sealed class F13SourceDiversityCheckTests
{
    private static readonly Guid Page1 = Guid.Parse("0197c000-0000-7000-8000-0000000000e1");
    private static readonly Guid Page2 = Guid.Parse("0197c000-0000-7000-8000-0000000000e2");
    private static readonly Guid Page3 = Guid.Parse("0197c000-0000-7000-8000-0000000000e3");
    private static readonly Guid NoteId = Guid.Parse("0197c000-0000-7000-8000-0000000000e4");
    private static readonly Guid UnknownId = Guid.Parse("0197c000-0000-7000-8000-0000000000e5");

    private static readonly string ShaA = new('a', 64);
    private static readonly string ShaB = new('b', 64);
    private static readonly string ShaC = new('c', 64);

    [Fact]
    public void Check_MinimumIsFiveFactsAndTwoDistinctSources()
    {
        F13SourceDiversityCheck.MinFacts.ShouldBe(5);
        F13SourceDiversityCheck.MinDistinctSources.ShouldBe(2);
    }

    [Fact]
    public void Check_ZeroFacts_ReturnsSourcesInsufficient()
    {
        var result = F13SourceDiversityCheck.Check([], Sources(Page(Page1, ShaA), Page(Page2, ShaB)));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(F13SourceDiversityCheck.SourcesInsufficientCode);
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Conflict);
    }

    [Fact]
    public void Check_FourFacts_ReturnsSourcesInsufficient()
    {
        // The generated draft may hold fewer than five facts (the validator enforces only the 40 cap), and the
        // hand-over of 02.5.3-T2 is that approve must refuse it (S31).
        var result = F13SourceDiversityCheck.Check(
            Facts(4, Page1, Page2),
            Sources(Page(Page1, ShaA), Page(Page2, ShaB)));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(F13SourceDiversityCheck.SourcesInsufficientCode);
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Conflict);
    }

    [Fact]
    public void Check_FiveFacts_CitingOneFetchedPage_ReturnsStorySourcesRequired()
    {
        var result = F13SourceDiversityCheck.Check(Facts(5, Page1), Sources(Page(Page1, ShaA)));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(F13SourceDiversityCheck.StorySourcesRequiredCode);
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Conflict);
    }

    [Fact]
    public void Check_TwoPagesWithTheSameText_ReturnStorySourcesRequired()
    {
        // Same content_sha256 means the same document: the second source is not a second source (F13).
        var result = F13SourceDiversityCheck.Check(
            Facts(5, Page1, Page2),
            Sources(Page(Page1, ShaA), Page(Page2, ShaA)));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(F13SourceDiversityCheck.StorySourcesRequiredCode);
    }

    [Fact]
    public void Check_TwoDistinctCitedFetchedPages_Succeed()
    {
        var result = F13SourceDiversityCheck.Check(
            Facts(5, Page1, Page2),
            Sources(Page(Page1, ShaA), Page(Page2, ShaB)));

        result.IsError.ShouldBeFalse();
    }

    [Fact]
    public void Check_ExactlyFiveFacts_Succeed()
    {
        var result = F13SourceDiversityCheck.Check(
            Facts(F13SourceDiversityCheck.MinFacts, Page1, Page2),
            Sources(Page(Page1, ShaA), Page(Page2, ShaB)));

        result.IsError.ShouldBeFalse();
    }

    [Fact]
    public void Check_DifferentFactsCiteTheTwoPages_Succeed()
    {
        // "each cited by ≥ 1 kept fact": the two documents need not sit in the same fact.
        var facts = new List<FactItem>
        {
            Fact("f1", Page1),
            Fact("f2", Page1),
            Fact("f3", Page2),
            Fact("f4", Page1),
            Fact("f5", Page2),
        };

        var result = F13SourceDiversityCheck.Check(facts, Sources(Page(Page1, ShaA), Page(Page2, ShaB)));

        result.IsError.ShouldBeFalse();
    }

    [Fact]
    public void Check_DistinctDocumentThatNoFactCites_DoesNotCount()
    {
        // Fetched and different, but nothing cites it: the rule counts documents the sheet actually uses.
        var result = F13SourceDiversityCheck.Check(
            Facts(5, Page1),
            Sources(Page(Page1, ShaA), Page(Page2, ShaB)));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(F13SourceDiversityCheck.StorySourcesRequiredCode);
    }

    [Fact]
    public void Check_AUserNote_NeverCounts()
    {
        var result = F13SourceDiversityCheck.Check(
            Facts(5, Page1, NoteId),
            Sources(Page(Page1, ShaA), UserNote(NoteId, ShaB)));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(F13SourceDiversityCheck.StorySourcesRequiredCode);
    }

    [Fact]
    public void Check_OnlyUserNotes_ReturnStorySourcesRequired()
    {
        var result = F13SourceDiversityCheck.Check(
            Facts(5, NoteId, Page2),
            Sources(UserNote(NoteId, ShaA), UserNote(Page2, ShaB)));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(F13SourceDiversityCheck.StorySourcesRequiredCode);
    }

    [Theory]
    [InlineData(StorySourceStatus.Pending)]
    [InlineData(StorySourceStatus.Failed)]
    [InlineData(StorySourceStatus.Rejected)]
    public void Check_UnfetchedPage_NeverCounts(StorySourceStatus status)
    {
        var result = F13SourceDiversityCheck.Check(
            Facts(5, Page1, Page2),
            Sources(Page(Page1, ShaA), Page(Page2, ShaB, status)));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(F13SourceDiversityCheck.StorySourcesRequiredCode);
    }

    [Fact]
    public void Check_CitedSourceMissingFromTheDictionary_IsIneligible()
    {
        // A source deleted after the sheet was written is simply absent from the map: it cannot support the
        // approval any more.
        var result = F13SourceDiversityCheck.Check(
            Facts(5, Page1, UnknownId),
            Sources(Page(Page1, ShaA)));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(F13SourceDiversityCheck.StorySourcesRequiredCode);
    }

    [Fact]
    public void Check_NullContentHash_NeverCounts()
    {
        var result = F13SourceDiversityCheck.Check(
            Facts(5, Page1, Page2),
            Sources(Page(Page1, ShaA), Page(Page2, null)));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(F13SourceDiversityCheck.StorySourcesRequiredCode);
    }

    [Fact]
    public void Check_TwoPagesWithNoTextAtAll_ReturnStorySourcesRequired()
    {
        var result = F13SourceDiversityCheck.Check(
            Facts(5, Page1, Page2),
            Sources(Page(Page1, null), Page(Page2, null)));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(F13SourceDiversityCheck.StorySourcesRequiredCode);
    }

    [Fact]
    public void Check_AFactCitingTheSameSourceTwice_CountsItOnce()
    {
        var result = F13SourceDiversityCheck.Check(
            Facts(5, Page1, Page1),
            Sources(Page(Page1, ShaA)));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(F13SourceDiversityCheck.StorySourcesRequiredCode);
    }

    [Fact]
    public void Check_OneSourceCitedByEveryFact_CountsOnce()
    {
        var facts = new List<FactItem>
        {
            Fact("f1", Page1),
            Fact("f2", Page1),
            Fact("f3", Page1),
            Fact("f4", Page1),
            Fact("f5", Page1),
        };

        var result = F13SourceDiversityCheck.Check(facts, Sources(Page(Page1, ShaA), Page(Page2, ShaB)));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(F13SourceDiversityCheck.StorySourcesRequiredCode);
    }

    [Fact]
    public void Check_ThreeDistinctCitedPages_Succeed()
    {
        var result = F13SourceDiversityCheck.Check(
            Facts(5, Page1, Page2, Page3),
            Sources(Page(Page1, ShaA), Page(Page2, ShaB), Page(Page3, ShaC)));

        result.IsError.ShouldBeFalse();
    }

    [Fact]
    public void Check_FactsWithoutSources_ReturnStorySourcesRequired()
    {
        // A stored fact always cites at least one source; the check must not throw if one does not.
        var result = F13SourceDiversityCheck.Check(Facts(5), Sources(Page(Page1, ShaA)));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(F13SourceDiversityCheck.StorySourcesRequiredCode);
    }

    [Fact]
    public void Check_Codes_MatchTheErrorCatalogue()
    {
        // The Domain may not reference Application/Common/Errors/ErrorCodes.cs; this test is the join.
        F13SourceDiversityCheck.SourcesInsufficientCode.ShouldBe(ErrorCodes.FactSheetSourcesInsufficient);
        F13SourceDiversityCheck.StorySourcesRequiredCode.ShouldBe(ErrorCodes.StorySourcesRequired);
    }

    [Fact]
    public void Check_NullArguments_Throw()
    {
        Should.Throw<ArgumentNullException>(() => F13SourceDiversityCheck.Check(null!, Sources()));
        Should.Throw<ArgumentNullException>(() => F13SourceDiversityCheck.Check([], null!));
    }

    [Fact]
    public void Check_MoreThanMinFacts_IsAccepted()
    {
        var result = F13SourceDiversityCheck.Check(
            Facts(40, Page1, Page2),
            Sources(Page(Page1, ShaA), Page(Page2, ShaB)));

        result.IsError.ShouldBeFalse();
    }

    private static FactItem Fact(string id, params Guid[] sourceIds) =>
        new(
            id,
            $"Statement of {id}.",
            sourceIds,
            [.. sourceIds.Select((_, index) => $"s{index + 1}")],
            null,
            FactConfidence.High,
            FactOrigin.Model);

    private static IReadOnlyList<FactItem> Facts(int count, params Guid[] sourceIds) =>
        [.. Enumerable.Range(1, count).Select(number => Fact($"f{number}", sourceIds))];

    private static F13SourceInfo Page(Guid sourceId, string? contentSha256, StorySourceStatus status = StorySourceStatus.Fetched) =>
        new(sourceId, StorySourceKind.Url, status, contentSha256);

    private static F13SourceInfo UserNote(Guid sourceId, string? contentSha256) =>
        new(sourceId, StorySourceKind.UserNote, StorySourceStatus.Fetched, contentSha256);

    private static Dictionary<Guid, F13SourceInfo> Sources(params F13SourceInfo[] sources) =>
        sources.ToDictionary(source => source.SourceId);
}
