using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Features.Publishing.Upload;
using NightTales.Domain.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Google;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// Token expiry across a long stream (PERF-5): the access token is fetched once and an upload can outlive it, so a
/// <c>401</c> is only a dead grant when the token it was sent with is still valid. The handler re-fetches once and
/// re-enters the resume step — never resending the spent stream — while a <c>401</c> on a valid token still expires
/// the connection.
/// </summary>
/// <remarks>
/// The token port is replaced by <see cref="ScriptedTokenService"/>: the assertion is about <em>when</em> the handler
/// asks again, and this harness registers no OAuth client for the seeded connection, so the production
/// <c>TokenService</c> answers <c>youtube_client_broken</c> before it ever reaches the fake's <c>/token</c> endpoint.
/// Everything else — session create, chunked stream, outcome transitions — is the production path.
/// </remarks>
/// <param name="database">The class database, a migrated copy of the run's template.</param>
public sealed class PublishYouTubeJobTokenTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Publish_ExpiredTokenMidStream_RefreshesOnceAndKeepsTheConnectionActive()
    {
        // PERF-5: the bearer was valid when the stream opened (eight minutes left, past the five-minute refresh
        // window) and expired before the 401 arrived. The handler asks Google for a fresh one through the real port,
        // re-probes the session with a new ranged open and finishes, so a healthy connection is not expired by a slow
        // upload.
        var tokens = new ScriptedTokenService(
            expired: new ChannelAccessToken(PublishYouTubeJobHarness.AccessToken, PublishYouTubeJobHarness.Start.AddMinutes(8)),
            fresh: new ChannelAccessToken("canary-refreshed-token", PublishYouTubeJobHarness.Start.AddHours(2)));

        await using var harness = await StartAsync(tokens);
        var world = await SeedReadyAsync(harness);
        harness.Google.Scenario = UploadScenario.AuthErrorOnChunk(2);
        harness.Google.HoldChunks();

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        wait.CancelAfter(TimeSpan.FromSeconds(90));

        var runToken = Ct;
        var run = Task.Run(() => harness.SendAsync(world.PublicationId, runToken), runToken);

        // The first chunk is parked mid-stream; the clock then passes the bearer's expiry, which is the state the 401
        // for the second chunk arrives in.
        await harness.Google.WaitForHeldChunksAsync(1, wait.Token);
        harness.Clock.Advance(TimeSpan.FromMinutes(10));
        harness.Google.ReleaseChunks();

        var outcome = await run;
        outcome.IsError.ShouldBeFalse();
        outcome.Value.ShouldBe(UploadOutcome.Processing, "the re-entered resume completes the stream");

        tokens.Calls.ShouldBe(2, "one fetch at the start and exactly one re-fetch after the expired 401");
        (await harness.ChannelStatusAsync(world.Channel.Id)).ShouldBe("active", "the grant is healthy; only the token aged");
        (await harness.StatusAsync(world.PublicationId)).ShouldBe("processing");
        (await harness.ErrorCodeAsync(world.PublicationId)).ShouldBeEmpty("a completed upload names no error");
    }

    [Fact]
    public async Task Publish_ForbiddenWithAValidToken_StillRequiresReauthentication()
    {
        // The other side of PERF-5: a 401 on a bearer that is still valid is a real refusal (the user revoked the
        // grant), so it keeps the old answer — the connection expires, the row fails and no token is fetched.
        var tokens = new ScriptedTokenService(
            expired: new ChannelAccessToken(PublishYouTubeJobHarness.AccessToken, PublishYouTubeJobHarness.Start.AddHours(1)),
            fresh: new ChannelAccessToken("canary-refreshed-token", PublishYouTubeJobHarness.Start.AddHours(2)));

        await using var harness = await StartAsync(tokens);
        var world = await SeedReadyAsync(harness);
        harness.Google.Scenario = UploadScenario.AuthErrorOnChunk(1);

        var outcome = await harness.SendAsync(world.PublicationId, Ct);

        outcome.IsError.ShouldBeFalse();
        outcome.Value.ShouldBe(
            UploadOutcome.ReauthRequired,
            "a refused grant is a decided failure, not a retry: the row is failed and the connection expired");
        tokens.Calls.ShouldBe(1, "a valid bearer is never refreshed on a 401");
        (await harness.ChannelStatusAsync(world.Channel.Id)).ShouldBe("expired");
        (await harness.StatusAsync(world.PublicationId)).ShouldBe("failed");
    }

    private Task<PublishYouTubeJobHarness> StartAsync(ITokenService tokens) =>
        PublishYouTubeJobHarness.StartAsync(
            Database,
            Ct,
            clock: new FakeClock(PublishYouTubeJobHarness.Start),
            services: collection =>
            {
                collection.RemoveAll<ITokenService>();
                collection.AddSingleton(tokens);
            });

    private static async Task<World> SeedReadyAsync(PublishYouTubeJobHarness harness)
    {
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        var episode = await harness.SeedEpisodeAsync(userId, harness.Blobs.DefaultLength, ct: Ct);
        var publicationId = await harness.SeedPublicationAsync(userId, episode.EpisodeId, channel.Id, ct: Ct);

        return new World(userId, channel, episode, publicationId);
    }

    /// <summary>One seeded world: the owner, its connection, the ready episode and the publication under test.</summary>
    /// <param name="UserId">The owner.</param>
    /// <param name="Channel">The seeded connection.</param>
    /// <param name="Episode">The seeded episode with its final video.</param>
    /// <param name="PublicationId">The row the test drives.</param>
    private sealed record World(
        Guid UserId,
        PublishYouTubeJobHarness.SeededChannel Channel,
        PublishYouTubeJobHarness.SeededEpisode Episode,
        Guid PublicationId);

    /// <summary>
    /// Hands out one bearer first and a different, later-expiring one afterwards, counting the calls: what a run that
    /// outlived its token asks the port for.
    /// </summary>
    private sealed class ScriptedTokenService(ChannelAccessToken expired, ChannelAccessToken fresh) : ITokenService
    {
        private int _calls;

        /// <summary>How many times the handler asked for a token.</summary>
        public int Calls => Volatile.Read(ref _calls);

        /// <inheritdoc />
        public Task<ErrorOr<ChannelAccessToken>> GetAccessTokenAsync(Guid connectionId, CancellationToken ct) =>
            Task.FromResult<ErrorOr<ChannelAccessToken>>(
                Interlocked.Increment(ref _calls) == 1 ? expired : fresh);
    }
}
