using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Episodes;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Npgsql;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// The interleaving <c>DELETE /api/v1/channels/{id}</c> shares with
/// <c>POST /api/v1/episodes/{id}/publications</c> (06.4.1-T1 security review SEC-1/R1, STATUS S66-3): a create
/// that reads the connection <em>unlocked</em> can see <c>active</c>, be overtaken by a disconnect that snapshots
/// the publications not yet inserted, and then commit an <c>awaiting_consent</c> row on a revoked connection that
/// no cascade will ever cancel. The create's own tests cannot catch it: the create is correct in isolation and
/// only the lock ordering makes it safe.
/// </summary>
/// <remarks>
/// <para>
/// Both tests drive the real routes through <see cref="DisconnectChannelHarness"/> — the seeded connection, the
/// fake Google and the host the disconnect tests already use — plus a ready episode seeded the way
/// <c>CreatePublicationTests</c> seeds its world (the builders, never a hand-written row).
/// </para>
/// <para>
/// The concurrent test holds the connection's row lock the way the disconnect does (a raw <c>FOR UPDATE</c> in an
/// open transaction, then the revocation, uncommitted) and proves the create <em>queues</em> on it by reading
/// <c>pg_stat_activity</c>, the same deterministic probe <c>TokenServiceTests</c> uses — no sleep decides the
/// outcome, and a create that took no lock is caught by the assertion instead of by a timeout.
/// </para>
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
[Collection("Process-global host state")]
[Trait("Category", "Integration")]
public sealed class DisconnectChannelRaceTests(TestDatabase database) : IntegrationTest(database)
{
    private const string EpisodesRoute = "/api/v1/episodes";

    /// <summary>The create's published refusal for a connection that is no longer <c>active</c>.</summary>
    private const string ChannelNotActive = "channel_not_active";

    private const string DefaultTitle = "Tập một: chuyến tàu đêm";
    private const string DefaultDescription = "Nội dung mô tả thử nghiệm cho video YouTube.";

    private static readonly string[] DefaultTags = ["đêm", "truyện", "audio"];

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Create_AfterACommittedDisconnect_AnswersChannelNotActiveAndWritesNoPublication()
    {
        // The sequential shape of the same contract: once the disconnect has committed, the connection row is
        // revoked and the create must refuse. It pins the answer and the "nothing written" side on the route
        // pair; the interleave below is what pins the lock that makes it true under concurrency.
        await using var harness = await StartAsync();
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        var episodeId = await SeedReadyEpisodeAsync(harness, userId);

        using var client = harness.ClientFor(userId);
        using var disconnected = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct);

        disconnected.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            await disconnected.Content.ReadAsStringAsync(Ct));

        harness.ChannelHas(channel.Id, "status = 'revoked'").ShouldBeTrue("the disconnect committed");

        using var response = await CreateAsync(client, episodeId, channel.Id);
        var code = await ReadErrorCodeAsync(response);

        code.ShouldBe(ChannelNotActive, "a revoked connection is not a target a publication may be created for");
        PublicationCount(episodeId).ShouldBe(0, "the refusal writes no row at all");
    }

    [Fact]
    public async Task Create_InterleavedWithAnInFlightDisconnect_QueuesOnTheLockAndAnswersChannelNotActive()
    {
        // The race SEC-1 describes: the create starts while a disconnect holds the connection's row lock and has
        // already revoked the row, uncommitted. Reading the row without the lock sees the pre-revoke snapshot
        // (`active`) and inserts; the insert's foreign-key key-share wait then only delays the commit, so the
        // publication lands on a revoked connection that the cascade has already snapshotted. With the lock in
        // the create's read path, the create queues, reads the committed `revoked` row and refuses.
        await using var harness = await StartAsync();
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        var episodeId = await SeedReadyEpisodeAsync(harness, userId);

        using var client = harness.ClientFor(userId);

        // The disconnect's own order, in one transaction: lock the row, revoke it, keep both uncommitted.
        await using var blocker = new NpgsqlConnection(Database.ConnectionString);
        await blocker.OpenAsync(Ct);
        await using var transaction = await blocker.BeginTransactionAsync(Ct);

        await using (var lockCommand = new NpgsqlCommand(
            "SELECT id FROM channel_connections WHERE id = $1 FOR UPDATE",
            blocker,
            transaction))
        {
            lockCommand.Parameters.AddWithValue(channel.Id);
            (await lockCommand.ExecuteScalarAsync(Ct)).ShouldNotBeNull("the seeded connection must be lockable");
        }

        await using (var revokeCommand = new NpgsqlCommand(
            "UPDATE channel_connections SET status = 'revoked', revoked_at = now() WHERE id = $1",
            blocker,
            transaction))
        {
            revokeCommand.Parameters.AddWithValue(channel.Id);
            (await revokeCommand.ExecuteNonQueryAsync(Ct)).ShouldBe(1, "the row the disconnect revokes");
        }

        var create = CreateAsync(client, episodeId, channel.Id);

        // Deterministic: the create is either parked on the lock or done with an answer that is not ours to give.
        await WaitForTheCreateToQueueOnTheLockAsync(Database, create, Ct);

        var earlyAnswer = create.IsCompleted
            ? await DescribeAsync(create)
            : null;

        create.IsCompleted.ShouldBeFalse(
            $"the create must wait for the disconnect's row lock instead of reading the pre-revoke snapshot: {earlyAnswer}");

        await transaction.CommitAsync(Ct);

        using var response = await create;
        var code = await ReadErrorCodeAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        code.ShouldBe(
            ChannelNotActive,
            "the create read the row the disconnect committed, not the snapshot it saw before the lock");
        PublicationCount(episodeId).ShouldBe(
            0,
            "no awaiting_consent row may be written on a connection the cascade already snapshotted");
        harness.ChannelHas(channel.Id, "status = 'revoked'").ShouldBeTrue("the disconnect's commit landed first");
    }

    /// <summary>
    /// One owner with a series and a ready episode — the world a create needs — with no render job and no
    /// publication, so the create under test is the only writer of that episode.
    /// </summary>
    /// <param name="harness">The running harness, whose clock the rows are stamped with.</param>
    /// <param name="userId">The owner of the series and the episode.</param>
    /// <returns>The <c>episodes.id</c> the create route is called on.</returns>
    private async Task<Guid> SeedReadyEpisodeAsync(DisconnectChannelHarness harness, Guid userId)
    {
        var actor = new TestCurrentUser { Id = userId };

        var series = await new SeriesBuilder(Database, actor, harness.Clock).CreateAsync(
            userId,
            title: "Chuỗi cuộc đua 06.4.1",
            createdAt: DisconnectChannelHarness.Start,
            ct: Ct);

        var episode = await new EpisodeBuilder(Database, actor, harness.Clock).CreateAsync(
            userId,
            series.Id,
            brief: "Nội dung tập cuộc đua 06.4.1.",
            status: EpisodeStatus.Ready,
            createdAt: DisconnectChannelHarness.Start,
            ct: Ct);

        return episode.Id;
    }

    /// <summary>Sends <c>POST /api/v1/episodes/{id}/publications</c> with the route's D14 wire members.</summary>
    /// <param name="client">The authenticated client.</param>
    /// <param name="episodeId">The episode the publication is created for.</param>
    /// <param name="channelConnectionId">The connection the create names.</param>
    private static Task<HttpResponseMessage> CreateAsync(
        HttpClient client,
        Guid episodeId,
        Guid channelConnectionId) =>
        client.PostAsJsonAsync(
            $"{EpisodesRoute}/{episodeId}/publications",
            new
            {
                channelConnectionId,
                title = DefaultTitle,
                description = DefaultDescription,
                tags = DefaultTags,
                privacy = "private",
                publishAt = (DateTimeOffset?)null,
            },
            Ct);

    /// <summary>The problem <c>code</c> of a refused request.</summary>
    /// <param name="response">The response, disposed by the caller.</param>
    private static async Task<string> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var document = JsonDocument.Parse(text);

        document.RootElement.TryGetProperty("code", out var code).ShouldBeTrue($"a problem body: {text}");

        return code.GetString() ?? string.Empty;
    }

    /// <summary>A completed request as one text, for the "it answered instead of queueing" failure message.</summary>
    /// <param name="request">The create's task.</param>
    private static async Task<string> DescribeAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;

        return $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}";
    }

    /// <summary>How many <c>publications</c> rows one episode has: zero is the refusal's proof.</summary>
    /// <param name="episodeId">The episode.</param>
    private long PublicationCount(Guid episodeId) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM publications WHERE episode_id = '{episodeId}'");

    /// <summary>
    /// Waits, with a bounded real-time deadline and no sleep in the decision, until the create is either parked in
    /// <c>pg_stat_activity</c> on the blocker's row lock or has already answered. The waiting statement differs by
    /// implementation — the locked read names <c>channel_connections</c>, an unlocked one parks on the insert into
    /// <c>publications</c> behind the same row's foreign key — so both are matched: a create that never even
    /// queues still falls through to the caller's assertion, which is where the regression is named.
    /// </summary>
    /// <param name="database">The class database, whose sessions the probe reads.</param>
    /// <param name="create">The create request under test.</param>
    /// <param name="ct">Cancels the bounded wait.</param>
    private static async Task WaitForTheCreateToQueueOnTheLockAsync(
        TestDatabase database,
        Task<HttpResponseMessage> create,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline && !create.IsCompleted)
        {
            var waiting = SqlProbe.Count(
                database,
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' "
                + "AND datname = current_database() "
                + "AND (query LIKE '%channel_connections%' OR query LIKE '%publications%')");
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(20, ct);
        }
    }

    /// <summary>Starts the harness, whose flag row is written before the host exists.</summary>
    private Task<DisconnectChannelHarness> StartAsync() => DisconnectChannelHarness.StartAsync(Database, Ct);
}
