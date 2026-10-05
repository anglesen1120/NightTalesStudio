using System.Text.Json.Nodes;
using ErrorOr;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Features.Stories;
using NightTales.Application.Features.Stories.Render;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.StoryGeneration;

/// <summary>Exercises the render hand-off against persisted rows without any text/moderation provider or API host.</summary>
[Trait("Category", "Integration")]
public sealed class StoryRenderInputProviderTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(Language.Vi, "vi")]
    [InlineData(Language.En, "en")]
    public async Task ApprovedVersionId_ReturnsBodyAndOrderedKeywordsAndSegmentAddresses(Language language, string wireName)
    {
        var world = await SeedAsync(language);
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        var provider = new StoryRenderInputProvider(db);

        var result = await provider.GetAsync(world.ApprovedVersionId, Ct);

        result.IsError.ShouldBeFalse();
        var input = result.Value;
        input.StoryVersionId.ShouldBe(world.ApprovedVersionId);
        input.Title.ShouldBe("Approved story");
        input.Language.ShouldBe(wireName);
        input.Tier.ShouldBe(LengthTier.Short);
        input.Body.ShouldBe("The hook.\n\nThe first paragraph.\n\nThe second paragraph.\n\nThe ending.");
        input.Keywords.ShouldBe([
            "hook first", "hook second", "paragraph one", "shared keyword", "paragraph two", "shared keyword", "ending last",
        ]);
        input.Segments.Select(segment => segment.Segment).ShouldBe(["hook", "p:0", "p:1", "ending"]);
        input.Segments[0].Keywords.ShouldBe(["hook first", "hook second"]);
        input.Segments[1].Keywords.ShouldBe(["paragraph one", "shared keyword"]);
        input.Segments[2].Keywords.ShouldBe(["paragraph two", "shared keyword"]);
        input.Segments[3].Keywords.ShouldBe(["ending last"]);

        // The caller-selected approval is older than current: the provider must not resolve a fallback.
        input.StoryVersionId.ShouldNotBe(world.CurrentVersionId);
        input.Title.ShouldNotBe("New current story");
    }

    [Fact]
    public async Task BodyMatchesD3Derivation_PreservesPersistedWhitespaceAndBlankLineOrder()
    {
        var draft = Draft() with
        {
            Hook = new StorySegmentV1("  The hook.\nStill the hook.", ["hook"], []),
            Paragraphs = [new StorySegmentV1("First paragraph. ", ["first"], []), new StorySegmentV1("\tSecond paragraph.", ["second"], [])],
            Ending = new StorySegmentV1("The ending.  ", ["ending"], []),
        };
        var world = await SeedAsync(Language.Vi, draft);
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });

        var result = await new StoryRenderInputProvider(db).GetAsync(world.ApprovedVersionId, Ct);

        result.IsError.ShouldBeFalse();
        result.Value.Body.ShouldBe("  The hook.\nStill the hook.\n\nFirst paragraph. \n\n\tSecond paragraph.\n\nThe ending.  ");
        result.Value.Body.ShouldBe(StoryVersion.Derive(draft).Body);
    }

    [Fact]
    public async Task UnknownVersionId_ReturnsNotFoundWithoutCurrentVersionFallback()
    {
        var world = await SeedAsync(Language.Vi);
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });

        var result = await new StoryRenderInputProvider(db).GetAsync(Guid.CreateVersion7(), Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorType.NotFound);
        result.FirstError.Code.ShouldBe("not_found");
    }

    [Fact]
    public async Task ForeignVersionId_ReturnsNotFound()
    {
        var world = await SeedAsync(Language.Vi);
        var other = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct);
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = other.Id });

        var result = await new StoryRenderInputProvider(db).GetAsync(world.ApprovedVersionId, Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorType.NotFound);
        result.FirstError.Code.ShouldBe("not_found");
    }

    private async Task<World> SeedAsync(Language language, StoryContentV1? approvedDraft = null)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, language: language, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor)
            .CreateAsync(user.Id, series.Id, lengthTier: LengthTier.Short, status: EpisodeStatus.StoryApproved, ct: Ct);
        var draft = approvedDraft ?? Draft();
        var approved = Version(user.Id, episode.Id, 1, draft);
        var current = Version(user.Id, episode.Id, 2, draft with { Title = "New current story", Hook = new StorySegmentV1("New current hook.", ["new current keyword"], []) });

        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id }))
        {
            db.StoryVersions.AddRange(approved, current);
            await db.SaveChangesAsync(Ct);
        }

        SqlProbe.Execute(
            Database,
            $"UPDATE episodes SET approved_story_version_id = '{approved.Id}', current_story_version_id = '{current.Id}' WHERE id = '{episode.Id}'");

        return new World(user.Id, approved.Id, current.Id);
    }

    private static StoryVersion Version(Guid userId, Guid episodeId, int number, StoryContentV1 draft) =>
        StoryVersion.Create(
            userId,
            episodeId,
            number,
            StoryVersionSource.Generated,
            draft,
            StoryContentJson.Write(draft, new JsonArray()),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow);

    private static StoryContentV1 Draft() => new(
        "Approved story",
        new StorySegmentV1("The hook.", ["hook first", "hook second"], []),
        [
            new StorySegmentV1("The first paragraph.", ["paragraph one", "shared keyword"], []),
            new StorySegmentV1("The second paragraph.", ["paragraph two", "shared keyword"], []),
        ],
        new StorySegmentV1("The ending.", ["ending last"], []),
        "A persisted story for the render hand-off.",
        [],
        [],
        new StoryStructureV1("ghost story", "old house", "a midnight call", "the narrator", "first_person"));

    private sealed record World(Guid UserId, Guid ApprovedVersionId, Guid CurrentVersionId);
}
