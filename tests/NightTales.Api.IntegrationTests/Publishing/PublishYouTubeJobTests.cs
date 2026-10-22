using System.Globalization;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Features.Publishing.Jobs;
using NightTales.Application.Features.Publishing.Upload;
using NightTales.Application.Publishing;
using NightTales.Application.Publishing.Quota;
using NightTales.Domain.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Google;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// 06.2.3-T2 §Tests: <c>PublishYouTubeJob</c> and its handler over real PostgreSQL 17, the real Worker composition
/// (<see cref="PublishYouTubeJobHarness"/>), <see cref="FakeGoogleServer"/> and a recording <see cref="IBlobStore"/>:
/// one video per consented publication even when the last response is lost, the encrypted session URI persisted
/// before the first byte, one quota unit reserved and released exactly once, every Google failure mapped to a row
/// transition with an <c>error_code</c>, a deferred <c>next_attempt_at</c> and an outbox event, and the credential
/// and metadata canaries absent from every log line.
/// </summary>
/// <remarks>
/// <para>
/// Every test seeds its own publication (raw SQL, see the harness) and reads committed state back with raw SQL, so
/// the assertions are about what a deployment would observe, not about a tracked entity of the scope under test.
/// </para>
/// <para>
/// The three incident names of the task §Tests list (<c>Incident_RepeatedRaises_CreateOneRowAndOneEvent</c>,
/// <c>Incident_OpenRow_SurvivesConcurrentRaises</c>, <c>Migration_06PlatformIncidents_…</c>) live in
/// <c>PlatformIncidentStoreTests</c> and <c>AddPlatformIncidentsMigrationTests</c>, written by the slice that owns the
/// store and the migration; they are deliberately not duplicated here. The per-user upload slot (plan ST1/ST2,
/// <c>user_upload_limit_reached</c>) belongs to the dropped 06.3.2 and has no row in this list, matching the README.
/// </para>
/// </remarks>
/// <param name="database">The class database, a migrated copy of the run's template.</param>
public sealed class PublishYouTubeJobTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>
    /// The first PT midnight after the harness's <see cref="PublishYouTubeJobHarness.Start"/> (02 Oct 2026 is PDT,
    /// UTC-7): the instant a refused reservation must be rescheduled to, hard-coded so the assertion cannot agree
    /// with a wrong implementation of the day.
    /// </summary>
    private static readonly DateTimeOffset FirstReset =
        new(2026, 10, 3, 7, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// <c>[JobPolicy(Retries = 5)]</c> gives Hangfire six attempts: the first plus five retries, so the sixth is the
    /// one that owes the row a verdict.
    /// </summary>
    private const int MaxAttempts = 6;

    /// <summary>The plan's bound on the quota reschedule's jitter (06 README D10: <c>&lt;= 10 min</c>).</summary>
    private static readonly TimeSpan JitterBound = TimeSpan.FromMinutes(10);

    /// <summary>The deferred instant of an incident hold: <c>now + 5 x Poll:IntervalSeconds</c> (YR24), 60 s here.</summary>
    private static readonly TimeSpan HoldDeferral = TimeSpan.FromSeconds(5 * 60);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Publish_HappyPath_SetsExternalPostIdAndProcessing_AndEmitsPublicationUpdated()
    {
        // The whole point of the task: a consented, scheduled row becomes a video, streams three chunks from object
        // storage, and lands in `processing` with the video id, a cleared retry, and the event the poller subscribes
        // to. The unit is spent and no longer reserved.
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness);

        var sent = await harness.SendAsync(world.PublicationId, Ct);

        sent.IsError.ShouldBeFalse($"a ready publication must upload: {sent.FirstError}");
        sent.Value.ShouldBe(UploadOutcome.Processing);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("processing");
        (await harness.ExternalPostIdAsync(world.PublicationId)).ShouldBe(
            harness.Google.LastVideoId,
            "the row must name the video YouTube actually created");
        (await harness.NextAttemptAtAsync(world.PublicationId)).ShouldBeNull("a successful upload clears the retry");
        (await harness.ErrorCodeAsync(world.PublicationId)).ShouldBeEmpty();
        (await harness.PayloadKeyAsync(world.PublicationId, PublicationPayload.QuotaReservedDayKey))
            .ShouldBeEmpty("the unit is spent, so the reservation key is removed rather than nulled");

        harness.Google.SessionCreateCalls.ShouldBe(1);
        harness.Google.ChunkRequests.ShouldBe(3, "a three-chunk payload is uploaded in three chunk PUTs");
        harness.Google.ContentRanges[^1].ShouldBe(
            $"bytes {2 * PublishYouTubeJobHarness.ChunkSizeBytes}-{PublishYouTubeJobHarness.PayloadLength - 1}/"
            + PublishYouTubeJobHarness.PayloadLength.ToString(CultureInfo.InvariantCulture),
            "the last chunk names the final byte against the declared total");

        var events = await harness.OutboxPayloadsAsync("publication_updated.v1", world.PublicationId);
        events.Count.ShouldBe(1, "one transition, one event");
        events[0].ShouldContain("processing");
    }

    [Fact]
    public async Task Publish_WithoutConsent_NeitherCallsGoogleNorChangesTheRow()
    {
        // NFR-06-1: no consent, no upload. The refusal is the aggregate's (`publication_consent_required`), the job
        // swallows it so Hangfire cannot retry five times, and nothing in the row, Google, or the ledger moves.
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness, consent: false);
        var before = await harness.PublicationRowAsync(world.PublicationId);

        await Should.NotThrowAsync(() => harness.RunAsync(world.PublicationId, Ct));

        var sent = await harness.SendAsync(world.PublicationId, Ct);

        sent.IsError.ShouldBeTrue("an un-consented row must be refused");
        sent.FirstError.Code.ShouldBe("publication_consent_required");
        (await harness.PublicationRowAsync(world.PublicationId)).ShouldBe(before, "the refusal writes nothing");
        harness.Google.Calls.ShouldBeEmpty("no consent means no Google call of any kind");
        harness.Blobs.OpenReads.ShouldBe(0);
        (await harness.QuotaUnitsAsync(harness.QuotaDayToday()) ?? 0).ShouldBe(0, "nothing was reserved");
    }

    [Fact]
    public async Task Publish_LostResponseAfterLastChunk_SecondRunResumes_AndSessionCreateCallsStaysOne()
    {
        // F02, the reason this task exists: the session URI is committed before the first byte, so the attempt whose
        // last response is lost leaves the row `uploading` with the session, and the next attempt probes ->
        // Complete(videoId) -> `processing` without ever creating a second session (or a second video).
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness);
        harness.Google.Scenario = UploadScenario.DropAfterLastChunk();

        await RunTolerantlyAsync(harness, world.PublicationId, attempt: 1);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe(
            "uploading",
            "the transport failure of the dropped response leaves the row resumable, not failed");
        (await harness.SessionCipherAsync(world.PublicationId)).ShouldNotBeNullOrWhiteSpace();
        harness.Google.SessionCreateCalls.ShouldBe(1);
        harness.Google.ChunkRequests.ShouldBeGreaterThanOrEqualTo(
            3,
            "the bytes did reach the server; only the answer was lost (the client's chunk-retry budget may resend the last one)");

        harness.Google.Scenario = UploadScenario.None;

        await RunTolerantlyAsync(harness, world.PublicationId, attempt: 2);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("processing");
        (await harness.ExternalPostIdAsync(world.PublicationId)).ShouldBe(harness.Google.LastVideoId);
        harness.Google.SessionCreateCalls.ShouldBe(1, "a resumed upload must never open a second session");
        harness.Google.SessionCreates.Count.ShouldBe(1, "one session, one video");
    }

    [Fact]
    public async Task Publish_SessionUriIsPersistedEncryptedBeforeTheFirstByte()
    {
        // D4/D17: the URI is stored as Data-Protection ciphertext under `tokens.v1/{connectionId}` before any chunk
        // leaves, and it round-trips back to the fake's own Location — so the resume works for the right reason.
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness);
        harness.Google.Scenario = UploadScenario.PersistentServerErrors;

        await RunTolerantlyAsync(harness, world.PublicationId, attempt: 1);

        var cipher = await harness.SessionCipherAsync(world.PublicationId);
        harness.Google.SessionCreates.ShouldNotBeEmpty("the run opened a session before the first byte");
        var location = harness.Google.SessionCreates[harness.Google.SessionCreates.Count - 1].Location;

        cipher.ShouldNotBeNullOrWhiteSpace("the session must be persisted before the stream is opened");
        cipher.ShouldNotBe(location, "a stored URI would be a bearer credential in plaintext");
        cipher.ShouldNotContain(FakeGoogleServer.SessionId(new Uri(location)));
        harness.Protector.TryUnprotect(
            world.Channel.Id.ToString("D", CultureInfo.InvariantCulture),
            cipher,
            out var plaintext)
            .ShouldBeTrue("the cipher must be readable under the row's own purpose");
        plaintext.ShouldBe(location);
        harness.Google.ChunkRequests.ShouldBeGreaterThan(0, "the failures happened after the session was persisted");
    }

    [Fact]
    public async Task Publish_ExistingExternalPostId_ReconcilesWithoutUpload()
    {
        // D9/F02 step 3: a row that already names a video is reconciled from `videos.list`, never uploaded again —
        // no session, no chunk, no blob open.
        const string ExistingVideoId = "canary-video-already-uploaded";
        await using var harness = await StartAsync(google: options =>
            options.VideoStatuses[ExistingVideoId] = new FakeVideoStatus("uploaded"));
        var world = await SeedReadyAsync(
            harness,
            status: PublicationStatus.Uploading,
            externalPostId: ExistingVideoId);

        var sent = await harness.SendAsync(world.PublicationId, Ct);

        sent.IsError.ShouldBeFalse($"the reconcile path must succeed: {sent.FirstError}");
        sent.Value.ShouldBe(UploadOutcome.Reconciled);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("processing");
        (await harness.ExternalPostIdAsync(world.PublicationId)).ShouldBe(ExistingVideoId);
        (await harness.NextAttemptAtAsync(world.PublicationId)).ShouldBeNull();
        harness.Google.VideoListCalls.ShouldBe(1, "the reconcile asks YouTube what it holds");
        harness.Google.SessionCreateCalls.ShouldBe(0, "a second upload must never start on this path");
        harness.Google.ChunkRequests.ShouldBe(0);
        harness.Blobs.OpenReads.ShouldBe(0);
    }

    [Fact]
    public async Task Publish_QuotaExhausted_ReschedulesToNextPtMidnight_WithBoundedJitter_AndReleasesTheUnit()
    {
        // D10, both sources of `quotaExceeded`: the ledger refusing the reservation (nothing was taken) and
        // YouTube refusing the session create after a unit was reserved (the unit goes back). Both end `scheduled`
        // with `youtube_quota_exhausted` at the next PT midnight, jittered by at most ten minutes.
        var day = PacificQuotaDay.Day(PublishYouTubeJobHarness.Start);
        await using var harness = await StartAsync(unitsPerDay: 1);
        await harness.SeedQuotaAsync(day, unitsUsed: 1, unitsLimit: 1, ct: Ct);
        var refused = await SeedReadyAsync(harness);

        await harness.RunAsync(refused.PublicationId, Ct);

        (await harness.StatusAsync(refused.PublicationId)).ShouldBe("scheduled");
        (await harness.ErrorCodeAsync(refused.PublicationId)).ShouldBe(PublicationErrorCodes.QuotaExhausted);
        AssertBoundedRescheduleToFirstReset(await harness.NextAttemptAtAsync(refused.PublicationId));
        (await harness.QuotaUnitsAsync(day)).ShouldBe(1, "a refused reservation leaves the counter untouched");
        harness.Google.SessionCreateCalls.ShouldBe(0, "no quota, no session");
        harness.Blobs.OpenReads.ShouldBe(0);
        (await harness.OutboxCountAsync("publication_updated.v1", refused.PublicationId))
            .ShouldBe(1, "the reschedule is a row transition and travels on the same event");

        await using var second = await StartAsync();
        var googleRefused = await SeedReadyAsync(second);
        second.Google.Scenario = UploadScenario.QuotaExceeded();

        await second.RunAsync(googleRefused.PublicationId, Ct);

        (await second.StatusAsync(googleRefused.PublicationId)).ShouldBe("scheduled");
        (await second.ErrorCodeAsync(googleRefused.PublicationId)).ShouldBe(PublicationErrorCodes.QuotaExhausted);
        AssertBoundedRescheduleToFirstReset(await second.NextAttemptAtAsync(googleRefused.PublicationId));
        second.Google.SessionCreateCalls.ShouldBe(1, "YouTube received the session create and refused it on quota");
        second.Google.ChunkRequests.ShouldBe(0);
        (await second.QuotaUnitsAsync(day) ?? 0).ShouldBe(
            0,
            "quotaExceeded before the first byte releases the reserved unit");
        (await second.PayloadKeyAsync(googleRefused.PublicationId, PublicationPayload.QuotaReservedDayKey))
            .ShouldBeEmpty("a released reservation removes the key");
    }

    [Fact]
    public async Task QuotaReschedule_SchedulesTheCorrectDelay()
    {
        // UC4: the reschedule's outbox row reaches `PublicationScheduledForUploadHandler`, which schedules the job
        // at exactly `next_attempt_at` — the delay Hangfire records is the difference to that instant.
        await using var harness = await StartAsync(unitsPerDay: 1);
        await harness.SeedQuotaAsync(harness.QuotaDayToday(), unitsUsed: 1, unitsLimit: 1, ct: Ct);
        var world = await SeedReadyAsync(harness);

        await harness.RunAsync(world.PublicationId, Ct);

        var nextAttemptAt = await harness.NextAttemptAtAsync(world.PublicationId);
        nextAttemptAt.ShouldNotBeNull();

        var delivered = await harness.DeliverOutboxAsync(Ct);
        delivered.ShouldBeGreaterThan(0, "the reschedule raised an event that the dispatcher must claim");

        var scheduled = harness.Jobs.Enqueued
            .Where(call => call.JobType == nameof(PublishYouTubeJob)
                && call.Queue == "publish"
                && call.Delay is not null)
            .ToList();

        scheduled.ShouldContain(
            call => call.Delay!.Value == nextAttemptAt.Value - harness.Clock.UtcNow,
            "a rescheduled publication must be handed to the scheduler at its own next_attempt_at");
    }

    [Fact]
    public async Task QuotaReschedule_RunsAtNextAttemptAt()
    {
        // The delayed job, run at its own instant, books a fresh unit on the new quota day and uploads.
        await using var harness = await StartAsync(unitsPerDay: 1);
        await harness.SeedQuotaAsync(harness.QuotaDayToday(), unitsUsed: 1, unitsLimit: 1, ct: Ct);
        var world = await SeedReadyAsync(harness);

        await harness.RunAsync(world.PublicationId, Ct);
        var nextAttemptAt = await harness.NextAttemptAtAsync(world.PublicationId);
        nextAttemptAt.ShouldNotBeNull();

        await harness.DeliverOutboxAsync(Ct);
        harness.Clock.Set(nextAttemptAt.Value);
        (await harness.RunPendingJobsAsync(Ct)).ShouldBeGreaterThan(0, "the delayed job must be the one that runs");

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("processing");
        harness.Google.SessionCreateCalls.ShouldBe(1);
        var newDay = PacificQuotaDay.Day(nextAttemptAt.Value);
        newDay.ShouldBe(new DateOnly(2026, 10, 3), "the retry ran on the Pacific day after the reschedule");
        (await harness.QuotaUnitsAsync(newDay)).ShouldBe(1, "the retry spent one unit of the new day");
    }

    [Fact]
    public async Task EarlyDelivery_BeforeNextAttemptAt_IsANoOp()
    {
        // YR27-1: a duplicate or early redelivery returns `Rescheduled` without touching the row, quota or Google.
        await using var harness = await StartAsync(unitsPerDay: 1);
        await harness.SeedQuotaAsync(harness.QuotaDayToday(), unitsUsed: 1, unitsLimit: 1, ct: Ct);
        var world = await SeedReadyAsync(harness);
        await harness.RunAsync(world.PublicationId, Ct);
        var before = await harness.PublicationRowAsync(world.PublicationId);
        var units = await harness.QuotaUnitsAsync(harness.QuotaDayToday());
        var googleCalls = harness.Google.Calls.Count;

        var sent = await harness.SendAsync(world.PublicationId, Ct);

        sent.IsError.ShouldBeFalse();
        sent.Value.ShouldBe(UploadOutcome.Rescheduled);
        (await harness.PublicationRowAsync(world.PublicationId)).ShouldBe(before, "an early delivery writes nothing");
        (await harness.QuotaUnitsAsync(harness.QuotaDayToday())).ShouldBe(units);
        harness.Google.Calls.Count.ShouldBe(googleCalls, "no Google call before the deferred instant");
    }

    [Theory]
    [InlineData("2026-03-08T20:00:00Z", "2026-03-09T07:00:00Z")]
    [InlineData("2026-11-01T20:00:00Z", "2026-11-02T08:00:00Z")]
    public async Task Publish_PtDayBoundary_AcrossDst_ReschedulesCorrectly(string now, string expectedReset)
    {
        // The Pacific day is the quota day (D10): 13:00 PDT on 8 March resets at 07:00Z (UTC-7), while 12:00 PST on
        // 1 November resets at 08:00Z (UTC-8). Both instants are local afternoon, so a fixed-offset day would answer
        // the wrong midnight in exactly one of the two.
        var at = DateTimeOffset.Parse(now, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var reset = DateTimeOffset.Parse(expectedReset, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        await using var harness = await StartAsync(unitsPerDay: 1, clock: new FakeClock(at));

        var day = PacificQuotaDay.Day(at);
        await harness.SeedQuotaAsync(day, unitsUsed: 1, unitsLimit: 1, ct: Ct);
        var world = await SeedReadyAsync(harness);

        await harness.RunAsync(world.PublicationId, Ct);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("scheduled");
        AssertBoundedReschedule(await harness.NextAttemptAtAsync(world.PublicationId), reset);
        harness.Google.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Publish_ClientBroken_HoldsWithPlatformIncidentAndConsumesNoQuota()
    {
        // D14/YR24: `invalid_client` is a problem of the OAuth client, not of the user's channel, so the row is held
        // with a deferred `next_attempt_at`, one incident row and one event, the connection is untouched and the
        // reservation is given back — the client being broken is not the user's spent unit.
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness, tokenExpiresIn: TimeSpan.Zero);
        harness.Google.Options.TokenStatusCode = System.Net.HttpStatusCode.BadRequest;
        harness.Google.Options.TokenError = "invalid_client";
        var connectionBefore = await harness.ChannelRowAsync(world.Channel.Id);

        await RunTolerantlyAsync(harness, world.PublicationId, attempt: 1);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("scheduled");
        (await harness.ErrorCodeAsync(world.PublicationId)).ShouldBe(PublicationErrorCodes.PlatformIncident);
        (await harness.NextAttemptAtAsync(world.PublicationId)).ShouldBe(
            harness.Clock.UtcNow + HoldDeferral,
            "a held row carries the deferred check instant, never null (YR24)");
        (await harness.PayloadKeyAsync(world.PublicationId, PublicationPayload.HeldByIncidentIdKey))
            .ShouldNotBeEmpty("the hold names the incident it waits on");
        (await harness.OpenIncidentCountAsync()).ShouldBe(1);
        (await harness.OutboxCountAsync("platform_incident_raised.v1"))
            .ShouldBeGreaterThan(0, "the incident must reach the alert path");
        (await harness.ChannelRowAsync(world.Channel.Id)).ShouldBe(
            connectionBefore,
            "a broken client must not change the connection row");
        (await harness.QuotaUnitsAsync(harness.QuotaDayToday()) ?? 0).ShouldBe(0);
        harness.Google.SessionCreateCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Publish_ConnectionExpired_FailsChannelReauthRequired_AndEmitsChannelConnectionChanged()
    {
        // `invalid_grant` is the owner's channel needing a reconnect: the connection becomes `expired`, the row is
        // `failed` for good (a retry cannot consent again) and both events travel.
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness, tokenExpiresIn: TimeSpan.Zero);
        harness.Google.Options.TokenStatusCode = System.Net.HttpStatusCode.BadRequest;
        harness.Google.Options.TokenError = "invalid_grant";

        await RunTolerantlyAsync(harness, world.PublicationId, attempt: 1);

        (await harness.ChannelStatusAsync(world.Channel.Id)).ShouldBe("expired");
        (await harness.StatusAsync(world.PublicationId)).ShouldBe("failed");
        (await harness.ErrorCodeAsync(world.PublicationId)).ShouldBe(PublicationErrorCodes.ChannelReauthRequired);
        (await harness.NextAttemptAtAsync(world.PublicationId)).ShouldBeNull("a failed row carries no retry");
        (await harness.OutboxCountAsync("publication_updated.v1", world.PublicationId)).ShouldBe(1);
        (await harness.OutboxRowsMentioningAsync(world.Channel.Id.ToString("D", CultureInfo.InvariantCulture)))
            .ShouldBeGreaterThan(0, "the connection change reaches the outbox");
        (await harness.QuotaUnitsAsync(harness.QuotaDayToday()) ?? 0)
            .ShouldBe(0, "nothing was sent, so the unit goes back");
        harness.Google.SessionCreateCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Publish_ValidationRejected_FailsUploadFailed_WithGoogleReason()
    {
        // A 4xx the metadata rules could not have caught: the row fails, the raw reason is kept as the diagnostic and
        // is never promoted to `error_code` (D4/D17).
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness);
        harness.Google.Scenario = UploadScenario.InvalidPublishAt();

        await RunTolerantlyAsync(harness, world.PublicationId, attempt: 1);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("failed");
        (await harness.ErrorCodeAsync(world.PublicationId)).ShouldBe(PublicationErrorCodes.UploadFailed);
        (await harness.PayloadKeyAsync(world.PublicationId, PublicationPayload.LastGoogleReasonKey))
            .ShouldBe(UploadScenario.Reasons.InvalidPublishAt);
        (await harness.NextAttemptAtAsync(world.PublicationId)).ShouldBeNull();
        (await harness.OpenIncidentCountAsync()).ShouldBe(0, "a metadata refusal is not a client-wide incident");
    }

    [Fact]
    public async Task Publish_TransportFailureFiveTimes_EndsFailedUploadFailed()
    {
        // The last attempt owes the row a verdict: a 5xx that outlives Hangfire's attempts is `youtube_upload_failed`
        // (the reason, when Google gave one, is the diagnostic key; a 5xx gives none). Attempts 1-4 must leave the
        // row resumable.
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness);
        harness.Google.Scenario = UploadScenario.PersistentServerErrors;

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await RunTolerantlyAsync(harness, world.PublicationId, attempt);

            (await harness.StatusAsync(world.PublicationId)).ShouldBe(
                "uploading",
                $"attempt {attempt} of {MaxAttempts} is retryable, so the row must stay resumable");
        }

        await RunTolerantlyAsync(harness, world.PublicationId, MaxAttempts);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("failed");
        (await harness.ErrorCodeAsync(world.PublicationId)).ShouldBe(PublicationErrorCodes.UploadFailed);
        (await harness.NextAttemptAtAsync(world.PublicationId)).ShouldBeNull();
        harness.Google.SessionCreateCalls.ShouldBe(1, "the retries resume the one session instead of opening more");

        // A 503 carries no Google `reason`, so the diagnostic key is either absent or a short token — never a body,
        // a URL or a header echoed into the row. The reason-preserving case is the metadata refusal above.
        var reason = await harness.PayloadKeyAsync(world.PublicationId, PublicationPayload.LastGoogleReasonKey);
        reason.ShouldNotContain("/", Case.Sensitive);
        reason.Length.ShouldBeLessThanOrEqualTo(64);
    }

    [Fact]
    public async Task Publish_TwoConcurrentRuns_PresentTheRefreshTokenOnce_AndUploadOnce()
    {
        // Two runs for one publication (a duplicate enqueue) with an expired access token: the connection lock makes
        // exactly one refresh happen (the loser re-reads the fresh token, F21), the row lock makes exactly one upload
        // happen, and neither run deadlocks (connection before publication, SEC-5).
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness, tokenExpiresIn: TimeSpan.Zero);

        // Either run may lose: the one that finds the other's session-create claim standing (SEC-8) creates nothing and
        // asks for another attempt, which the harness tolerates here because the row is what this test asserts.
        await Task
            .WhenAll(
                RunTolerantlyAsync(harness, world.PublicationId, attempt: 1),
                RunTolerantlyAsync(harness, world.PublicationId, attempt: 1))
            .WaitAsync(TimeSpan.FromSeconds(120), Ct);

        harness.Google.RefreshCalls.ShouldBe(1, "the refresh token is presented once, whatever the race does");
        harness.Google.SessionCreateCalls.ShouldBe(1, "the claim on the row serialises the two runs into one upload");
        // Both runs may stream: the loser's probe can land before the winner's chunks are acknowledged, so it resumes
        // from that offset and re-sends the same byte ranges into the same session — idempotent by the byte-range
        // contract, and one video, which the id assertion below proves. A fresh session per run would be the defect.
        harness.Google.ChunkRequests.ShouldBeGreaterThanOrEqualTo(3, "the object was streamed to the one session");
        (await harness.StatusAsync(world.PublicationId)).ShouldBe("processing");
        (await harness.ExternalPostIdAsync(world.PublicationId)).ShouldBe(harness.Google.LastVideoId);
    }

    [Fact]
    public async Task Publish_TerminalStatesAreNoOps()
    {
        // A duplicate enqueue of a row that already finished (or was cancelled) must not re-open anything, whatever
        // state it reached: each is seeded without an external id, so no reconcile call is owed either.
        await using var harness = await StartAsync();
        var seeded = new List<(Guid Id, string Row)>();

        foreach (var status in new[]
                 {
                     PublicationStatus.Processing,
                     PublicationStatus.Published,
                     PublicationStatus.Failed,
                     PublicationStatus.Cancelled,
                 })
        {
            var world = await SeedReadyAsync(harness, status: status);
            seeded.Add((world.PublicationId, await harness.PublicationRowAsync(world.PublicationId)));
        }

        foreach (var (id, row) in seeded)
        {
            var sent = await harness.SendAsync(id, Ct);

            sent.IsError.ShouldBeFalse($"a terminal row is a no-op, not an error: {sent.FirstError}");
            (await harness.PublicationRowAsync(id)).ShouldBe(row, "a no-op writes nothing");
        }

        harness.Google.Calls.ShouldBeEmpty("a terminal row reaches no Google endpoint");
        harness.Blobs.OpenReads.ShouldBe(0);
        (await harness.QuotaUnitsAsync(harness.QuotaDayToday()) ?? 0).ShouldBe(0);
    }

    [Fact]
    public async Task Publish_FlagOff_ReturnsFeatureDisabled_AndTouchesNothing()
    {
        // D-speed / rollback: with `youtube_publish` off the job is a logged no-op (never five retries), and no row,
        // Google endpoint, blob or ledger entry is touched.
        await using var harness = await StartAsync(flagOn: false);
        var world = await SeedReadyAsync(harness);
        var before = await harness.PublicationRowAsync(world.PublicationId);

        await Should.NotThrowAsync(() => harness.RunAsync(world.PublicationId, Ct));
        var sent = await harness.SendAsync(world.PublicationId, Ct);

        sent.IsError.ShouldBeTrue();
        sent.FirstError.Code.ShouldBe("feature_disabled");
        (await harness.PublicationRowAsync(world.PublicationId)).ShouldBe(before);
        harness.Google.Calls.ShouldBeEmpty();
        harness.Blobs.OpenReads.ShouldBe(0);
        (await harness.QuotaUnitsAsync(harness.QuotaDayToday()) ?? 0).ShouldBe(0);
    }

    [Fact]
    public async Task Publish_OpensTheBlobOnce_AndDisposesIt()
    {
        // D18: one open, one stream, disposed on the success path and on the failure path — the source is never held
        // across the two, and a leak would keep the S3 connection (and its buffer) alive per retry.
        await using var harness = await StartAsync();
        var successful = await SeedReadyAsync(harness);

        await harness.RunAsync(successful.PublicationId, Ct);

        harness.Blobs.OpenReads.ShouldBe(1);
        harness.Blobs.Disposals.ShouldBe(1, "the stream of a successful run is disposed");
        (await harness.StatusAsync(successful.PublicationId)).ShouldBe("processing");

        var failing = await SeedReadyAsync(harness);
        harness.Google.Scenario = UploadScenario.PersistentServerErrors;

        await RunTolerantlyAsync(harness, failing.PublicationId, attempt: 1);

        harness.Blobs.OpenReads.ShouldBe(2);
        harness.Blobs.Disposals.ShouldBe(2, "the stream of a failed run is disposed too");
        harness.Blobs.MaxRequestedBytes.ShouldBeLessThanOrEqualTo(
            PublishYouTubeJobHarness.PayloadLength,
            "the upload streams the object instead of buffering it whole");
    }

    [Fact]
    public async Task Publish_BuildsTheRequestFromTheStickyUploadPlan_NotFromLiveOptions()
    {
        // F13/CODE-3: `platform_payload.upload` is read verbatim, and it is the ONLY source of the privacy and the
        // scheduled instant. An approval that lands after the plan was written leaves `privacy = public` and a future
        // `publish_at` on the row; the request must still follow the pre-audit plan (private, no instant), because
        // that plan is what the user's upload was decided on.
        await using var harness = await StartAsync(auditStatus: YouTubeAuditStatuses.Approved);
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        var episode = await harness.SeedEpisodeAsync(userId, harness.Blobs.DefaultLength, ct: Ct);
        var publicationId = await harness.SeedPublicationAsync(
            userId,
            episode.EpisodeId,
            channel.Id,
            platformPayloadJson: """{"upload":{"privacyStatus":"private","preAudit":true},"preAudit":true,"requestedPrivacy":"public"}""",
            privacy: "public",
            publishAt: PublishYouTubeJobHarness.Start.AddDays(2),
            ct: Ct);

        await harness.RunAsync(publicationId, Ct);

        var create = harness.Google.LastSessionCreate;
        create.ShouldNotBeNull();
        create!.PrivacyStatus.ShouldBe("private", "the sticky plan, not the row's live privacy, decides the privacy");
        create.PublishAt.ShouldBeNull("the plan carried no publishAt, so a future publish_at column must not add one");
        create.ContainsSyntheticMedia.ShouldBe(true, "the row's D8 declaration reaches the request");
        create.DeclaredLength.ShouldBe(PublishYouTubeJobHarness.PayloadLength);
        create.Title.ShouldBe(PublishYouTubeJobHarness.EpisodeTitle, "the metadata is sent, just never logged");
    }

    [Fact]
    public async Task Publish_SendsThePostAuditPlanVerbatim_EvenWhenTheRowStillCarriesPreAuditColumns()
    {
        // The other half of F13: once a plan says public + an instant, that is what travels — the row's own
        // pre-audit `private` column must not override the plan it was replaced by.
        const string instant = "2026-11-01T10:00:00.0000000+00:00";
        await using var harness = await StartAsync(auditStatus: YouTubeAuditStatuses.Approved);
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        var episode = await harness.SeedEpisodeAsync(userId, harness.Blobs.DefaultLength, ct: Ct);
        var publicationId = await harness.SeedPublicationAsync(
            userId,
            episode.EpisodeId,
            channel.Id,
            platformPayloadJson: """{"upload":{"privacyStatus":"public","publishAt":"2026-11-01T10:00:00.0000000+00:00","preAudit":false},"preAudit":true,"requestedPrivacy":"public"}""",
            privacy: "private",
            ct: Ct);

        await harness.RunAsync(publicationId, Ct);

        var create = harness.Google.LastSessionCreate;
        create.ShouldNotBeNull();
        create!.PrivacyStatus.ShouldBe("public");

        var sent = create.PublishAt.ShouldNotBeNull("the plan's instant is sent");
        DateTimeOffset.Parse(sent, CultureInfo.InvariantCulture).ShouldBe(DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Publish_WithoutAStickyPlan_DerivesOneFromTheRowAndPersistsIt()
    {
        // CODE-3's fallback on a branch that has no 06.2.4-T1 writer yet: a row with no `upload` key still uploads,
        // and the plan it used is persisted in the same claim transaction, so a retry of the row cannot send a
        // different request than the first attempt did.
        await using var harness = await StartAsync(auditStatus: YouTubeAuditStatuses.Approved);
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        var episode = await harness.SeedEpisodeAsync(userId, harness.Blobs.DefaultLength, ct: Ct);
        var publishAt = PublishYouTubeJobHarness.Start.AddDays(3);
        var publicationId = await harness.SeedPublicationAsync(
            userId,
            episode.EpisodeId,
            channel.Id,
            platformPayloadJson: """{"preAudit":false,"requestedPrivacy":"public"}""",
            privacy: "public",
            publishAt: publishAt,
            ct: Ct);

        await harness.RunAsync(publicationId, Ct);

        var create = harness.Google.LastSessionCreate;
        create.ShouldNotBeNull();
        create!.PrivacyStatus.ShouldBe("private", "an instant is only accepted with a private upload (API rule)");
        DateTimeOffset
            .Parse(create.PublishAt.ShouldNotBeNull(), CultureInfo.InvariantCulture)
            .ShouldBe(publishAt);

        (await harness.PayloadKeyKindAsync(publicationId, "upload")).ShouldBe(
            "object",
            "the derived plan is persisted, so the next attempt reads the same decision");
        var stored = PublicationPayload.ReadUpload(await harness.PlatformPayloadAsync(publicationId));
        stored.ShouldNotBeNull();
        stored!.PrivacyStatus.ShouldBe("private");
        stored.PublishAt.ShouldBe(publishAt);
        stored.PreAudit.ShouldBeFalse();
    }

    [Fact]
    public async Task Publish_Resume_OpensOnlyTheSliceGoogleStillNeeds()
    {
        // PERF-2/CR-17: the probe says Google already holds a prefix, so the resume opens the store at that offset
        // instead of reading the object from byte 0 and discarding it.
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness);
        const long prefix = 2 * PublishYouTubeJobHarness.ChunkSizeBytes;

        harness.Google.Scenario = UploadScenario.DropChunksAfter(2);
        await RunTolerantlyAsync(harness, world.PublicationId, attempt: 1);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("uploading", "a transport failure keeps the session");
        var sessionUri = new Uri(harness.Google.LastSessionCreate!.Location);
        harness.Google.HeldBytes(sessionUri).ShouldBe(prefix, "the session holds exactly the two accepted chunks");

        harness.Google.Scenario = UploadScenario.None;
        harness.Blobs.ResetReads();
        await RunTolerantlyAsync(harness, world.PublicationId, attempt: 2);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("processing");
        harness.Google.SessionCreateCalls.ShouldBe(1, "a resume reuses the session");
        harness.Blobs.RangeOpens.ShouldBe([prefix], "the store is opened at the offset the probe reported");
        harness.Blobs.FirstByteRead.ShouldBe(prefix, "no byte of the uploaded prefix is read a second time");

        var resumed = harness.Google.Chunks.Where(chunk => !chunk.IsProbe && chunk.Start >= prefix).ToList();
        resumed.ShouldNotBeEmpty();
        resumed[0].Start.ShouldBe(prefix, "the first range the resume declares starts where the session stopped");
    }

    [Fact]
    public async Task Publish_ReserveWritesQuotaReservedDay_AndClearsItOnSuccessAndOnRelease()
    {
        // S72-1: the key exists exactly while a unit is outstanding. Reserved and still in flight -> present and one
        // unit spent; uploaded -> removed, the unit spent for good; released before the session opened -> removed,
        // the unit back.
        var day = PacificQuotaDay.Day(PublishYouTubeJobHarness.Start);
        await using var harness = await StartAsync();
        var inFlight = await SeedReadyAsync(harness);
        harness.Google.Scenario = UploadScenario.PersistentServerErrors;

        await RunTolerantlyAsync(harness, inFlight.PublicationId, attempt: 1);

        (await harness.PayloadKeyKindAsync(inFlight.PublicationId, PublicationPayload.QuotaReservedDayKey)).ShouldBe(
            "string",
            "06.2.6-T1's reader accepts a bare ISO day and reads an object as unreadable");
        (await harness.PayloadKeyAsync(inFlight.PublicationId, PublicationPayload.QuotaReservedDayKey)).ShouldBe(
            PublicationPayload.FormatQuotaDay(day),
            "the reserve step writes the ISO Pacific day of the reservation");
        (await harness.QuotaUnitsAsync(day)).ShouldBe(1, "one session create, one unit");

        harness.Google.Scenario = UploadScenario.None;
        var uploaded = await SeedReadyAsync(harness);

        await harness.RunAsync(uploaded.PublicationId, Ct);

        (await harness.PayloadKeyAsync(uploaded.PublicationId, PublicationPayload.QuotaReservedDayKey))
            .ShouldBeEmpty("an uploaded row holds nothing to give back");
        (await harness.QuotaUnitsAsync(day)).ShouldBe(2, "the second upload spent a second unit");

        var released = await SeedReadyAsync(harness, tokenExpiresIn: TimeSpan.Zero);
        harness.Google.Options.TokenStatusCode = System.Net.HttpStatusCode.BadRequest;
        harness.Google.Options.TokenError = "invalid_grant";

        await RunTolerantlyAsync(harness, released.PublicationId, attempt: 1);

        (await harness.PayloadKeyAsync(released.PublicationId, PublicationPayload.QuotaReservedDayKey))
            .ShouldBeEmpty("a released reservation removes the key, never rewrites it");
        (await harness.QuotaUnitsAsync(day)).ShouldBe(
            2,
            "a failure before the session opened puts the unit back");
        harness.Google.SessionCreateCalls.ShouldBe(
            2,
            "only the two rows that reached a session created one; the reauth failure created none");
    }

    [Fact]
    public async Task Resume_WithQuotaReservedDaySet_SkipsTheSecondReserve()
    {
        // YR27-2: the unit belongs to the session create, so a resume of an existing session must not spend another.
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness);
        harness.Google.Scenario = UploadScenario.DropAfterLastChunk();
        await RunTolerantlyAsync(harness, world.PublicationId, attempt: 1);

        var day = harness.QuotaDayToday();
        (await harness.QuotaUnitsAsync(day)).ShouldBe(1);
        harness.Google.Scenario = UploadScenario.None;

        await RunTolerantlyAsync(harness, world.PublicationId, attempt: 2);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("processing");
        (await harness.QuotaUnitsAsync(day)).ShouldBe(1, "a resume reuses the unit its session already consumed");
        harness.Google.SessionCreateCalls.ShouldBe(1);
    }

    [Fact]
    public async Task SessionExpiredRestart_ReservesAFreshUnit()
    {
        // YR27-3: the stored session is gone (a 404 probe), so this run opens a new one and must reserve again —
        // the old unit bought a session nobody can use.
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness, tokenExpiresIn: TimeSpan.Zero);
        var day = harness.QuotaDayToday();
        await harness.SeedQuotaAsync(day, unitsUsed: 1, unitsLimit: 100, ct: Ct);

        // The fake answers a probe only on its own session route (`/upload/youtube/v3/session/{id}?upload_id={id}`);
        // a URI outside it would 404 without ever counting a probe, and the restart would look like a fresh start.
        var goneSession = new Uri(
            harness.Google.UploadBaseUrl,
            $"/{FakeGoogleServer.UploadSessionPrefix.TrimStart('/')}gone-session"
            + $"?{FakeGoogleServer.UploadIdQueryKey}=gone-session");
        var payload = await harness.PayloadForSessionAsync(world.Channel.Id, goneSession.AbsoluteUri, day, Ct);

        // A second episode: one episode may hold only one non-terminal publication, so the resume's row cannot share
        // the world's episode with the row SeedReadyAsync already wrote.
        var resumed = await harness.SeedEpisodeAsync(world.UserId, harness.Blobs.DefaultLength, ct: Ct);
        var publicationId = await harness.SeedPublicationAsync(
            world.UserId,
            resumed.EpisodeId,
            world.Channel.Id,
            status: PublicationStatus.Uploading,
            platformPayloadJson: payload,
            ct: Ct);
        harness.Google.Scenario = UploadScenario.None;

        await RunTolerantlyAsync(harness, publicationId, attempt: 2);

        (await harness.StatusAsync(publicationId)).ShouldBe("processing");
        (await harness.QuotaUnitsAsync(day)).ShouldBe(2, "the restart reserved a fresh unit");
        harness.Google.SessionCreateCalls.ShouldBe(1, "the gone session is replaced by exactly one new session");
        harness.Google.GoneSessionProbes.ShouldBeGreaterThan(
            0,
            "the restart begins with the probe that found the stored session gone (the fake counts only a probe of a session it never had)");
    }

    [Fact]
    public async Task Resume_FailsBeforeResumeSend_DoesNotReleaseTheEarlierUnit()
    {
        // The mirror of the release rule: a resume that fails at the token step consumed nothing in this run, but its
        // session create already spent the earlier unit, so the key and the counter must be left alone.
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness, tokenExpiresIn: TimeSpan.Zero);
        var day = harness.QuotaDayToday();
        await harness.SeedQuotaAsync(day, unitsUsed: 1, unitsLimit: 100, ct: Ct);
        var session = new Uri(harness.Google.UploadBaseUrl, "session/old-session?upload_id=old-session");
        var payload = await harness.PayloadForSessionAsync(world.Channel.Id, session.AbsoluteUri, day, Ct);

        // A second episode: one episode may hold only one non-terminal publication, so the resume's row cannot share
        // the world's episode with the row SeedReadyAsync already wrote.
        var resumed = await harness.SeedEpisodeAsync(world.UserId, harness.Blobs.DefaultLength, ct: Ct);
        var publicationId = await harness.SeedPublicationAsync(
            world.UserId,
            resumed.EpisodeId,
            world.Channel.Id,
            status: PublicationStatus.Uploading,
            platformPayloadJson: payload,
            ct: Ct);
        harness.Google.Options.TokenStatusCode = System.Net.HttpStatusCode.BadRequest;
        harness.Google.Options.TokenError = "invalid_grant";

        await RunTolerantlyAsync(harness, publicationId, attempt: 3);

        (await harness.StatusAsync(publicationId)).ShouldBe("failed");
        (await harness.ErrorCodeAsync(publicationId)).ShouldBe(PublicationErrorCodes.ChannelReauthRequired);
        (await harness.QuotaUnitsAsync(day)).ShouldBe(1, "this run reserved nothing, so nothing may be released");
        (await harness.PayloadKeyAsync(publicationId, PublicationPayload.QuotaReservedDayKey)).ShouldBe(
            PublicationPayload.FormatQuotaDay(day),
            "the earlier session's unit is still outstanding");
        harness.Google.SessionCreateCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Publish_MarkProcessingWritesTheProcessingDeadline()
    {
        // 06.2.5-T1 claims the row on this block: `startedAt` is the run's clock, `deadlineAt` is `Poll:MaxMinutes`
        // later, and `polls` starts at zero.
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness);

        await harness.RunAsync(world.PublicationId, Ct);

        var payload = await harness.PlatformPayloadAsync(world.PublicationId);
        var processing = PublicationPayload.ReadProcessing(payload);
        processing.ShouldNotBeNull("a processing row must carry the poller's claim block");
        processing!.StartedAt.ShouldBe(harness.Clock.UtcNow);
        processing.DeadlineAt.ShouldBe(harness.Clock.UtcNow.AddMinutes(45));
        processing.Polls.ShouldBe(0);
    }

    [Fact]
    public async Task Publish_TotalMismatchWithAssetsSizeBytes_FailsBeforeAnyGoogleCall()
    {
        // CR-18: the object's real length and the asset's `size_bytes` disagree, so the metadata the request would
        // declare is a lie. That is caught before the session exists — no Google call, no blob read, no unit spent.
        await using var harness = await StartAsync();
        harness.Blobs.LengthOverride = PublishYouTubeJobHarness.PayloadLength + 1;
        var world = await SeedReadyAsync(harness);

        await RunTolerantlyAsync(harness, world.PublicationId, attempt: 1);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("failed");
        (await harness.ErrorCodeAsync(world.PublicationId)).ShouldBe(PublicationErrorCodes.UploadFailed);
        harness.Google.Calls.ShouldBeEmpty("the mismatch is detectable without asking YouTube anything");
        (await harness.QuotaUnitsAsync(harness.QuotaDayToday()) ?? 0).ShouldBe(0);
    }

    [Fact]
    public async Task Publish_NeverLogsTitleDescriptionTokenOrSessionUri()
    {
        // D17: the metadata and the credentials are data, not diagnostics. This run refreshes (an issued token exists)
        // and creates a session (a URI exists), so every value that could leak is present in the process when the
        // assertion runs.
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness, tokenExpiresIn: TimeSpan.Zero);

        await harness.RunAsync(world.PublicationId, Ct);

        harness.Google.IssuedAccessTokens.Count.ShouldBe(1, "the run must have rotated a token for this test to bite");
        var sessionId = FakeGoogleServer.SessionId(new Uri(harness.Google.LastSessionCreate!.Location));
        var logs = harness.Logs.AllText();

        logs.ShouldNotContain(PublishYouTubeJobHarness.EpisodeTitle, Case.Sensitive);
        logs.ShouldNotContain(PublishYouTubeJobHarness.Description, Case.Sensitive);
        logs.ShouldNotContain(PublishYouTubeJobHarness.AccessToken, Case.Sensitive);
        logs.ShouldNotContain(PublishYouTubeJobHarness.RefreshToken, Case.Sensitive);
        logs.ShouldNotContain(harness.Google.IssuedAccessTokens[0], Case.Sensitive);
        logs.ShouldNotContain(sessionId, Case.Sensitive);

        (await harness.AuditRowsMentioningAsync(sessionId)).ShouldBe(0, "an upload writes no audit row at all");
        (await harness.OutboxRowsMentioningAsync(sessionId)).ShouldBe(0, "a session URI never travels in an event");
    }

    private static void AssertBoundedRescheduleToFirstReset(DateTimeOffset? nextAttemptAt) =>
        AssertBoundedReschedule(nextAttemptAt, FirstReset);

    private static void AssertBoundedReschedule(DateTimeOffset? nextAttemptAt, DateTimeOffset reset)
    {
        nextAttemptAt.ShouldNotBeNull("a quota reschedule always names its instant");
        nextAttemptAt.Value.ShouldBeGreaterThanOrEqualTo(reset, "the next PT midnight is the earliest retry");
        nextAttemptAt.Value.ShouldBeLessThanOrEqualTo(reset + JitterBound, "the jitter is bounded by ten minutes");
    }

    /// <summary>
    /// Runs one attempt, tolerating the transport exception a retryable attempt is allowed to throw: Hangfire's
    /// attempt counter is the deployment's, and the test asserts the row rather than the throw.
    /// </summary>
    /// <param name="harness">The world.</param>
    /// <param name="publicationId">The row.</param>
    /// <param name="attempt">The attempt number; below 5 the exception is part of the contract.</param>
    private static async Task RunTolerantlyAsync(
        PublishYouTubeJobHarness harness,
        Guid publicationId,
        int attempt)
    {
        try
        {
            await harness.RunAttemptAsync(publicationId, attempt, MaxAttempts, Ct);
        }
        catch (Exception) when (attempt < MaxAttempts)
        {
            // A retryable failure surfaces to Hangfire; the row is what this test asserts.
        }
    }

    private Task<PublishYouTubeJobHarness> StartAsync(
        bool flagOn = true,
        Action<FakeGoogleOptions>? google = null,
        string auditStatus = YouTubeAuditStatuses.Pending,
        int unitsPerDay = 100,
        FakeClock? clock = null) =>
        PublishYouTubeJobHarness.StartAsync(
            Database,
            Ct,
            flagOn: flagOn,
            google: google,
            auditStatus: auditStatus,
            unitsPerDay: unitsPerDay,
            clock: clock);

    private static async Task<World> SeedReadyAsync(
        PublishYouTubeJobHarness harness,
        PublicationStatus status = PublicationStatus.Scheduled,
        bool consent = true,
        string? externalPostId = null,
        TimeSpan? tokenExpiresIn = null)
    {
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, expiresIn: tokenExpiresIn, ct: Ct);
        var episode = await harness.SeedEpisodeAsync(userId, harness.Blobs.DefaultLength, ct: Ct);
        var publicationId = await harness.SeedPublicationAsync(
            userId,
            episode.EpisodeId,
            channel.Id,
            status: status,
            consent: consent,
            externalPostId: externalPostId,
            ct: Ct);

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
}
