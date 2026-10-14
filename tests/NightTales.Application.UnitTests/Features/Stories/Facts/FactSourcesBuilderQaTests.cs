using NightTales.Application.Abstractions.Research;
using NightTales.Application.Features.Stories.Facts;
using NightTales.Domain.Research;
using NightTales.Domain.Stories.Content;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories.Facts;

/// <summary>
/// QA gap tests for <c>FactSourcesBuilder</c> (02.5.5-T1): null-safety of model-shaped segments, dedupe of a
/// missing source cited by several facts, first-use ordering across the content, duplicate unknown refs, and the
/// stored title fallback. Complements <see cref="FactSourcesBuilderTests"/> without duplicating it.
/// </summary>
public sealed class FactSourcesBuilderQaTests
{
    private static readonly Guid SourceA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SourceB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid MissingSource = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private const string NoteTitle = "your note";

    [Fact]
    public void QA_NullFactRefsOnEverySegment_AreTreatedAsNoRefs()
    {
        // A model-shaped JSON segment with no fact_refs property deserialises to null; the builder must not throw.
        var content = new StoryContentV1(
            "Title",
            new StorySegmentV1("Hook.", ["k1"], null!),
            [new StorySegmentV1("Paragraph.", ["k1"], null!)],
            new StorySegmentV1("Ending.", ["k1"], null!),
            "Description.",
            ["tag"],
            [],
            new StoryStructureV1("sub", "setting", "opening", "twist", "first_person"));

        var result = FactSourcesBuilder.Build(content, Sheet([Fact("f1", "s1")], Aliases(("s1", SourceA))), Sources(), NoteTitle);

        result.Sources.ShouldBeEmpty();
        result.UnknownRefs.ShouldBeEmpty();
        result.SkippedSourceIds.ShouldBeEmpty();
    }

    [Fact]
    public void QA_MissingSourceCitedByTwoFacts_IsReportedOnce()
    {
        var sheet = Sheet([Fact("f1", "s1"), Fact("f2", "s2")], Aliases(("s1", MissingSource), ("s2", MissingSource)));
        var content = Draft(hook: ["f1"], paragraphs: [["f2"]]);

        var result = FactSourcesBuilder.Build(content, sheet, Sources(), NoteTitle);

        result.Sources.ShouldBeEmpty();
        result.SkippedSourceIds.ShouldBe([MissingSource], "the same deleted row is reported once, not once per citing fact");
    }

    [Fact]
    public void QA_SourcesAreOrderedByFirstUseInTheContent_NotBySheetOrder()
    {
        // The sheet lists f1 (source A) before f2 (source B), but the content first cites f2.
        var sheet = Sheet([Fact("f1", "s1"), Fact("f2", "s2")], Aliases(("s1", SourceA), ("s2", SourceB)));
        var content = Draft(hook: ["f2"], paragraphs: [["f1"]]);

        var result = FactSourcesBuilder.Build(content, sheet, Sources(), NoteTitle);

        result.Sources.Select(source => source.SourceId).ShouldBe([SourceB, SourceA]);
    }

    [Fact]
    public void QA_RepeatedUnknownRef_IsReportedOnce()
    {
        var content = Draft(hook: ["fX", "fX"], paragraphs: [["fX"]]);

        var result = FactSourcesBuilder.Build(content, Sheet([Fact("f1", "s1")], Aliases(("s1", SourceA))), Sources(), NoteTitle);

        result.UnknownRefs.ShouldBe(["fX"]);
        result.Sources.ShouldBeEmpty();
    }

    [Fact]
    public void QA_UrlSourceWithoutTitle_FallsBackToItsUrl()
    {
        var sources = new Dictionary<Guid, FactSourceMetadata>
        {
            [SourceA] = new(SourceA, StorySourceKind.Url, null, "https://a.test/x"),
        };
        var content = Draft(hook: ["f1"]);

        var result = FactSourcesBuilder.Build(content, Sheet([Fact("f1", "s1")], Aliases(("s1", SourceA))), sources, NoteTitle);

        result.Sources.ShouldHaveSingleItem();
        result.Sources[0].Title.ShouldBe("https://a.test/x");
        result.Sources[0].Url.ShouldBe("https://a.test/x");
    }

    private static ApprovedFactSheet Sheet(IReadOnlyList<FactForWriter> facts, Dictionary<string, Guid> aliases) =>
        new(Guid.CreateVersion7(), 1, facts, aliases);

    private static FactForWriter Fact(string id, params string[] aliases) => new(id, $"Statement {id}.", aliases);

    private static Dictionary<string, Guid> Aliases(params (string Alias, Guid SourceId)[] pairs) =>
        pairs.ToDictionary(pair => pair.Alias, pair => pair.SourceId, StringComparer.Ordinal);

    private static Dictionary<Guid, FactSourceMetadata> Sources() => new()
    {
        [SourceA] = new(SourceA, StorySourceKind.Url, "Article A", "https://a.test/x"),
        [SourceB] = new(SourceB, StorySourceKind.Url, "Article B", "https://b.test/y"),
    };

    private static StoryContentV1 Draft(
        IReadOnlyList<string>? hook = null,
        IReadOnlyList<IReadOnlyList<string>>? paragraphs = null,
        IReadOnlyList<string>? ending = null) =>
        new(
            "Title",
            Segment("Hook.", hook),
            [.. (paragraphs ?? []).Select(factRefs => Segment("Paragraph.", factRefs))],
            Segment("Ending.", ending),
            "Description.",
            ["tag"],
            [],
            new StoryStructureV1("sub", "setting", "opening", "twist", "first_person"));

    private static StorySegmentV1 Segment(string text, IReadOnlyList<string>? factRefs) =>
        new(text, ["k1"], factRefs ?? []);
}
