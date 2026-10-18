using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Episodes;
using NightTales.Domain.Episodes.Events;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Persistence.Outbox;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Stories;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.VideoReview;

/// <summary>
/// The Worker's <c>video_rejected.v1</c> consumer over a real PostgreSQL 17 (09.7.1-T1, 09 README D6/D11,
/// WT3): a <c>re_render</c> rejection queues exactly one render of the already-approved story, the same
/// message redelivered reserves once, a <c>story</c> rejection queues nothing, and a refused render leaves
/// the outbox row due so the dispatcher retries with backoff.
/// </summary>
/// <remarks>
/// <para>
/// The delivery is the production path: the row is written the way the interceptor writes it, dispatched by
/// the Worker's own <c>OutboxDispatcher</c> through
/// <see cref="WorkerHost.RunOutboxOnceAsync"/>, and the render command runs in the D4 pipeline of the
/// delivery's scope. Only two ports are fakes — the credit ledger, so "reserved once" is observable, and the
/// platform spend cap, so a refusal is deterministic instead of depending on the suite's usage rows.
/// </para>
/// <para>
/// The suite shares one process and one class database, so every assertion is scoped to this test's own user
/// or episode; no count is global.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class VideoReRenderOutboxTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The event the rejection raised.</summary>
    private const string Event = "video_rejected.v1";

    /// <summary>The event the render command raises in its own transaction.</summary>
    private const string RenderRequested = "render_requested.v1";

    private static readonly DateTimeOffset Start = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task VideoReRenderHandler_CreatesExactlyOneRenderJob_ForAReRejectEvent()
    {
        var world = await SeedAsync();
        var ledger = new RecordingLedger();
        var messageId = await InsertRejectionAsync(world, RejectTarget.ReRender);

        await using var host = await ReRenderHostAsync(ledger);
        await host.RunOutboxOnceAsync(Ct);

        var jobs = SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE episode_id = '{world.EpisodeId}'");
        jobs.ShouldBe(1, "one rejection is one render job");

        // The job carries the approved story it renders and the reservation this delivery made: the reserve
        // and the insert share the unit of work (D5), so the id in the row is that one.
        SqlProbe.ScalarText(
                Database,
                $"SELECT story_version_id::text || '|' || reservation_id::text FROM render_jobs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe($"{Id(world.VersionId)}|{ledger.ReservationFor(world.UserId)}");
        ledger.ReservedFor(world.UserId).ShouldBe(1);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = '{RenderRequested}' AND user_id = '{world.UserId}'")
            .ShouldBe(1, "the render command raises its own event once");
        MessageState(messageId).ShouldBe("true|0|true", "the rejection is delivered and processed");

        await QuietRenderRequestsAsync(world);
    }

    [Fact]
    public async Task VideoReRenderHandler_IsIdempotent_AcrossDispatcherRetries()
    {
        var world = await SeedAsync();
        var ledger = new RecordingLedger();
        var messageId = await InsertRejectionAsync(world, RejectTarget.ReRender);

        await using var host = await ReRenderHostAsync(ledger);
        await host.RunOutboxOnceAsync(Ct);
        ledger.ReservedFor(world.UserId).ShouldBe(1);

        // The redelivery a crash mid-message produces (09.2.4 plan §9): the row keeps its id, so the handler
        // sends the same D6 key `video-reject-rerender:{messageId}` and the pipeline must not spend twice.
        await Database.ExecuteAsync(
            $"UPDATE outbox_messages SET processed_at = NULL, next_attempt_at = '{Start:O}' WHERE id = '{messageId}'",
            Ct);
        await host.RunOutboxOnceAsync(Ct);

        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(1, "one rejection is one render job however often the send is retried");
        ledger.ReservedFor(world.UserId).ShouldBe(1, "the D6 key is what makes the retry free");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = '{RenderRequested}' AND user_id = '{world.UserId}'")
            .ShouldBe(1);

        // A redelivery after a committed send is a success, not a failed attempt: the row must be marked
        // processed with no attempt spent, or the dispatcher keeps backing it off and dead-letters a message
        // whose work is already done (D11, `outbox_dead_letters_total`). This is the D6 key's whole point:
        // `video-reject-rerender:{messageId}` identifies the send, so an already-processed key means "sent".
        MessageState(messageId).ShouldBe(
            "true|0|true",
            "the second delivery replays the stored result instead of spending an attempt");

        await QuietRenderRequestsAsync(world);
    }

    [Fact]
    public async Task VideoReRenderHandler_IgnoresStoryTarget()
    {
        var world = await SeedAsync(approved: false);
        var ledger = new RecordingLedger();
        var messageId = await InsertRejectionAsync(world, RejectTarget.Story);

        await using var host = await ReRenderHostAsync(ledger);
        await host.RunOutboxOnceAsync(Ct);

        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(0, "the story target hands the episode back to the editor; there is nothing to render");
        ledger.ReservedFor(world.UserId).ShouldBe(0);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = '{RenderRequested}' AND user_id = '{world.UserId}'")
            .ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("story_review", "the delivery changes nothing else");
        MessageState(messageId).ShouldBe("true|0|true", "a target this handler does not act on is a success");
    }

    [Fact]
    public async Task VideoReRenderHandler_OnRenderError_RequestsOutboxRetry()
    {
        var world = await SeedAsync();
        var ledger = new RecordingLedger();
        var messageId = await InsertRejectionAsync(world, RejectTarget.ReRender);

        // The platform cap refuses the render: an error result must leave the outbox row due so the dispatcher
        // retries with backoff, instead of being swallowed as a delivered message (D11, D17).
        await using var host = await ReRenderHostAsync(ledger, denySpend: true);
        await host.RunOutboxOnceAsync(Ct);

        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(0, "nothing is queued and nothing is reserved");
        ledger.ReservedFor(world.UserId).ShouldBe(0, "the cap is checked before the reservation");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = '{RenderRequested}' AND user_id = '{world.UserId}'")
            .ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("story_approved", "the rejected cut is still the approved story, so the owner can retry");

        MessageState(messageId).ShouldBe("false|1|true");
        SqlProbe.ScalarText(
                Database,
                $"SELECT (next_attempt_at = TIMESTAMPTZ '{Start.AddSeconds(2):O}')::text || '|' || error FROM outbox_messages WHERE id = '{messageId}'")
            .ShouldStartWith("true|", Case.Sensitive);
        SqlProbe.ScalarText(Database, $"SELECT error FROM outbox_messages WHERE id = '{messageId}'")
            .ShouldContain("spend_cap_reached", Case.Sensitive, "the row records the codes only (D17)");
    }

    [Fact]
    public async Task VideoReRenderHandler_WhenRenderSucceeds_EpisodeBecomesRendering()
    {
        var world = await SeedAsync();
        var ledger = new RecordingLedger();
        await InsertRejectionAsync(world, RejectTarget.ReRender);

        await using var host = await ReRenderHostAsync(ledger);
        await host.RunOutboxOnceAsync(Ct);

        // The re-render re-uses the approved story: no new version, no editor round trip.
        SqlProbe.ScalarText(
                Database,
                $"SELECT status || '|' || attempt::text || '|' || story_version_id::text FROM render_jobs WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe($"queued|1|{Id(world.VersionId)}");
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("rendering");
        SqlProbe.Count(Database, $"SELECT count(*) FROM story_versions WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(1, "a re-render composes no new story version");

        await QuietRenderRequestsAsync(world);
    }

    /// <summary>
    /// The Worker harness with the video-review consumer registered, one known reservation per call and a
    /// deterministic spend cap.
    /// </summary>
    /// <param name="ledger">The recorded credit ledger of this test.</param>
    /// <param name="denySpend">Whether the platform cap refuses every render.</param>
    /// <returns>The started host.</returns>
    private async Task<WorkerHost> ReRenderHostAsync(RecordingLedger ledger, bool denySpend = false)
    {
        var host = new WorkerHost(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", "1000")
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "100000");

        host.ConfigureServices(services =>
        {
            services.RemoveAll<ICreditLedger>();
            services.AddSingleton<ICreditLedger>(ledger);
            services.RemoveAll<IBudgetGuard>();
            services.AddSingleton<IBudgetGuard>(new FakeBudgetGuard { Deny = denySpend });
        });

        await host.StartAsync(Ct);

        return host;
    }

    /// <summary>
    /// One owner with a series, an episode and — unless <paramref name="approved"/> says otherwise — the
    /// approved story version and the price version a render reads, plus one live render asset.
    /// </summary>
    /// <param name="approved">Whether the episode is left in <see cref="EpisodeStatus.StoryApproved"/> with a version.</param>
    /// <returns>The seeded ids.</returns>
    private async Task<World> SeedAsync(bool approved = true)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(
            user.Id,
            series.Id,
            lengthTier: LengthTier.Short,
            status: approved ? EpisodeStatus.StoryApproved : EpisodeStatus.StoryReview,
            ct: Ct);
        await SeedPriceVersionAsync(user.Id);

        var versionId = approved
            ? await SeedApprovedVersionAsync(user.Id, episode.Id)
            : Guid.Empty;

        return new World(user.Id, series.Id, episode.Id, versionId);
    }

    /// <summary>Writes the story version <c>render-params</c> is built from and points the episode at it.</summary>
    /// <param name="userId">The owner.</param>
    /// <param name="episodeId">The episode to approve.</param>
    /// <returns>The version's id.</returns>
    private async Task<Guid> SeedApprovedVersionAsync(Guid userId, Guid episodeId)
    {
        var draft = Draft();
        var version = StoryVersion.Create(
            userId,
            episodeId,
            1,
            StoryVersionSource.Generated,
            draft,
            StoryContentJson.Write(draft, new JsonArray()),
            "model-x",
            "rel=1",
            null,
            null,
            null,
            null,
            null,
            Start);

        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = userId }, new FakeClock(Start)))
        {
            db.StoryVersions.Add(version);
            await db.SaveChangesAsync(Ct);
        }

        SqlProbe.Execute(
            Database,
            $"UPDATE episodes SET status = 'story_approved', approved_story_version_id = '{version.Id}', approved_at = '{Start:O}' WHERE id = '{episodeId}'");

        return version.Id;
    }

    /// <summary>The active price version the render's cost estimate reads, once per class database.</summary>
    /// <param name="userId">The owner the row is written as.</param>
    private async Task SeedPriceVersionAsync(Guid userId)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = userId }, new FakeClock(Start));
        if (db.PriceVersions.Any(version => version.Version == 1))
        {
            return;
        }

        var rates = new PriceVersionRates(
            new Dictionary<string, PriceRates> { ["gpt-6-luna"] = new(0.10m, 0.01m, 0.125m, 0.50m) },
            new Dictionary<string, ImagePriceRate>(),
            null,
            TextCostCalculator.DefaultSurchargeThresholdTokens).ToJson();
        db.PriceVersions.Add(PriceVersion.Create(
            1,
            Start.AddDays(-1),
            rates,
            PriceVersionJson.WriteLimits(PriceLimits.D4Defaults),
            "video-reject-rerender-price-version",
            Start.AddDays(-1)));
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// Writes the <c>video_rejected.v1</c> row the interceptor would have written, with the payload the domain
    /// event serializes to (ids and the snake_case-lower target only).
    /// </summary>
    /// <param name="world">The seeded world the rejection belongs to.</param>
    /// <param name="target">Where the rejection sent the episode.</param>
    /// <returns>The message's id, which is also the D6 key's suffix.</returns>
    private async Task<Guid> InsertRejectionAsync(World world, RejectTarget target)
    {
        var messageId = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(
            new VideoRejected(world.EpisodeId, world.SeriesId, world.UserId, target, Start),
            OutboxJson.Options);

        await Database.ExecuteAsync(
            $"""
             INSERT INTO outbox_messages (id, type, payload, user_id, occurred_at, next_attempt_at, attempts)
             VALUES ('{messageId}', '{Event}', '{payload}', '{world.UserId}', '{Start:O}', '{Start:O}', 0)
             """,
            Ct);

        return messageId;
    }

    /// <summary>
    /// Marks this test's own <c>render_requested.v1</c> rows processed. The Worker would deliver them next —
    /// that is 09.6.x's path — and this class is not about it; leaving them due would make a later test of the
    /// class claim a row that belongs to an earlier one.
    /// </summary>
    /// <param name="world">The seeded world whose rows are settled.</param>
    private Task QuietRenderRequestsAsync(World world) =>
        Database.ExecuteAsync(
            $"UPDATE outbox_messages SET processed_at = '{Start:O}' WHERE type = '{RenderRequested}' AND user_id = '{world.UserId}' AND processed_at IS NULL",
            Ct);

    /// <summary>The delivery outcome of one row: processed, attempts, and whether it is not a dead letter.</summary>
    /// <param name="messageId">The row to read.</param>
    /// <returns>The three flags, so a whole outcome is one assertion.</returns>
    private string MessageState(Guid messageId) =>
        SqlProbe.ScalarText(
            Database,
            $"SELECT (processed_at IS NOT NULL)::text || '|' || attempts::text || '|' || (failed_at IS NULL)::text "
            + $"FROM outbox_messages WHERE id = '{messageId}'");

    /// <summary>A GUID in the lowercase form SQL and the wire use.</summary>
    /// <param name="value">The id.</param>
    /// <returns>The canonical string.</returns>
    private static string Id(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>The story the render reads: one clean keyword per segment and no name for the corpus.</summary>
    /// <returns>The content document the version stores.</returns>
    private static StoryContentV1 Draft() => new(
        "The pier at dawn",
        new StorySegmentV1("The grey mist drifted over the riverside.", ["quiet riverside morning", "grey mist over water"], []),
        [new StorySegmentV1("A wooden pier stood empty at dusk.", ["empty wooden pier at dusk"], [], "setup")],
        new StorySegmentV1("Cold morning light returned.", ["cold morning light"], []),
        "A quiet story about an empty pier.",
        ["mist", "pier"],
        ["atmosphere"],
        new StoryStructureV1("ghost", "riverside", "a misty dawn", "the narrator", "first_person"));

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId, Guid VersionId);

    /// <summary>
    /// The credit ledger these tests run against: one known reservation id per reserve, every reservation
    /// recorded with its user so a test in a shared process can assert only its own.
    /// </summary>
    private sealed class RecordingLedger : ICreditLedger
    {
        private readonly List<(Guid UserId, Guid Reservation)> _reservations = [];

        /// <summary>How often <paramref name="userId"/>'s renders reserved credits.</summary>
        /// <param name="userId">The owner to count.</param>
        /// <returns>The count.</returns>
        public int ReservedFor(Guid userId) => _reservations.Count(entry => entry.UserId == userId);

        /// <summary>The reservation id of the one reserve <paramref name="userId"/> made.</summary>
        /// <param name="userId">The owner to look up.</param>
        /// <returns>The id, as the job row stores it.</returns>
        public string ReservationFor(Guid userId) =>
            _reservations.Single(entry => entry.UserId == userId).Reservation.ToString("D", CultureInfo.InvariantCulture);

        /// <inheritdoc />
        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var reservation = Guid.CreateVersion7();
            _reservations.Add((userId, reservation));

            return ValueTask.FromResult<ErrorOr<ReservationId>>(new ReservationId(reservation));
        }

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
    }
}
