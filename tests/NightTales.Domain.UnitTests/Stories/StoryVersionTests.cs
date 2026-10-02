using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories;

/// <summary>The append-only <c>StoryVersion</c> (02.2.2 plan §3.2): the D3 derivation and the factory rules.</summary>
public sealed class StoryVersionTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 3, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void StoryVersion_Derive_BuildsBodyKeywordsAndWordCount()
    {
        var content = Draft();

        var derived = StoryVersion.Derive(content);
        var version = Create(content, StoryVersionSource.Generated, Guid.CreateVersion7());

        derived.Body.ShouldBe("Mở đầu.\n\nĐoạn một.\n\nKết.");
        derived.Keywords.ShouldBe(["opening", "middle"]);
        derived.WordCount.ShouldBe(StoryText.Tokenize(derived.Body).Count);

        version.Body.ShouldBe(derived.Body);
        version.Keywords.ShouldBe(derived.Keywords);
        version.WordCount.ShouldBe(derived.WordCount);
        version.Title.ShouldBe("Tựa");
    }

    [Fact]
    public void StoryVersion_Create_StoresProvenanceAndTheCurrentSchema()
    {
        var runId = Guid.CreateVersion7();
        var parentId = Guid.CreateVersion7();
        var sheetId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var episodeId = Guid.CreateVersion7();

        var version = StoryVersion.Create(
            userId,
            episodeId,
            3,
            StoryVersionSource.Revised,
            Draft(),
            "{\"schema_version\":\"story.v1\"}",
            "gpt-test",
            "rel=1",
            runId,
            parentId,
            sheetId,
            "{\"audit\":1}",
            0.1234m,
            FixedNow);

        version.Id.ShouldNotBe(Guid.Empty);
        version.UserId.ShouldBe(userId);
        version.EpisodeId.ShouldBe(episodeId);
        version.Version.ShouldBe(3);
        version.Source.ShouldBe(StoryVersionSource.Revised);
        version.GenerationRunId.ShouldBe(runId);
        version.ParentVersionId.ShouldBe(parentId);
        version.FactSheetId.ShouldBe(sheetId);
        version.SchemaVersion.ShouldBe("story.v1");
        version.Model.ShouldBe("gpt-test");
        version.PromptVersion.ShouldBe("rel=1");
        version.AuditReport.ShouldBe("{\"audit\":1}");
        version.SimilarityScore.ShouldBe(0.1234m);
        version.CreatedAt.ShouldBe(FixedNow);
    }

    [Fact]
    public void StoryVersion_Create_EditedVersionWithARun_Throws()
    {
        Should.Throw<ArgumentException>(() => Create(Draft(), StoryVersionSource.Edited, Guid.CreateVersion7()));

        // An edit has no run (02.3.1) and is otherwise a normal version.
        Create(Draft(), StoryVersionSource.Edited, null).GenerationRunId.ShouldBeNull();
    }

    [Fact]
    public void StoryVersion_Create_RefusesAnInvalidVersionOrEmptyOwner()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => StoryVersion.Create(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                0,
                StoryVersionSource.Generated,
                Draft(),
                "{}",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                FixedNow));

        Should.Throw<ArgumentException>(
            () => StoryVersion.Create(
                Guid.Empty,
                Guid.CreateVersion7(),
                1,
                StoryVersionSource.Generated,
                Draft(),
                "{}",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                FixedNow));
    }

    [Fact]
    public void StoryVersion_Keywords_AreReadOnlyToTheCaller()
    {
        var version = Create(Draft(), StoryVersionSource.Generated, Guid.CreateVersion7());

        version.Keywords.ShouldBeAssignableTo<IReadOnlyList<string>>();
        (version.Keywords is IList<string> { IsReadOnly: false }).ShouldBeFalse("no caller can add to a saved version's list");
    }

    private static StoryVersion Create(StoryContentV1 content, StoryVersionSource source, Guid? runId) =>
        StoryVersion.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            1,
            source,
            content,
            "{}",
            null,
            null,
            runId,
            null,
            null,
            null,
            null,
            FixedNow);

    private static StoryContentV1 Draft() =>
        new(
            "Tựa",
            new StorySegmentV1("Mở đầu.", ["opening"], []),
            [new StorySegmentV1("Đoạn một.", ["middle"], [], "setup")],
            new StorySegmentV1("Kết.", [], []),
            "Mô tả",
            ["tag"],
            [],
            new StoryStructureV1("sub", "setting", "opening", "twist", "first_person"));
}
