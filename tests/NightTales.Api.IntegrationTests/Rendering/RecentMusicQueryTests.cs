using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Rendering;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// <see cref="RecentMusicQuery"/> against the real database layout (05 README D8, 05.3.2-T3): the avoid-recent list of a
/// series, in one statement, tenant-scoped.
/// </summary>
/// <remarks>
/// The <c>render_jobs</c> rows are inserted with raw SQL on purpose: the point of the port is the JSON path of the
/// stored column (<c>params-&gt;'music'-&gt;&gt;'track_id'</c>) and the absence of <c>DISTINCT</c>, so the test controls
/// the stored documents — including a row whose <c>music</c> is an explicit JSON <c>null</c>. The ids are fixed UUIDv7
/// values so the "newest first" order is deterministic even when several rows are written inside one millisecond.
/// </remarks>
/// <param name="database">The per-class database.</param>
[Trait("Category", "Integration")]
public sealed class RecentMusicQueryTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task MostRecentThree_OrderedDescending_AcrossSeries()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = Guid.CreateVersion7();
        var actor = new TestCurrentUser { Id = userId };
        // fk_series_asp_net_users_user_id: the user row must exist before the series points at it.
        await Database.CreateUserBuilder(actor).CreateAsync(userId, ct: ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(userId, ct: ct);
        var otherSeries = await new SeriesBuilder(Database, actor).CreateAsync(userId, title: "Another series", ct: ct);

        var episodeA = await new EpisodeBuilder(Database, actor).CreateAsync(userId, series.Id, ct: ct);
        var episodeB = await new EpisodeBuilder(Database, actor).CreateAsync(userId, series.Id, ct: ct);
        var episodeC = await new EpisodeBuilder(Database, actor).CreateAsync(userId, series.Id, ct: ct);
        var elsewhere = await new EpisodeBuilder(Database, actor).CreateAsync(userId, otherSeries.Id, ct: ct);

        SeedRenderJob(OrderedId(1), userId, episodeA.Id, SeedStoryVersion(userId, episodeA.Id), "track-oldest");
        SeedRenderJob(OrderedId(2), userId, episodeB.Id, SeedStoryVersion(userId, episodeB.Id), "track-middle");
        SeedRenderJob(OrderedId(3), userId, episodeC.Id, SeedStoryVersion(userId, episodeC.Id), "track-newest");
        SeedRenderJob(OrderedId(4), userId, elsewhere.Id, SeedStoryVersion(userId, elsewhere.Id), "track-other-series");

        var counter = new StatementCounter();
        await using var read = ReadContext(actor, counter);
        var query = new RecentMusicQuery(read, actor);

        var recent = await query.GetRecentTrackIdsAsync(series.Id, 3, ct);

        recent.ShouldBe(["track-newest", "track-middle", "track-oldest"]);
        counter.Statements.ShouldBe(1, "the port answers in one SQL statement (acceptance check 9)");
    }

    [Fact]
    public async Task ExcludesNullMusic_AndOtherUsers()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = Guid.CreateVersion7();
        var actor = new TestCurrentUser { Id = userId };
        // fk_series_asp_net_users_user_id: the user row must exist before the series points at it.
        await Database.CreateUserBuilder(actor).CreateAsync(userId, ct: ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(userId, ct: ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(userId, series.Id, ct: ct);
        var version = SeedStoryVersion(userId, episode.Id);

        SeedRenderJob(OrderedId(5), userId, episode.Id, version, "track-mine");
        SeedRenderJob(OrderedId(6), userId, episode.Id, version, trackId: null);

        var otherUserId = Guid.CreateVersion7();
        var otherActor = new TestCurrentUser { Id = otherUserId };
        await Database.CreateUserBuilder(otherActor).CreateAsync(otherUserId, ct: ct);
        var otherSeries = await new SeriesBuilder(Database, otherActor).CreateAsync(otherUserId, ct: ct);
        var otherEpisode = await new EpisodeBuilder(Database, otherActor).CreateAsync(otherUserId, otherSeries.Id, ct: ct);
        SeedRenderJob(
            OrderedId(7),
            otherUserId,
            otherEpisode.Id,
            SeedStoryVersion(otherUserId, otherEpisode.Id),
            "track-theirs");

        await using var read = ReadContext(actor);
        var query = new RecentMusicQuery(read, actor);

        var recent = await query.GetRecentTrackIdsAsync(series.Id, 5, ct);

        recent.ShouldBe(["track-mine"]);

        // A scope with no actor owns no row, so the statement answers nothing rather than everything.
        var anonymous = new TestCurrentUser();
        await using var anonymousRead = ReadContext(anonymous);
        var anonymousQuery = new RecentMusicQuery(anonymousRead, anonymous);

        (await anonymousQuery.GetRecentTrackIdsAsync(series.Id, 5, ct)).ShouldBeEmpty();
    }

    /// <summary>A fixed, ordered UUIDv7-shaped id, so "newest first" does not depend on the ms a row was written in.</summary>
    private static Guid OrderedId(int sequence) => Guid.Parse("019a2c00-0000-7000-8000-00000000000" + sequence);

    /// <summary>Reads <c>render_jobs</c> as the given actor, optionally counting the statements it runs.</summary>
    private ReadDbContext ReadContext(TestCurrentUser actor, DbCommandInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<ReadDbContext>().UseNpgsql(Database.ConnectionString);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new ReadDbContext(options.Options, actor);
    }

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

    private void SeedRenderJob(Guid id, Guid userId, Guid episodeId, Guid storyVersionId, string? trackId)
    {
        var @params = trackId is null
            ? "{\"schema\":\"render-params.v1\",\"music\":null}"
            : "{\"schema\":\"render-params.v1\",\"music\":{\"track_id\":\"" + trackId
                + "\",\"bgm_volume\":0.2,\"attribution\":null}}";

        SqlProbe.Execute(
            Database,
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, "
            + "percent, params, attempt) VALUES ('" + id + "', '" + userId + "', '" + episodeId + "', '" + storyVersionId
            + "', '" + Guid.CreateVersion7() + "', 'succeeded', 'done', 100, '" + @params + "'::jsonb, 1)");
    }

    /// <summary>Counts the statements that touch <c>render_jobs</c> (acceptance check 9).</summary>
    private sealed class StatementCounter : DbCommandInterceptor
    {
        /// <summary>The statements seen so far.</summary>
        public int Statements { get; private set; }

        /// <inheritdoc />
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Count(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Count(DbCommand command)
        {
            if (command.CommandText.Contains("render_jobs", StringComparison.Ordinal))
            {
                Statements++;
            }
        }
    }
}
