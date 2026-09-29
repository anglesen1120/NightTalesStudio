using Microsoft.EntityFrameworkCore;
using NightTales.Domain.Compliance;
using NightTales.Domain.Stories;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Persistence;

/// <summary>
/// The append-only list columns of 09.2.2-T1 (<c>story_versions.keywords</c>,
/// <c>moderation_events.categories</c>) are exposed as <see cref="IReadOnlyList{T}"/> over a private
/// <c>List&lt;string&gt;</c> field that EF maps through field access. These tests prove the column type
/// and nullability are unchanged, that a row written through the field reads back, and that the public
/// API cannot append to a loaded row.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AppendOnlyCollectionTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task ModerationEvent_Categories_RoundTripAndAreReadOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);

        // The caller keeps ownership of its own list; the factory copies it.
        var categories = new List<string> { "hate", "self-harm" };
        var moderationEvent = ModerationEvent.Record(
            user.Id,
            ModerationSource.Internal,
            "qa-stage",
            categories,
            DateTimeOffset.UtcNow);

        categories.Clear();

        await using (var insert = Database.CreateAppDbContext())
        {
            insert.ModerationEvents.Add(moderationEvent);
            await insert.SaveChangesAsync(ct);
        }

        // Written from the private _categories field: EF has no setter to use.
        SqlProbe.ScalarText(
                Database,
                $"SELECT array_to_string(categories, ',') FROM moderation_events WHERE id = '{moderationEvent.Id}'")
            .ShouldBe("hate,self-harm");

        await using var verify = Database.CreateAppDbContext();
        var loaded = await verify.ModerationEvents.SingleAsync(row => row.Id == moderationEvent.Id, ct);

        loaded.Categories.ShouldBe(["hate", "self-harm"]);
        AssertReadOnlyAndUnchanged(loaded.Categories, ["hate", "self-harm"]);
    }

    [Fact]
    public async Task StoryVersion_Keywords_RoundTripAndAreReadOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: ct);
        var seriesBuilder = new SeriesBuilder(Database, actor);
        var series = await seriesBuilder.CreateAsync(user.Id, ct: ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: ct);
        var id = Guid.CreateVersion7();

        // The behaviour owner (02.2.2-T1) brings the factory; until then the shell is filled through the
        // change tracker, exactly as the other shell builders do.
        var storyVersion = (StoryVersion)Activator.CreateInstance(typeof(StoryVersion), nonPublic: true)!;

        await using (var insert = Database.CreateAppDbContext())
        {
            insert.StoryVersions.Add(storyVersion);

            var entry = insert.Entry(storyVersion);
            entry.Property(nameof(StoryVersion.Id)).CurrentValue = id;
            entry.Property(nameof(StoryVersion.UserId)).CurrentValue = user.Id;
            entry.Property(nameof(StoryVersion.EpisodeId)).CurrentValue = episode.Id;
            entry.Property(nameof(StoryVersion.Version)).CurrentValue = 1;
            entry.Property(nameof(StoryVersion.Source)).CurrentValue = StoryVersionSource.Generated;
            entry.Property(nameof(StoryVersion.Title)).CurrentValue = "qa-title";
            entry.Property(nameof(StoryVersion.Body)).CurrentValue = "qa-body";
            entry.Property(nameof(StoryVersion.Keywords)).CurrentValue = new List<string> { "alpha", "beta" };
            entry.Property(nameof(StoryVersion.WordCount)).CurrentValue = 2;
            entry.Property(nameof(StoryVersion.Content)).CurrentValue = "{}";
            entry.Property(nameof(StoryVersion.CreatedAt)).CurrentValue = DateTimeOffset.UtcNow;

            await insert.SaveChangesAsync(ct);
        }

        SqlProbe.ScalarText(
                Database,
                $"SELECT array_to_string(keywords, ',') FROM story_versions WHERE id = '{id}'")
            .ShouldBe("alpha,beta");

        await using var verify = Database.CreateAppDbContext();
        var loaded = await verify.StoryVersions.SingleAsync(row => row.Id == id, ct);

        loaded.Keywords.ShouldBe(["alpha", "beta"]);
        AssertReadOnlyAndUnchanged(loaded.Keywords, ["alpha", "beta"]);
    }

    /// <summary>
    /// The public API of a loaded row cannot append: the exposed type is
    /// <see cref="IReadOnlyList{T}"/>, and the <see cref="IList{T}"/> view its runtime type implements
    /// rejects the mutation.
    /// </summary>
    private static void AssertReadOnlyAndUnchanged(IReadOnlyList<string> loaded, string[] expected)
    {
        loaded.ShouldBe(expected);

        var asList = loaded as IList<string>;
        asList.ShouldNotBeNull("the property must expose IReadOnlyList<string>, not a mutable List<string>");
        Should.Throw<NotSupportedException>(() => asList.Add("extra"));
        loaded.ShouldBe(expected, "the rejected mutation changed nothing");
    }
}
