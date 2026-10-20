using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Google;
using NightTales.TestKit.Telemetry;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// <c>DELETE /api/v1/publications/{id}</c> against the real Api and a real PostgreSQL 17 (06.2.6-T1,
/// 06 README D7/D10/D13/D15/D17): a cancel before the upload starts moves
/// <c>awaiting_consent|scheduled</c> to <c>cancelled</c> under the <c>If-Match</c> precondition, releases the
/// quota unit the payload reserved, and leaves exactly one <c>publication_updated.v1</c> row and one
/// <c>publishing.cancelled</c> audit row of ids and statuses only — while a repeat, a stale tag, a missing
/// tag, an <c>uploading</c> row, a foreign row and a flag-off host all write nothing.
/// </summary>
/// <remarks>
/// <para>
/// Every test seeds its own owner through the TestKit builders, so the one database a class shares never
/// makes two tests see each other's rows (D13). Every outbox and audit assertion is scoped to the
/// publication id this test seeded, because other tests of the class leave their own rows in the same
/// database.
/// </para>
/// <para>
/// The <c>youtube_publish</c> flag row and the clock are in place before <c>ApiFactory.Services</c> is first
/// touched, because the host caches both. The clock is a <see cref="FakeClock"/> pinned to
/// <see cref="Start"/>; the quota ledger the host resolves is the production
/// <c>YouTubeQuotaLedger</c> over <c>platform_quota_usage</c>, so the release is asserted on the real
/// table (06.3.1-T1) and not on a fake.
/// </para>
/// <para>
/// <b>What this file cannot pin yet.</b> The upload job of 06.2.3-T2 does not exist in this branch, so
/// <see cref="JobAfterCancel_DoesNotUpload"/> pins the domain/state guard only: after the cancel it loads the
/// row through the same <c>IAppDbContext.LockPublicationForUpdateAsync</c> lock the job takes, calls
/// <c>Publication.StartUpload</c>, and proves the guard refuses with <c>publication_invalid_state</c> and that
/// the <see cref="FakeGoogleServer"/> received no session create. The job-level assertion — "a scheduled
/// <c>PublishYouTubeJob</c> run for this id performs no upload" — stays an open item of 06.2.3-T2.
/// </para>
/// <para>
/// Worktrees are not indexed by codebase-memory, so this file was written from <c>read</c> over absolute
/// paths and mirrors <see cref="ConsentPublicationTests"/> (fixtures, auth, flag row, seeds) and
/// <c>CancelRenderEndpointTests</c> (the DELETE/204 shape and its row probes).
/// </para>
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
[Trait("Category", "Integration")]
public sealed class CancelPublicationTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The publication resource group (06.2.2-T1 task §Endpoint).</summary>
    private const string PublicationsRoute = "/api/v1/publications";

    private const string FeatureKey = "youtube_publish";

    /// <summary>The outbox type of D13, <c>{ ids, status }</c> only.</summary>
    private const string OutboxEventType = "publication_updated.v1";

    /// <summary>D17's audit action of this command.</summary>
    private const string AuditAction = "publishing.cancelled";

    /// <summary>A word only the seeded publication carries, so an ids-only row can be proven free of it.</summary>
    private const string TitleCanary = "Tựa đề canary 06.2.6";

    /// <summary>A word only the seeded description carries.</summary>
    private const string DescriptionCanary = "Mô tả canary 06.2.6";

    /// <summary>A word only the seeded channel's title carries.</summary>
    private const string ChannelCanary = "Kênh canary 06.2.6";

    /// <summary>The Pacific quota day the seeded reservation was made on (the writer is 06.2.3-T2).</summary>
    private const string QuotaDay = "2026-10-02";

    /// <summary>A second day, for the "the key is absent" comparison: the ledger row there must not move.</summary>
    private const string OtherDay = "2026-10-03";

    /// <summary>A third day, for the malformed-key case.</summary>
    private const string MalformedDay = "2026-10-04";

    /// <summary>A fourth day whose payload names it but which has no ledger row at all.</summary>
    private const string MissingRowDay = "2026-10-05";

    /// <summary>A payload without the quota key: the cancel must leave the ledger exactly as it was.</summary>
    private const string PayloadWithoutQuotaKey = """{"uploadSessionUri":"https://upload.example.test/session/1"}""";

    private static readonly DateTimeOffset Start = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Cancel_204_OnScheduledRow()
    {
        var world = await SeedAsync(status: PublicationStatus.Scheduled);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await DeleteAsync(client, world.PublicationId, CurrentTag(world.PublicationId));
        var text = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, text);
        text.ShouldBeEmpty("a 204 carries no body: there is no DTO and no ETag to return (task §Endpoint)");
        response.Headers.ETag.ShouldBeNull("a 204 carries no tag; the client refetches the row");

        PublicationStatusOf(world.PublicationId).ShouldBe("cancelled");

        var payloads = OutboxPayloads(world.PublicationId);
        payloads.Count.ShouldBe(1, "exactly one publication_updated.v1 row, scoped to this publication");
        using (var payload = JsonDocument.Parse(payloads[0]))
        {
            payload.RootElement.GetProperty("status").GetString().ShouldBe("cancelled");
        }

        var audit = AuditData(world.PublicationId);
        audit.Count.ShouldBe(1, "exactly one publishing.cancelled row, scoped to this publication");
        using (var data = JsonDocument.Parse(audit[0]))
        {
            data.RootElement.GetProperty("previousStatus").GetString().ShouldBe("scheduled");
            data.RootElement.GetProperty("actor").GetString().ShouldBe("user");
        }

        audit[0].ShouldNotContain(TitleCanary, Case.Sensitive);
        audit[0].ShouldNotContain(DescriptionCanary, Case.Sensitive);
    }

    [Fact]
    public async Task Cancel_AwaitingConsent_204_WithVetoListPreviousStatus()
    {
        // C9: the veto-list row cancels before the consent was ever given.
        var world = await SeedAsync();
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await DeleteAsync(client, world.PublicationId, CurrentTag(world.PublicationId));
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        PublicationStatusOf(world.PublicationId).ShouldBe("cancelled");

        var audit = AuditData(world.PublicationId);
        audit.Count.ShouldBe(1, "D17: one publishing.cancelled row per cancel");
        using var json = JsonDocument.Parse(audit[0]);
        json.RootElement.GetProperty("previousStatus").GetString().ShouldBe("awaiting_consent");

        var payloads = OutboxPayloads(world.PublicationId);
        payloads.Count.ShouldBe(1, "D13: exactly one publication_updated.v1 row per cancel");
        using var payload = JsonDocument.Parse(payloads[0]);
        payload.RootElement.GetProperty("status").GetString().ShouldBe("cancelled");
    }

    [Fact]
    public async Task IdempotentRepeat()
    {
        var world = await SeedAsync(status: PublicationStatus.Scheduled, platformPayloadJson: QuotaPayload(QuotaDay));
        await SetFlagAsync(true);
        await SeedQuotaRowAsync(QuotaDay, unitsUsed: 1, unitsLimit: 6);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        // The tag the client read before cancelling: after the first write it names no current row, and a
        // retry after a lost response still carries it (D7's lost-response rule).
        var preCancelTag = CurrentTag(world.PublicationId);

        using (var first = await DeleteAsync(client, world.PublicationId, preCancelTag))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.NoContent, await first.Content.ReadAsStringAsync(Ct));
        }

        var writtenXmin = Xmin(world.PublicationId);
        var ledgerAfterFirst = QuotaRow(QuotaDay);
        UnitsUsed(QuotaDay).ShouldBe(0, "the first cancel released the reserved unit");

        using (var repeat = await DeleteAsync(client, world.PublicationId, preCancelTag))
        {
            repeat.StatusCode.ShouldBe(
                HttpStatusCode.NoContent,
                "an already-cancelled row is answered before the If-Match comparison: "
                + await repeat.Content.ReadAsStringAsync(Ct));
        }

        Xmin(world.PublicationId).ShouldBe(writtenXmin, "the repeat writes nothing at all");
        PublicationStatusOf(world.PublicationId).ShouldBe("cancelled");
        OutboxPayloads(world.PublicationId).Count.ShouldBe(1, "the repeat raises no second event");
        AuditData(world.PublicationId).Count.ShouldBe(1, "the repeat writes no second audit row");
        QuotaRow(QuotaDay).ShouldBe(ledgerAfterFirst, "the repeat releases nothing a second time");
    }

    [Fact]
    public async Task UploadingConflict()
    {
        // The upload already started: the row is not deletable, the user is told to use YouTube Studio.
        var world = await SeedAsync(status: PublicationStatus.Uploading, consentAt: Start);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        var before = Xmin(world.PublicationId);

        using var response = await DeleteAsync(client, world.PublicationId, CurrentTag(world.PublicationId));
        var problem = await ReadProblemAsync(response, HttpStatusCode.Conflict);

        problem.Code.ShouldBe("publication_invalid_state");
        problem.Errors.ShouldBeNull("the body carries no action list; the client refetches the row");

        PublicationStatusOf(world.PublicationId).ShouldBe("uploading", "the refused cancel keeps the status");
        Xmin(world.PublicationId).ShouldBe(before, "the refused cancel writes nothing");
        OutboxPayloads(world.PublicationId).ShouldBeEmpty("the refused cancel adds no event");
        AuditData(world.PublicationId).ShouldBeEmpty("the refused cancel adds no audit row");
    }

    [Fact]
    public async Task IfMatch428()
    {
        var world = await SeedAsync(status: PublicationStatus.Scheduled);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await DeleteAsync(client, world.PublicationId, ifMatch: null);

        var problem = await ReadProblemAsync(response, HttpStatusCode.PreconditionRequired);
        problem.Code.ShouldBe("if_match_required");

        AssertUntouched(world, "scheduled", "the 428 filter answers before the handler runs");
    }

    [Fact]
    public async Task IfMatch412()
    {
        var world = await SeedAsync(status: PublicationStatus.Scheduled);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        var current = Xmin(world.PublicationId);
        var stale = ETags.Publication(current + 1);

        using var response = await DeleteAsync(client, world.PublicationId, stale);

        var problem = await ReadProblemAsync(response, HttpStatusCode.PreconditionFailed);
        problem.Code.ShouldBe("publication_conflict");

        // 09.1.4-T1: the handler compares If-Match after loading the row, so the 412 can name the current tag.
        response.Headers.ETag.ShouldNotBeNull();
        response.Headers.ETag!.Tag.ShouldBe(
            ETags.Publication(current),
            "the 412 carries the row's current ETag for the retry");

        AssertUntouched(world, "scheduled", "a stale If-Match writes nothing");
    }

    [Fact]
    public async Task ForeignUser404()
    {
        var mine = await SeedAsync(status: PublicationStatus.Scheduled);
        var stranger = await SeedAsync(status: PublicationStatus.Scheduled);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(mine.UserId);

        using var response = await DeleteAsync(client, stranger.PublicationId, CurrentTag(stranger.PublicationId));

        var problem = await ReadProblemAsync(response, HttpStatusCode.NotFound);
        problem.Code.ShouldBe("not_found");

        AssertUntouched(stranger, "scheduled", "another owner's publication is 404, never 403 (D10)");
        OutboxPayloads(mine.PublicationId).ShouldBeEmpty("the refused cancel adds no event to the caller's rows");
        AuditData(mine.PublicationId).ShouldBeEmpty("the refused cancel writes no audit row");
    }

    [Fact]
    public async Task FlagOff404()
    {
        var world = await SeedAsync(status: PublicationStatus.Scheduled);
        await SetFlagAsync(false);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await DeleteAsync(client, world.PublicationId, CurrentTag(world.PublicationId));

        var problem = await ReadProblemAsync(response, HttpStatusCode.NotFound);
        problem.Code.ShouldBe("feature_disabled");

        AssertUntouched(world, "scheduled", "a disabled feature answers like a route that does not exist (BD13)");
    }

    [Fact]
    public async Task OutboxAndAudit()
    {
        var world = await SeedAsync(
            status: PublicationStatus.Scheduled,
            title: TitleCanary,
            description: DescriptionCanary,
            displayName: ChannelCanary);
        await SetFlagAsync(true);
        using var factory = NewFactory();
        using var client = factory.ClientFor(world.UserId);

        using var response = await DeleteAsync(client, world.PublicationId, CurrentTag(world.PublicationId));
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        var payloads = OutboxPayloads(world.PublicationId);
        payloads.Count.ShouldBe(1, "D13: exactly one publication_updated.v1 row per cancel");

        var payload = payloads[0];
        using (var json = JsonDocument.Parse(payload))
        {
            var root = json.RootElement;

            MemberNames(root).ShouldBeSubsetOf(OutboxPayloadMembers);
            root.GetProperty("publicationId").GetGuid().ShouldBe(world.PublicationId);
            root.GetProperty("episodeId").GetGuid().ShouldBe(world.EpisodeId);
            root.GetProperty("userId").GetGuid().ShouldBe(world.UserId);
            root.GetProperty("status").GetString().ShouldBe("cancelled");
        }

        // No user text reaches the outbox: not the title, not the description, not the channel's title.
        payload.ShouldNotContain(TitleCanary, Case.Sensitive);
        payload.ShouldNotContain(DescriptionCanary, Case.Sensitive);
        payload.ShouldNotContain(ChannelCanary, Case.Sensitive);

        var audit = AuditData(world.PublicationId);
        audit.Count.ShouldBe(1, "D17: one publishing.cancelled row inside the same transaction");

        AuditHas(world.PublicationId, "entity_type = 'publication'")
            .ShouldBeTrue("the audit row names the entity type the contract fixes");
        AuditHas(world.PublicationId, $"entity_id = '{world.PublicationId}'")
            .ShouldBeTrue("the audit row is scoped to the publication id");

        var data = audit[0];
        using (var auditJson = JsonDocument.Parse(data))
        {
            var root = auditJson.RootElement;

            // Ids and statuses only: an extra member would be a second copy of user text in an append-only
            // table, so the set is exact.
            MemberNames(root).ShouldBe(
                ["publicationId", "episodeId", "previousStatus", "actor"],
                ignoreOrder: true,
                customMessage: "the audit data carries the ids, the previous status and the actor only");

            root.GetProperty("publicationId").GetGuid().ShouldBe(world.PublicationId);
            root.GetProperty("episodeId").GetGuid().ShouldBe(world.EpisodeId);
            root.GetProperty("previousStatus").GetString().ShouldBe("scheduled");
            root.GetProperty("actor").GetString().ShouldBe("user");
        }

        data.ShouldNotContain(TitleCanary, Case.Sensitive);
        data.ShouldNotContain(DescriptionCanary, Case.Sensitive);
        data.ShouldNotContain(ChannelCanary, Case.Sensitive);

        // The episode is untouched: a cancel is not an episode transition (D12 keeps the two apart).
        EpisodeStatusOf(world.EpisodeId).ShouldBe("ready");
    }

    [Fact]
    public async Task QuotaRelease_RestoresUnitsUsed()
    {
        // The writer of the key is 06.2.3-T2: it stamps the Pacific quota day it reserved on and removes the
        // key when it releases. The test seeds the key itself, which is the read side the cancel depends on.
        var reserved = await SeedAsync(
            status: PublicationStatus.Scheduled,
            platformPayloadJson: QuotaPayload(QuotaDay));
        var withoutKey = await SeedAsync(
            status: PublicationStatus.Scheduled,
            platformPayloadJson: PayloadWithoutQuotaKey);
        var malformed = await SeedAsync(
            status: PublicationStatus.Scheduled,
            platformPayloadJson: """{"quotaReservedDay":"not-a-date"}""");
        var missingRow = await SeedAsync(
            status: PublicationStatus.Scheduled,
            platformPayloadJson: QuotaPayload(MissingRowDay));

        await SetFlagAsync(true);
        await SeedQuotaRowAsync(QuotaDay, unitsUsed: 1, unitsLimit: 6);
        await SeedQuotaRowAsync(OtherDay, unitsUsed: 2, unitsLimit: 6);
        await SeedQuotaRowAsync(MalformedDay, unitsUsed: 1, unitsLimit: 6);

        using var factory = NewFactory();

        // (1) The reserved unit comes back on the row the payload names: units_used 1 -> 0.
        var reservedTag = CurrentTag(reserved.PublicationId);
        using (var client = factory.ClientFor(reserved.UserId))
        using (var response = await DeleteAsync(client, reserved.PublicationId, reservedTag))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

            PublicationStatusOf(reserved.PublicationId).ShouldBe("cancelled");
            UnitsUsed(QuotaDay).ShouldBe(0, "the cancel releases the one unit this publication reserved");
            UnitsUsed(QuotaDay).ShouldBeGreaterThanOrEqualTo(0, "the ledger can never count a negative unit");
            UnitsUsed(QuotaDay).ShouldBeLessThanOrEqualTo(6, "the release never passes units_limit");

            var afterFirst = QuotaRow(QuotaDay);

            // A second delete with the same (now stale) tag is answered by the status, so the ledger is not
            // touched a second time and cannot go below zero.
            using (var repeat = await DeleteAsync(client, reserved.PublicationId, reservedTag))
            {
                repeat.StatusCode.ShouldBe(HttpStatusCode.NoContent, await repeat.Content.ReadAsStringAsync(Ct));
            }

            UnitsUsed(QuotaDay).ShouldBe(0, "a second release must not empty the ledger further");
            QuotaRow(QuotaDay).ShouldBe(afterFirst, "the repeat leaves the ledger row byte for byte unchanged");
        }

        // (2) The key is absent: nothing to release, and the ledger row of the day is identical before/after.
        var otherDayBefore = QuotaRow(OtherDay);
        using (var client = factory.ClientFor(withoutKey.UserId))
        using (var response = await DeleteAsync(client, withoutKey.PublicationId, CurrentTag(withoutKey.PublicationId)))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

            PublicationStatusOf(withoutKey.PublicationId).ShouldBe("cancelled");
            QuotaRow(OtherDay).ShouldBe(otherDayBefore, "without the key there is nothing to release");
        }

        // (3) A malformed key is nothing to release either: the cancel succeeds and the ledger stays as it was.
        var malformedDayBefore = QuotaRow(MalformedDay);
        using (var client = factory.ClientFor(malformed.UserId))
        using (var response = await DeleteAsync(client, malformed.PublicationId, CurrentTag(malformed.PublicationId)))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
            QuotaRow(MalformedDay).ShouldBe(malformedDayBefore, "an unparseable key never fails a cancel");
        }

        // (4) The key names a day without a ledger row: the release is a no-op, and no row is ever inserted for
        // a day the cancel did not reserve on.
        using (var client = factory.ClientFor(missingRow.UserId))
        using (var response = await DeleteAsync(
            client,
            missingRow.PublicationId,
            CurrentTag(missingRow.PublicationId)))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
            QuotaRowCount(MissingRowDay).ShouldBe(0, "a cancel never inserts a ledger row");
        }
    }

    [Fact]
    public async Task NoEmailAndNoGoogleCall()
    {
        var world = await SeedAsync(status: PublicationStatus.Scheduled);
        await SetFlagAsync(true);
        await using var google = await FakeGoogleServer.StartAsync(cancellationToken: Ct);
        using var factory = NewFactory(google: google);
        using var client = factory.ClientFor(world.UserId);

        using var response = await DeleteAsync(client, world.PublicationId, CurrentTag(world.PublicationId));
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        // Every Google endpoint of this host points at the fake (FakeGoogleHosting.SettingsFor), so an empty
        // call log is the "the command issues no Google call" acceptance check.
        google.Calls.ShouldBeEmpty("a cancel never talks to Google: nothing was uploaded, nothing is deleted");

        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE user_id = '{world.UserId}'")
            .ShouldBe(0, "a cancel sends no e-mail");
    }

    [Fact]
    public async Task JobAfterCancel_DoesNotUpload()
    {
        // The row is cancelled while it is still uploadable (consent recorded), so the refusal below comes from
        // the state and not from the missing consent.
        var world = await SeedAsync(status: PublicationStatus.Scheduled, consentAt: Start);
        await SetFlagAsync(true);
        await using var google = await FakeGoogleServer.StartAsync(cancellationToken: Ct);
        using var factory = NewFactory(google: google);
        using var client = factory.ClientFor(world.UserId);

        using (var response = await DeleteAsync(client, world.PublicationId, CurrentTag(world.PublicationId)))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
        }

        // The job's own first step (06.2.3-T2): a fresh context, the publication row locked through the same
        // IAppDbContext.LockPublicationForUpdateAsync the command takes, then Publication.StartUpload. It must
        // refuse, because the row — not the job schedule — is the source of truth (task §Design, "Why not a
        // status = cancelled special case").
        await using (var db = Database.CreateAppDbContext(
            new TestCurrentUser { Id = world.UserId },
            new FakeClock(Start)))
        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            await db.LockPublicationForUpdateAsync(world.PublicationId, Ct);

            var row = await db.Publications.SingleAsync(item => item.Id == world.PublicationId, Ct);

            row.Status.ShouldBe(PublicationStatus.Cancelled);
            row.ConsentAt.ShouldBe(Start, "the consent is still recorded, so ConsentRequired is not the reason");

            var started = row.StartUpload(Start);

            started.IsError.ShouldBeTrue("a cancelled publication never starts an upload");
            started.FirstError.Code.ShouldBe("publication_invalid_state");
            row.Status.ShouldBe(PublicationStatus.Cancelled, "the refused start leaves the row cancelled");
            row.ExternalPostId.ShouldBeNull("no upload means no external post id");
        }

        PublicationStatusOf(world.PublicationId).ShouldBe("cancelled");
        PublicationHas(world.PublicationId, "external_post_id IS NULL")
            .ShouldBeTrue("the row carries no video id after the cancel");

        // The upload job never reached a session create on this host's Google fake.
        google.SessionCreateCalls.ShouldBe(0, "no session create may follow a cancelled row");
        google.Calls.ShouldBeEmpty("no Google call at all for a cancelled row");

        // Positive control: the same counter increments when a create really happens, so the zero above is
        // evidence and not a counter that never counts.
        await using (var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct))
        {
            var created = await harness.StartUploadAsync(1024, cancellationToken: Ct);
            created.IsError.ShouldBeFalse($"a session create must succeed: {created.FirstError}");
            harness.Google.SessionCreateCalls.ShouldBe(1);
        }
    }

    [Fact]
    public async Task NoTitleOrDescriptionInTheLogs()
    {
        var world = await SeedAsync(
            status: PublicationStatus.Scheduled,
            title: TitleCanary,
            description: DescriptionCanary,
            displayName: ChannelCanary);
        await SetFlagAsync(true);
        var sink = new TestLogSink();
        using var factory = NewFactory(sink: sink);
        using var client = factory.ClientFor(world.UserId);

        using var response = await DeleteAsync(client, world.PublicationId, CurrentTag(world.PublicationId));
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        var log = sink.AllText();

        log.ShouldNotBeNullOrWhiteSpace("the sink must have seen this host's own lines, or the search proves nothing");
        log.ShouldNotContain(TitleCanary, Case.Sensitive);
        log.ShouldNotContain(DescriptionCanary, Case.Sensitive);
        log.ShouldNotContain(ChannelCanary, Case.Sensitive);
    }

    /// <summary>
    /// One owner with a series, an episode, an active connection and a publication — the world the cancel route
    /// transitions.
    /// </summary>
    /// <param name="status">The publication's lifecycle state; <c>awaiting_consent</c> and <c>scheduled</c> cancel.</param>
    /// <param name="episodeStatus">The episode's state; <c>ready</c> is the one the consent path reaches.</param>
    /// <param name="title">The publication's title, or the builder's default.</param>
    /// <param name="description">The publication's description, or the builder's default.</param>
    /// <param name="displayName">The connection's channel title, or a fresh one per call.</param>
    /// <param name="platformPayloadJson">The <c>platform_payload</c> document, for the quota-key cases.</param>
    /// <param name="consentAt">The consent to record, for a row that must be uploadable before it is cancelled.</param>
    private async Task<World> SeedAsync(
        PublicationStatus status = PublicationStatus.AwaitingConsent,
        EpisodeStatus episodeStatus = EpisodeStatus.Ready,
        string? title = null,
        string? description = null,
        string? displayName = null,
        string? platformPayloadJson = null,
        DateTimeOffset? consentAt = null)
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
            status: episodeStatus,
            createdAt: Start,
            ct: Ct);

        // `ux_channel_connections_account` and the publication's FK need one real connection per owner.
        var display = displayName ?? $"NTS Test Channel {Guid.CreateVersion7():N}";
        var connection = await new ChannelConnectionBuilder(Database, actor, clock).CreateAsync(
            userId,
            ChannelConnectionStatus.Active,
            display,
            ct: Ct);

        var publicationId = await new PublicationBuilder(Database).CreateAsync(
            userId,
            episode.Id,
            connection.Id,
            status: status,
            title: title,
            description: description,
            platformPayloadJson: platformPayloadJson,
            createdAt: Start,
            ct: Ct);

        if (consentAt is { } consent)
        {
            await RecordConsentAsync(publicationId, consent);
        }

        return new World(userId, episode.Id, publicationId, display);
    }

    /// <summary>
    /// Records the consent evidence of a <c>scheduled</c> row (06.2.2-T1 writes this in production), so
    /// <c>StartUpload</c> can be refused for the state alone rather than for a missing consent.
    /// </summary>
    private async Task RecordConsentAsync(Guid publicationId, DateTimeOffset consentAt)
    {
        var stamp = Instant(consentAt);

        await Database.ExecuteAsync(
            "UPDATE publications SET consent_at = " + stamp + ", consent_text_version = 'pc.v1', "
            + $"consent_source = 'user', updated_at = {stamp} WHERE id = '{publicationId}'",
            Ct);
    }

    /// <summary>Writes the one flag row these routes read; a missing row is the 404 case.</summary>
    private async Task SetFlagAsync(bool enabled)
    {
        await Database.ExecuteAsync($"DELETE FROM feature_flags WHERE key = '{FeatureKey}'", Ct);

        if (enabled)
        {
            await Database.ExecuteAsync(
                "INSERT INTO feature_flags (key, enabled, rules, created_at, updated_at) "
                + $"VALUES ('{FeatureKey}', true, '{{}}', now(), now())",
                Ct);
        }
    }

    /// <summary>
    /// The host. The flag row is already in place before the first <c>Services</c> read, because the flag cache
    /// is built when the host starts; a test that asserts "no Google call" or the upload guard passes the fake
    /// every endpoint of the host points at.
    /// </summary>
    private ApiFactory NewFactory(FakeGoogleServer? google = null, TestLogSink? sink = null)
    {
        var factory = new ApiFactory(Database, new FakeClock(Start));

        if (google is not null)
        {
            factory = factory.WithFakeGoogle(google);
        }

        return sink is null
            ? factory
            : factory.WithServices(services => services.AddSingleton<Serilog.Core.ILogEventSink>(sink));
    }

    /// <summary><c>DELETE /api/v1/publications/{id}</c> with the given <c>If-Match</c>.</summary>
    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid publicationId, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{PublicationsRoute}/{publicationId}");

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>Asserts the status and the problem body, and returns the body; a non-problem body fails here.</summary>
    private static async Task<ProblemBody> ReadProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);
        problem.ShouldNotBeNull();
        problem.Status.ShouldBe((int)expected);

        return problem;
    }

    /// <summary>The row was not written: its status is unchanged, and neither an event nor an audit row exists.</summary>
    private void AssertUntouched(World world, string expectedStatus, string reason)
    {
        PublicationStatusOf(world.PublicationId).ShouldBe(expectedStatus, reason);
        OutboxPayloads(world.PublicationId).ShouldBeEmpty(reason);
        AuditData(world.PublicationId).ShouldBeEmpty(reason);
    }

    /// <summary>The D13 payload's members: the ids and the state, plus the event record's own two.</summary>
    private static readonly string[] OutboxPayloadMembers = new[]
    {
        "publicationId", "episodeId", "userId", "status", "errorCode", "occurredAt",
    };

    private static IReadOnlyList<string> MemberNames(JsonElement element) =>
        [.. element.EnumerateObject().Select(property => property.Name)];

    /// <summary>The row's current strong ETag, read from <c>xmin</c> like the client's own copy.</summary>
    private string CurrentTag(Guid publicationId) => ETags.Publication(Xmin(publicationId));

    /// <summary>The <c>xmin</c> of the row, as <c>ETags.Publication</c> spells it.</summary>
    private uint Xmin(Guid publicationId) =>
        uint.Parse(
            SqlProbe.ScalarText(Database, $"SELECT xmin::text FROM publications WHERE id = '{publicationId}'"),
            CultureInfo.InvariantCulture);

    /// <summary>Whether the one row satisfies <paramref name="predicate"/> — every column check goes through here.</summary>
    private bool PublicationHas(Guid publicationId, string predicate) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM publications WHERE id = '{publicationId}' AND ({predicate})") == 1;

    private string PublicationStatusOf(Guid publicationId) =>
        SqlProbe.ScalarText(Database, $"SELECT status FROM publications WHERE id = '{publicationId}'");

    private string EpisodeStatusOf(Guid episodeId) =>
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{episodeId}'");

    /// <summary>Whether this cancel's audit row satisfies <paramref name="predicate"/>.</summary>
    private bool AuditHas(Guid publicationId, string predicate) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM audit_logs WHERE action = '{AuditAction}' "
            + $"AND entity_id = '{publicationId}' AND ({predicate})") == 1;

    /// <summary>The <c>data</c> of this command's audit rows for <paramref name="publicationId"/> (D17).</summary>
    private List<string> AuditData(Guid publicationId) =>
        SqlProbe.QueryStrings(
            Database,
            $"SELECT data::text FROM audit_logs WHERE action = '{AuditAction}' "
            + $"AND entity_id = '{publicationId}'");

    /// <summary>The payloads of the D13 row this cancel raised for <paramref name="publicationId"/>.</summary>
    private List<string> OutboxPayloads(Guid publicationId) =>
        SqlProbe.QueryStrings(
            Database,
            $"SELECT payload::text FROM outbox_messages WHERE type = '{OutboxEventType}' "
            + $"AND payload->>'publicationId' = '{publicationId}'");

    /// <summary>Seeds one <c>platform_quota_usage</c> row for the given day, replacing any earlier one.</summary>
    private async Task SeedQuotaRowAsync(string day, int unitsUsed, int unitsLimit)
    {
        await Database.ExecuteAsync(
            $"DELETE FROM platform_quota_usage WHERE platform = 'youtube' AND quota_day = '{day}'",
            Ct);

        await Database.ExecuteAsync(
            "INSERT INTO platform_quota_usage (platform, quota_day, units_used, units_limit, created_at, updated_at) "
            + $"VALUES ('youtube', '{day}', {unitsUsed}, {unitsLimit}, now(), now())",
            Ct);
    }

    /// <summary>The <c>units_used</c> of the day's row.</summary>
    private int UnitsUsed(string day) =>
        int.Parse(
            SqlProbe.ScalarText(
                Database,
                $"SELECT units_used FROM platform_quota_usage WHERE platform = 'youtube' AND quota_day = '{day}'"),
            CultureInfo.InvariantCulture);

    /// <summary>A whole ledger row as one text, so "identical before and after" is one assertion.</summary>
    private string QuotaRow(string day) =>
        SqlProbe.ScalarText(
            Database,
            "SELECT platform || '|' || quota_day::text || '|' || units_used::text || '|' || units_limit::text "
            + $"|| '|' || updated_at::text FROM platform_quota_usage WHERE platform = 'youtube' AND quota_day = '{day}'");

    /// <summary>How many ledger rows the day has; a cancel must never create one.</summary>
    private long QuotaRowCount(string day) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM platform_quota_usage WHERE platform = 'youtube' AND quota_day = '{day}'");

    /// <summary>The <c>platform_payload</c> of the key contract with 06.2.3-T2: a Pacific day as ISO text.</summary>
    private static string QuotaPayload(string day) => $$"""{"quotaReservedDay":"{{day}}"}""";

    /// <summary>An instant a raw <c>WHERE</c> can compare with, in the round-tripping format.</summary>
    private static string Instant(DateTimeOffset value) =>
        $"'{value.ToString("O", CultureInfo.InvariantCulture)}'::timestamptz";

    /// <summary>One seeded owner: the publication, the episode it belongs to and the connection's channel title.</summary>
    private sealed record World(Guid UserId, Guid EpisodeId, Guid PublicationId, string DisplayName);

    private sealed record ProblemBody(string Code, int Status, Dictionary<string, string[]>? Errors);
}
