using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Features.Publishing.Events;
using NightTales.Application.Features.Publishing.Jobs;
using NightTales.Application.Publishing;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;
using NightTales.Infrastructure.Persistence.Outbox;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// The Worker's <c>publication_updated.v1</c> subscriber that schedules the upload of a publication that just
/// reached <c>scheduled</c> (06.2.3-T2, 06.2.3 plan §4 UC4, 06 README D12/D13, YR3/YR24) over a real PostgreSQL 17.
/// </summary>
/// <remarks>
/// <para>
/// The handler re-reads the row, so a fake <c>IAppDbContext</c> would prove nothing: every test runs against the
/// class database through <see cref="TestDatabase.CreateAppDbContext"/> with the owner pinned, exactly as the
/// dispatcher pins <c>ISystemContext</c> for a delivery. The scheduler is a recording fake — the real port's only
/// observable effect here is <em>which</em> of <c>Enqueue</c>/<c>Schedule</c> was called and with what delay — and
/// the clock is a <see cref="FakeClock"/>, so a delay is asserted as an exact span instead of a wall-clock wait.
/// </para>
/// <para>
/// One test drives the production path instead: the <c>scheduled</c> event is written the way the interceptor writes
/// it, the Worker's own <see cref="OutboxDispatcher"/> delivers it through <see cref="WorkerHost.RunOutboxOnceAsync"/>
/// and the host's <see cref="InlineJobScheduler"/> records the job the subscriber queued. That is what proves the
/// consent → outbox → schedule chain actually reaches this handler (STATUS S71-9).
/// </para>
/// <para>
/// Job-level idempotency (a second run of <see cref="PublishYouTubeJob"/> being a no-op) belongs to the job's own
/// suite; here a redelivered event is asserted to schedule twice, which is the at-least-once contract of D11.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PublicationScheduledForUploadHandlerTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The outbox type of D13, unchanged in shape (no <c>queueAt</c> member).</summary>
    private const string OutboxEventType = "publication_updated.v1";

    /// <summary>The <c>YouTube:PublishLeadMinutes</c> the handler under test reads (06 README D12).</summary>
    private const int LeadMinutes = 90;

    /// <summary>The shipped <c>YouTube:Poll:IntervalSeconds</c>, which YR24 multiplies by five for a hold.</summary>
    private const int PollIntervalSeconds = 60;

    private static readonly DateTimeOffset Start = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task HandleAsync_NextAttemptAtSet_SchedulesAtThatInstant()
    {
        var due = Start.AddMinutes(45);
        var world = await SeedAsync(nextAttemptAt: due);
        var scheduler = new RecordingScheduler();

        await HandleAsync(world, PublicationStatus.Scheduled, scheduler);

        var call = scheduler.Calls.ShouldHaveSingleItem();
        call.JobType.ShouldBe(nameof(PublishYouTubeJob));
        call.Delay.HasValue.ShouldBeTrue("next_attempt_at is the instant the next run is due");
        call.Delay.Value.ShouldBe(TimeSpan.FromMinutes(45));
    }

    [Fact]
    public async Task HandleAsync_NoNextAttemptAt_FallsBackToPublishAtMinusLeadTime()
    {
        // A fresh consent: next_attempt_at is NULL, so D12's formula answers publish_at − PublishLeadMinutes.
        var world = await SeedAsync(publishAt: Start.AddHours(4));
        var scheduler = new RecordingScheduler();

        await HandleAsync(world, PublicationStatus.Scheduled, scheduler);

        var fallback = scheduler.Calls.ShouldHaveSingleItem().Delay;
        fallback.HasValue.ShouldBeTrue("D12 answers publish_at − PublishLeadMinutes");
        fallback.Value.ShouldBe(TimeSpan.FromMinutes((4 * 60) - LeadMinutes));

        // The same formula clamps to now when publish_at sits inside the lead window: no negative delay, and the
        // boundary rule stays in PublicationQueueTime rather than being repeated here (STATUS S69-7).
        var insideLead = await SeedAsync(publishAt: Start.AddMinutes(30));
        var clamped = new RecordingScheduler();

        await HandleAsync(insideLead, PublicationStatus.Scheduled, clamped);

        clamped.Calls.ShouldHaveSingleItem().Delay.ShouldBeNull("a publish_at inside the lead window is due now, so it is enqueued");
    }

    [Fact]
    public async Task HandleAsync_NoNextAttemptAtAndNoPublishAt_EnqueuesNow()
    {
        var world = await SeedAsync();
        var scheduler = new RecordingScheduler();

        await HandleAsync(world, PublicationStatus.Scheduled, scheduler);

        scheduler.Calls.ShouldHaveSingleItem().Delay.ShouldBeNull("D12 answers now for a publication with no publish_at");
    }

    [Fact]
    public async Task HandleAsync_RowGone_IsANoOp()
    {
        var world = await SeedAsync();
        var scheduler = new RecordingScheduler();

        // The row was deleted between the commit that raised the event and this delivery.
        await HandleAsync(world, PublicationStatus.Scheduled, scheduler, publicationId: Guid.CreateVersion7());

        scheduler.Calls.ShouldBeEmpty("a publication that no longer exists has nothing to schedule");
    }

    [Fact]
    public async Task HandleAsync_RowAlreadyHasExternalPostId_IsANoOp()
    {
        var world = await SeedAsync(externalPostId: "dQw4w9WgXcQ");
        var scheduler = new RecordingScheduler();

        // A row that already carries a video is the job's reconcile case, never a reason to queue a second upload.
        await HandleAsync(world, PublicationStatus.Scheduled, scheduler);

        scheduler.Calls.ShouldBeEmpty("an upload that already produced a video must not be scheduled again");
    }

    [Fact]
    public async Task HandleAsync_StatusNotScheduled_IsANoOp()
    {
        var world = await SeedAsync();
        var scheduler = new RecordingScheduler();

        // The event's own status decides first: a processing/failed transition is another consumer's business.
        await HandleAsync(world, PublicationStatus.Processing, scheduler);
        await HandleAsync(world, PublicationStatus.Failed, scheduler);

        scheduler.Calls.ShouldBeEmpty();

        // And the re-read decides second: a scheduled event whose row has since been cancelled schedules nothing.
        var cancelled = await SeedAsync(status: PublicationStatus.Cancelled);
        await HandleAsync(cancelled, PublicationStatus.Scheduled, scheduler);

        scheduler.Calls.ShouldBeEmpty("the current row state wins over the event's snapshot");
    }

    [Fact]
    public async Task HandleAsync_HeldRow_SchedulesAtTheDeferredInstant()
    {
        // YR24: a hold writes a real deferred next_attempt_at (now + 5 × Poll:IntervalSeconds), never NULL.
        var deferred = Start.AddSeconds(5 * PollIntervalSeconds);
        var world = await SeedAsync(nextAttemptAt: deferred, heldByIncidentId: Guid.CreateVersion7());
        var scheduler = new RecordingScheduler();

        await HandleAsync(world, PublicationStatus.Scheduled, scheduler);

        var call = scheduler.Calls.ShouldHaveSingleItem();
        call.Delay.HasValue.ShouldBeTrue("a held row carries a deferred instant, never the D12 fallback (YR24)");
        call.Delay.Value.ShouldBe(
            TimeSpan.FromSeconds(5 * PollIntervalSeconds),
            "a held row is re-checked when its deferred instant arrives, not immediately (no hold → enqueue loop)");
    }

    [Fact]
    public async Task HandleAsync_RedeliveredEvent_SchedulesTwice_JobRunsOnceEffectively()
    {
        var world = await SeedAsync(nextAttemptAt: Start.AddMinutes(10));
        var scheduler = new RecordingScheduler();

        // D11 delivery is at least once: the same message may arrive twice, and the subscriber is not the guard —
        // the job's own status guard plus external_post_id reconcile make the second run a no-op.
        await HandleAsync(world, PublicationStatus.Scheduled, scheduler);
        await HandleAsync(world, PublicationStatus.Scheduled, scheduler);

        scheduler.Calls.Count.ShouldBe(2);
        scheduler.Calls.ShouldAllBe(call => call.JobType == nameof(PublishYouTubeJob));
    }

    [Fact]
    public async Task ScheduleOnConsent_NextAttemptAtInThePast_EnqueuesImmediately_AndInTheFuture_SchedulesTheDelay()
    {
        var past = await SeedAsync(nextAttemptAt: Start.AddMinutes(-5));
        var future = await SeedAsync(nextAttemptAt: Start.AddMinutes(20));
        var scheduler = new RecordingScheduler();

        await HandleAsync(past, PublicationStatus.Scheduled, scheduler);
        await HandleAsync(future, PublicationStatus.Scheduled, scheduler);

        scheduler.Calls.Count.ShouldBe(2);
        scheduler.Calls[0].Delay.ShouldBeNull("an instant that already passed is due now");

        var futureCall = scheduler.Calls[1];
        futureCall.Delay.HasValue.ShouldBeTrue("a future instant is scheduled, not enqueued");
        futureCall.Delay.Value.ShouldBe(TimeSpan.FromMinutes(20));
    }

    [Fact]
    public async Task ScheduleOnConsent_IgnoresNonScheduledStatuses_AndAMissingRow()
    {
        var world = await SeedAsync();
        var scheduler = new RecordingScheduler();

        await HandleAsync(world, PublicationStatus.Uploading, scheduler);
        await HandleAsync(world, PublicationStatus.Published, scheduler);
        await HandleAsync(world, PublicationStatus.Scheduled, scheduler, publicationId: Guid.CreateVersion7());

        scheduler.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ScheduleOnConsent_HeldRow_SchedulesAtTheDeferredInstant_NotImmediately()
    {
        var deferred = Start.AddSeconds(5 * PollIntervalSeconds);
        var world = await SeedAsync(nextAttemptAt: deferred, heldByIncidentId: Guid.CreateVersion7());
        var scheduler = new RecordingScheduler();

        await HandleAsync(world, PublicationStatus.Scheduled, scheduler);

        var delay = scheduler.Calls.ShouldHaveSingleItem().Delay;
        delay.HasValue.ShouldBeTrue("a held row's next_attempt_at is deferred; the D12 fallback is never taken (YR24)");
        delay.Value.ShouldBe(TimeSpan.FromSeconds(5 * PollIntervalSeconds));
        delay.Value.ShouldBeGreaterThan(TimeSpan.Zero, "the next check is deferred, so re-checking is never a hot loop");
    }

    [Fact]
    public async Task ScheduleOnConsent_RedeliveredEvent_SchedulesTwice_ButTheJobRunsOnceEffectively()
    {
        var world = await SeedAsync(nextAttemptAt: Start.AddMinutes(15));
        var scheduler = new RecordingScheduler();

        await HandleAsync(world, PublicationStatus.Scheduled, scheduler);
        await HandleAsync(world, PublicationStatus.Scheduled, scheduler);

        // Two Hangfire jobs for the same publication; both name the same row, and the job's UC1 guards make the
        // second run a no-op (that half is pinned by PublishYouTubeJobTests).
        scheduler.Calls.Count.ShouldBe(2);
        scheduler.Calls.ShouldAllBe(call => call.Delay == TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task ScheduleOnConsent_DispatchThroughTheOutbox_EnqueuesPublishYouTubeJob()
    {
        var world = await SeedAsync();
        var messageId = await InsertScheduledEventAsync(world);
        var clock = new FakeClock(Start);

        await using var host = new WorkerHost(Database, clock);
        await host.StartAsync(Ct);

        // The production delivery path: the Worker's own dispatcher claims the row, pins the scope to the event's
        // user and runs every registered consumer — this subscriber among them (STATUS S71-9).
        await host.RunOutboxOnceAsync(Ct);

        using (var scope = host.CreateScope())
        {
            scope.ServiceProvider
                .GetServices<IOutboxHandler<PublicationUpdated>>()
                .ShouldContain(
                    handler => handler is PublicationScheduledForUploadHandler,
                    "the Worker composition registers this subscriber for publication_updated.v1");
        }

        var call = host.Jobs.Enqueued.ShouldHaveSingleItem();

        call.JobType.ShouldBe(nameof(PublishYouTubeJob));
        call.Queue.ShouldBe(JobQueues.Name(JobQueue.Publish), "the job's own policy selects the queue");
        call.Delay.ShouldBeNull("a freshly consented publication with no publish_at is due now");

        MessageState(messageId).ShouldBe("true|0", "the delivery is complete, so the dispatcher marks the row processed");
    }

    /// <summary>Delivers one event to the handler over the class database, with the owner pinned as the dispatcher pins it.</summary>
    /// <param name="world">The seeded world the event belongs to.</param>
    /// <param name="status">The status the event carries.</param>
    /// <param name="scheduler">The recording job port the handler is handed.</param>
    /// <param name="publicationId">The event's publication id, or <see cref="World.PublicationId"/>.</param>
    private async Task HandleAsync(
        World world,
        PublicationStatus status,
        RecordingScheduler scheduler,
        Guid? publicationId = null)
    {
        var clock = new FakeClock(Start);

        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, clock);

        var handler = new PublicationScheduledForUploadHandler(
            db,
            scheduler,
            new YouTubeOptions { PublishLeadMinutes = LeadMinutes },
            clock);

        await handler.HandleAsync(
            new PublicationUpdated(
                publicationId ?? world.PublicationId,
                world.EpisodeId,
                world.UserId,
                status,
                ErrorCode: null,
                Start),
            new OutboxContext(Guid.CreateVersion7(), world.UserId, Attempt: 1, Start),
            Ct);
    }

    /// <summary>
    /// One owner with a series, a <c>ready</c> episode, an active connection and one publication, plus the columns the
    /// aggregate's own transitions would have written (<c>consent_at</c>, <c>next_attempt_at</c>, the hold stamp,
    /// <c>external_post_id</c>) — which <see cref="PublicationBuilder"/> does not model.
    /// </summary>
    /// <param name="status">The publication's state after the transition the event reports.</param>
    /// <param name="publishAt">The owner's requested go-live instant, or <see langword="null"/> for "as soon as possible".</param>
    /// <param name="nextAttemptAt">The row's own next-attempt instant: a quota reschedule, a hold, or a clear.</param>
    /// <param name="externalPostId">The video id when the row already has one.</param>
    /// <param name="heldByIncidentId">The incident a held row names in <c>platform_payload.heldByIncidentId</c>.</param>
    private async Task<World> SeedAsync(
        PublicationStatus status = PublicationStatus.Scheduled,
        DateTimeOffset? publishAt = null,
        DateTimeOffset? nextAttemptAt = null,
        string? externalPostId = null,
        Guid? heldByIncidentId = null)
    {
        var owner = new TestCurrentUser();
        var userId = (await new UserBuilder(Database, owner).CreateAsync(ct: Ct)).Id;
        var actor = new TestCurrentUser { Id = userId };
        var clock = new FakeClock(Start);

        var series = await new SeriesBuilder(Database, actor, clock).CreateAsync(
            userId,
            title: "Chuỗi thử nghiệm",
            createdAt: Start,
            ct: Ct);

        var episode = await new EpisodeBuilder(Database, actor, clock).CreateAsync(
            userId,
            series.Id,
            brief: "Nội dung tập thử nghiệm.",
            status: EpisodeStatus.Ready,
            createdAt: Start,
            ct: Ct);

        // `ux_channel_connections_account` and the publication's FK need one real connection per owner.
        var connection = await new ChannelConnectionBuilder(Database, actor, clock).CreateAsync(
            userId,
            ChannelConnectionStatus.Active,
            $"NTS Test Channel {Guid.CreateVersion7():N}",
            ct: Ct);

        var payload = heldByIncidentId is { } incident
            ? PublicationPayload.WithKey(null, PublicationPayload.HeldByIncidentIdKey, incident.ToString("D"))
            : null;

        var publicationId = await new PublicationBuilder(Database).CreateAsync(
            userId,
            episode.Id,
            connection.Id,
            status: status,
            publishAt: publishAt,
            platformPayloadJson: payload,
            createdAt: Start,
            ct: Ct);

        await Database.ExecuteAsync(
            "UPDATE publications SET next_attempt_at = " + Instant(nextAttemptAt)
            + ", external_post_id = " + Literal(externalPostId)
            + ", consent_at = " + Instant(status is PublicationStatus.Scheduled or PublicationStatus.Uploading
                ? Start
                : null)
            + $" WHERE id = '{publicationId}'",
            Ct);

        return new World(userId, episode.Id, publicationId);
    }

    /// <summary>
    /// Writes the <c>publication_updated.v1</c> row the interceptor would have written for a transition to
    /// <c>scheduled</c>, with the payload D13 serializes to (ids and the snake_case status only).
    /// </summary>
    /// <param name="world">The seeded world the event belongs to.</param>
    /// <returns>The message's id.</returns>
    private async Task<Guid> InsertScheduledEventAsync(World world)
    {
        var messageId = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(
            new PublicationUpdated(
                world.PublicationId,
                world.EpisodeId,
                world.UserId,
                PublicationStatus.Scheduled,
                ErrorCode: null,
                Start),
            OutboxJson.Options);

        await Database.ExecuteAsync(
            $"""
             INSERT INTO outbox_messages (id, type, payload, user_id, occurred_at, next_attempt_at, attempts)
             VALUES ('{messageId}', '{OutboxEventType}', '{payload}', '{world.UserId}', '{Start:O}', '{Start:O}', 0)
             """,
            Ct);

        return messageId;
    }

    /// <summary>The delivery outcome of one row: processed, and how many attempts it spent.</summary>
    /// <param name="messageId">The row to read.</param>
    private string MessageState(Guid messageId) =>
        SqlProbe.ScalarText(
            Database,
            "SELECT (processed_at IS NOT NULL)::text || '|' || attempts::text "
            + $"FROM outbox_messages WHERE id = '{messageId}'");

    private static string Literal(string? value) =>
        value is null ? "NULL" : $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string Instant(DateTimeOffset? value) =>
        Literal(value?.ToString("O", CultureInfo.InvariantCulture));

    /// <summary>The ids one seeded test owns; every assertion is scoped to them, never to a class-wide count.</summary>
    private sealed record World(Guid UserId, Guid EpisodeId, Guid PublicationId);

    /// <summary>One recorded <see cref="IJobScheduler"/> call: the job class and the delay of a schedule, if any.</summary>
    private sealed record RecordedCall(string JobType, TimeSpan? Delay);

    /// <summary>The recording job port: it never runs a job, so no upload machinery is needed to assert scheduling.</summary>
    private sealed class RecordingScheduler : IJobScheduler
    {
        private readonly List<RecordedCall> _calls = [];

        public List<RecordedCall> Calls => _calls;

        public string Enqueue<TJob>(Expression<Func<TJob, CancellationToken, Task>> methodCall)
            where TJob : class, IBackgroundJob
        {
            ArgumentNullException.ThrowIfNull(methodCall);

            _calls.Add(new RecordedCall(typeof(TJob).Name, Delay: null));

            return Guid.CreateVersion7().ToString("N");
        }

        public string Schedule<TJob>(Expression<Func<TJob, CancellationToken, Task>> methodCall, TimeSpan delay)
            where TJob : class, IBackgroundJob
        {
            ArgumentNullException.ThrowIfNull(methodCall);

            _calls.Add(new RecordedCall(typeof(TJob).Name, delay));

            return Guid.CreateVersion7().ToString("N");
        }

        public void AddOrUpdateRecurring<TJob>(
            string id,
            string cron,
            Expression<Func<TJob, CancellationToken, Task>> methodCall)
            where TJob : class, IBackgroundJob =>
            throw new NotSupportedException("The upload subscriber registers no recurring job.");

        public void RemoveRecurringIfExists(string id) =>
            throw new NotSupportedException("The upload subscriber removes no recurring job.");
    }
}
