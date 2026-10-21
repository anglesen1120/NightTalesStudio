using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Compliance.Deletion;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Compliance;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Compliance;

/// <summary>
/// The episode deletion scope (<c>EpisodeDeletionScopeHandler</c>) removes the episode's <c>render_jobs</c> rows: the
/// episode delete cascades to them through the composite tenant foreign key, and nothing else is touched.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RenderJobsDeletionScopeTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EpisodeScope_RemovesTheEpisodesRenderJobs_AndLeavesOtherRenderJobsIntact()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);

        var userId = Guid.CreateVersion7();
        var actor = new TestCurrentUser { Id = userId };
        // fk_series_asp_net_users_user_id: the user row must exist before the series points at it.
        await Database.CreateUserBuilder(actor).CreateAsync(userId, ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(userId, ct: Ct);
        var doomed = await new EpisodeBuilder(Database, actor).CreateAsync(userId, series.Id, ct: Ct);
        var kept = await new EpisodeBuilder(Database, actor).CreateAsync(userId, series.Id, ct: Ct);

        var doomedJob = Guid.CreateVersion7();
        SeedRenderJob(doomedJob, userId, doomed.Id, SeedStoryVersion(userId, doomed.Id));
        var keptJob = Guid.CreateVersion7();
        SeedRenderJob(keptJob, userId, kept.Id, SeedStoryVersion(userId, kept.Id));

        var otherUserId = Guid.CreateVersion7();
        var otherActor = new TestCurrentUser { Id = otherUserId };
        await Database.CreateUserBuilder(otherActor).CreateAsync(otherUserId, ct: Ct);
        var otherSeries = await new SeriesBuilder(Database, otherActor).CreateAsync(otherUserId, ct: Ct);
        var otherEpisode = await new EpisodeBuilder(Database, otherActor).CreateAsync(otherUserId, otherSeries.Id, ct: Ct);
        var otherJob = Guid.CreateVersion7();
        SeedRenderJob(otherJob, otherUserId, otherEpisode.Id, SeedStoryVersion(otherUserId, otherEpisode.Id));

        var request = await DeletionRequestFactory.InsertAsync(
            Database,
            userId,
            DeletionScope.Episode,
            doomed.Id,
            DeletionReason.UserRequest,
            harness.Clock.UtcNow - TimeSpan.FromDays(8),
            ct: Ct);

        await harness.RunJobAsync(Ct);

        (await StatusOf(request)).ShouldBe("completed:1:-");
        (await CountWhere($"episode_id = '{doomed.Id}'")).ShouldBe("0", "the deleted episode's render job is gone");
        (await CountWhere($"id = '{doomedJob}'")).ShouldBe("0");
        (await CountWhere($"id = '{keptJob}'")).ShouldBe("1", "another episode of the same user keeps its render job");
        (await CountWhere($"id = '{otherJob}'")).ShouldBe("1", "another user's render job is untouched");
    }

    private async Task<string> StatusOf(Guid id) =>
        (await Database.QueryStringsAsync(
            $"SELECT status || ':' || attempts || ':' || coalesce(last_error_code, '-') FROM data_deletion_requests WHERE id = '{id}'", Ct))
            .Single();

    private async Task<string> CountWhere(string predicate) =>
        (await Database.QueryStringsAsync($"SELECT count(*)::text FROM render_jobs WHERE {predicate}", Ct)).Single();

    private Guid SeedStoryVersion(Guid userId, Guid episodeId)
    {
        var id = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            "INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, "
            + "word_count, content, created_at) VALUES ('" + id + "', '" + userId + "', '" + episodeId + "', 1, "
            + "'edited', 'Title', 'Body', ARRAY[]::text[], 1, '{\"schema_version\":\"story.v1\"}'::jsonb, now())");
        return id;
    }

    private void SeedRenderJob(Guid id, Guid userId, Guid episodeId, Guid storyVersionId)
    {
        SqlProbe.Execute(
            Database,
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, "
            + "percent, params, attempt) VALUES ('" + id + "', '" + userId + "', '" + episodeId + "', '" + storyVersionId
            + "', '" + Guid.CreateVersion7() + "', 'succeeded', 'done', 100, "
            + "'{\"schema\":\"render-params.v1\",\"music\":null}'::jsonb, 1)");
    }
}
