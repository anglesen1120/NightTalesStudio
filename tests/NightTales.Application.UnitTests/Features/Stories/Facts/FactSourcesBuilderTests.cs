using NightTales.Application.Abstractions.Research;
using NightTales.Application.Features.Stories.Facts;
using NightTales.Domain.Common;
using NightTales.Domain.Research;
using NightTales.Domain.Stories.Content;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories.Facts;

/// <summary>
/// <c>FactSourcesBuilder</c> (02.5.5-T1 D2/D3, 02 README F12): the stored <c>fact_sources</c> is built from the approved
/// sheet's aliases and the episode's own source rows, never from anything the model wrote, and a ref the sheet cannot
/// resolve or a source row that is gone is reported instead of failing the run.
/// </summary>
public sealed class FactSourcesBuilderTests
{
    private static readonly Guid SourceA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SourceB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SourceC = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string NoteTitle = "your note";

    [Fact]
    public void FactSourcesBuilder_CollectsRefsFromHookParagraphsAndEnding()
    {
        var sheet = Sheet(
            facts: [Fact("f1", "s1"), Fact("f2", "s2"), Fact("f3", "s3")],
            aliases: Aliases(("s1", SourceA), ("s2", SourceB), ("s3", SourceC)));
        var content = Draft(hook: ["f1"], paragraphs: [["f2"]], ending: ["f3"]);

        var result = FactSourcesBuilder.Build(content, sheet, Sources(), NoteTitle);

        result.UnknownRefs.ShouldBeEmpty();
        result.SkippedSourceIds.ShouldBeEmpty();
        result.Sources.Select(source => source.SourceId).ShouldBe([SourceA, SourceB, SourceC]);
        result.Sources.Select(source => source.Title).ShouldBe(["Article A", "Article B", "Article C"]);
        result.Sources.Select(source => source.Url).ShouldBe(["https://a.test/x", "https://b.test/y", "https://c.test/z"]);
    }

    [Fact]
    public void FactSourcesBuilder_DedupesBySourceId_OrderedByFirstUse()
    {
        var sheet = Sheet(
            facts: [Fact("f1", "s1"), Fact("f2", "s2"), Fact("f3", "s3")],
            aliases: Aliases(("s1", SourceC), ("s2", SourceA), ("s3", SourceC)));

        // SourceC is reached first through f1 (hook) even though its id sorts after SourceA, which f2 cites later.
        var result = FactSourcesBuilder.Build(Draft(hook: ["f1"], paragraphs: [["f2"]], ending: ["f3"]), sheet, Sources(), NoteTitle);

        result.Sources.Select(source => source.SourceId).ShouldBe([SourceC, SourceA], "de-duplicated by source id, ordered by first use");
        result.Sources.Count.ShouldBe(2);
    }

    [Fact]
    public void FactSourcesBuilder_UnknownFactRef_ReportedNotBuilt()
    {
        var sheet = Sheet(facts: [Fact("f1", "s1")], aliases: Aliases(("s1", SourceA)));

        var result = FactSourcesBuilder.Build(Draft(hook: ["f9"], paragraphs: [["f1"]]), sheet, Sources(), NoteTitle);

        result.Sources.Select(source => source.SourceId).ShouldBe([SourceA], "the known ref is still built");
        result.UnknownRefs.ShouldBe(["f9"], "a ref absent from the approved sheet is reported, never resolved");
        result.SkippedSourceIds.ShouldBeEmpty();
    }

    [Fact]
    public void FactSourcesBuilder_SkipsDeletedSource_AndReportsIt()
    {
        var sheet = Sheet(
            facts: [Fact("f1", "s1"), Fact("f2", "s2")],
            aliases: Aliases(("s1", SourceA), ("s2", SourceB)));
        var sources = new Dictionary<Guid, FactSourceMetadata>
        {
            [SourceA] = Metadata(SourceA, StorySourceKind.Url, "Article A", "https://a.test/x"),
        };

        var result = FactSourcesBuilder.Build(Draft(hook: ["f1"], paragraphs: [["f2"]]), sheet, sources, NoteTitle);

        result.Sources.Select(source => source.SourceId).ShouldBe([SourceA]);
        result.SkippedSourceIds.ShouldBe([SourceB], "a cited source whose row is gone is skipped and reported, never an error");
        result.UnknownRefs.ShouldBeEmpty();
    }

    [Fact]
    public void FactSourcesBuilder_NeverUsesModelWrittenUrlsOrIds()
    {
        const string HostileRef = "https://evil.test/leak";
        var sheet = Sheet(facts: [Fact("f1", "s1")], aliases: Aliases(("s1", SourceA)));
        var content = Draft(hook: [HostileRef, SourceA.ToString()], paragraphs: [["f1"]]);

        var result = FactSourcesBuilder.Build(content, sheet, Sources(), NoteTitle);

        result.Sources.ShouldHaveSingleItem();
        result.Sources[0].SourceId.ShouldBe(SourceA);
        result.Sources[0].Url.ShouldBe("https://a.test/x", "the stored url is the row's, never one taken from the model's text");
        result.Sources.ShouldNotContain(source => source.Url == HostileRef);
        result.UnknownRefs.ShouldBe([HostileRef, SourceA.ToString()], "hostile refs are reported verbatim, never resolved to a source");
    }

    [Fact]
    public void FactSourcesBuilder_UserNote_UsesLabelAndNullUrl()
    {
        var sheet = Sheet(facts: [Fact("f1", "s1")], aliases: Aliases(("s1", SourceA)));
        var sources = new Dictionary<Guid, FactSourceMetadata>
        {
            [SourceA] = Metadata(SourceA, StorySourceKind.UserNote, NoteTitle, null),
        };

        var result = FactSourcesBuilder.Build(Draft(hook: ["f1"]), sheet, sources, NoteTitle);

        result.Sources.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            source => source.Title.ShouldBe(NoteTitle),
            source => source.Url.ShouldBeNull());
    }

    [Fact]
    public void FactSourcesBuilder_FactWithTwoAliasesToSameSource_OneEntry()
    {
        var sheet = Sheet(facts: [Fact("f1", "s1", "s2")], aliases: Aliases(("s1", SourceA), ("s2", SourceA)));

        var result = FactSourcesBuilder.Build(Draft(hook: ["f1"]), sheet, Sources(), NoteTitle);

        result.Sources.ShouldHaveSingleItem().SourceId.ShouldBe(SourceA);
    }

    [Fact]
    public void FactSourcesBuilder_AliasMissingFromMap_IsNotBuilt()
    {
        // The guard rejects a sheet whose facts cite an alias the map does not hold, so this state is unreachable in a
        // run; the builder still must not invent a source for it.
        var sheet = Sheet(facts: [Fact("f1", "s1", "s9")], aliases: Aliases(("s1", SourceA)));

        var result = FactSourcesBuilder.Build(Draft(hook: ["f1"]), sheet, Sources(), NoteTitle);

        result.Sources.Select(source => source.SourceId).ShouldBe([SourceA]);
        result.UnknownRefs.ShouldBeEmpty();
        result.SkippedSourceIds.ShouldBeEmpty();
    }

    [Fact]
    public void FactSourcesBuilder_NoSheet_IsEmpty()
    {
        var content = Draft(hook: ["f1"], paragraphs: [["f2"]], ending: ["f3"]);

        var result = FactSourcesBuilder.Build(content, ApprovedFactSheet.None, new Dictionary<Guid, FactSourceMetadata>(), NoteTitle);

        result.Sources.ShouldBeEmpty("a fiction run keeps an empty fact_sources array");
        result.UnknownRefs.ShouldBe(["f1", "f2", "f3"]);
    }

    [Fact]
    public void FactSourcesBuilder_UserNoteTitle_IsLocalizedPerLanguage()
    {
        FactSourcesBuilder.UserNoteTitle(Language.En).ShouldBe("your note");
        FactSourcesBuilder.UserNoteTitle(Language.Vi).ShouldBe("ghi chú của bạn");
    }

    private static ApprovedFactSheet Sheet(IReadOnlyList<FactForWriter> facts, Dictionary<string, Guid> aliases) =>
        new(Guid.CreateVersion7(), 1, facts, aliases);

    private static FactForWriter Fact(string id, params string[] aliases) => new(id, $"Statement {id}.", aliases);

    private static Dictionary<string, Guid> Aliases(params (string Alias, Guid SourceId)[] pairs) =>
        pairs.ToDictionary(pair => pair.Alias, pair => pair.SourceId, StringComparer.Ordinal);

    private static Dictionary<Guid, FactSourceMetadata> Sources() => new()
    {
        [SourceA] = Metadata(SourceA, StorySourceKind.Url, "Article A", "https://a.test/x"),
        [SourceB] = Metadata(SourceB, StorySourceKind.Url, "Article B", "https://b.test/y"),
        [SourceC] = Metadata(SourceC, StorySourceKind.Url, "Article C", "https://c.test/z"),
    };

    // The caller projects Title as title ?? final_url; the builder falls back to Url when neither is known.
    private static FactSourceMetadata Metadata(Guid id, StorySourceKind kind, string? title, string? url) => new(id, kind, title, url);

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
