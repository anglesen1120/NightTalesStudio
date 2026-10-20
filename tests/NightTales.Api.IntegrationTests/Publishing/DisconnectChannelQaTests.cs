using System.Net;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Google;
using Npgsql;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// Independent QA pass over <c>DELETE /api/v1/channels/{id}</c> (06.4.1-T1). It pins what the implementer's
/// <see cref="DisconnectChannelTests"/> and <see cref="DisconnectChannelCascadeTests"/> leave open: a
/// disconnect of an <c>expired</c>/<c>error</c> row, an <c>uploading</c> publication (which the cascade must
/// never touch) next to an <c>awaiting_consent</c> one (which it must cancel), the anonymous and non-GUID
/// answers, an aborted request that may never leave a half-written disconnect, a repeat after the
/// channel-scope request already completed, and the schema consequence of plan.md:69 (a revoked row leaves
/// the account's partial unique index, so the same Google channel can be connected again).
/// </summary>
/// <remarks>
/// <para>
/// Every test reuses <see cref="DisconnectChannelHarness"/> — the seeded row, the fake Google, the real Api
/// and the raw-SQL probes — and seeds nothing it cannot scope to its own ids.
/// </para>
/// <para>
/// The class joins the <c>Process-global host state</c> collection: a host built here publishes on the
/// process-wide <c>NightTales</c> meter, and <see cref="DisconnectChannelTests"/> reads
/// <c>youtube_channel_revoke_failed_total</c> through a process-wide listener, so the two classes must never
/// run concurrently.
/// </para>
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
[Collection("Process-global host state")]
[Trait("Category", "Integration")]
public sealed class DisconnectChannelQaTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContextToken.Value;

    [Theory]
    [InlineData(ChannelConnectionStatus.Expired)]
    [InlineData(ChannelConnectionStatus.Error)]
    public async Task Disconnect_ExpiredOrErrorConnection_IsAcceptedAndRevoked(ChannelConnectionStatus status)
    {
        // The 06.4 plan §3.1 state machine: active/expired/error -> revoked is this route's transition. The
        // implementer's tests only seed `active`, so a handler that refused the other two reads (a wrong
        // status check) would pass them and still break the contract.
        await using var harness = await StartAsync();
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, status: status, ct: Ct);
        var publication = await harness.SeedPublicationAsync(userId, channel.Id, ct: Ct);

        using var client = harness.ClientFor(userId);
        using var response = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, text);

        harness.ChannelHas(
                channel.Id,
                "status = 'revoked' AND access_token_enc IS NULL AND refresh_token_enc IS NULL")
            .ShouldBeTrue($"{status} is a disconnectable state and ends in the same wiped row");
        harness.ChannelHas(channel.Id, $"revoked_at = {DisconnectChannelHarness.Instant(DisconnectChannelHarness.Start)}")
            .ShouldBeTrue("revoked_at is the command's own instant, whatever state the row left");

        harness.Google.RevokeCount.ShouldBe(1, "the expired/error row still held a plaintext to revoke");
        harness.DeletionRequestCount(userId).ShouldBe(1, "the purge is scheduled for every disconnectable state");
        harness.AuditData(channel.Id).ShouldHaveSingleItem();
        harness.ConnectionPayloads(channel.Id).ShouldHaveSingleItem();
        harness.PublicationStatusOf(publication.Id).ShouldBe(
            "cancelled",
            "the cascade is driven by the publication's status, not by the connection's");
    }

    [Fact]
    public async Task Disconnect_LeavesAnUploadingPublicationAlone_AndCancelsTheAwaitingConsentOne()
    {
        // Failure handling "Disconnect while an upload job is streaming": the cascade ignores `uploading`, and
        // "A cancelled publication has no quotaReservedDay key" is the *only* release rule — a row that holds a
        // unit but keeps uploading must keep its unit. The implementer's cascade test covers processing /
        // published / failed / cancelled and two `scheduled` rows; `awaiting_consent` (the state the aggregate
        // factory actually produces) and `uploading` are the two it never seeds.
        await using var harness = await StartAsync();
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);

        var uploading = await harness.SeedPublicationAsync(
            userId,
            channel.Id,
            status: PublicationStatus.Uploading,
            platformPayloadJson: DisconnectChannelHarness.QuotaPayload(DisconnectChannelHarness.QuotaDay),
            ct: Ct);
        var awaitingConsent = await harness.SeedPublicationAsync(
            userId,
            channel.Id,
            status: PublicationStatus.AwaitingConsent,
            platformPayloadJson: DisconnectChannelHarness.QuotaPayload(DisconnectChannelHarness.QuotaDay),
            ct: Ct);

        await harness.SeedQuotaRowAsync(DisconnectChannelHarness.QuotaDay, unitsUsed: 2, unitsLimit: 6, ct: Ct);
        var uploadingXmin = harness.Xmin(uploading.Id);

        using var client = harness.ClientFor(userId);
        using var response = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        harness.PublicationStatusOf(awaitingConsent.Id).ShouldBe(
            "cancelled",
            "an awaiting_consent row is the other state the aggregate's Cancel accepts (D15)");
        harness.PublicationPayloads(awaitingConsent.Id).ShouldHaveSingleItem();

        harness.PublicationStatusOf(uploading.Id).ShouldBe(
            "uploading",
            "a video already on its way to YouTube is never deleted or cancelled by a disconnect");
        harness.Xmin(uploading.Id).ShouldBe(uploadingXmin, "the uploading row is not written at all");
        harness.PublicationPayloads(uploading.Id).ShouldBeEmpty();

        harness.UnitsUsed(DisconnectChannelHarness.QuotaDay).ShouldBe(
            1,
            "only the cancelled row gives its unit back; the uploading row still holds its reservation");

        var audit = harness.AuditData(channel.Id).ShouldHaveSingleItem();
        using var data = System.Text.Json.JsonDocument.Parse(audit);
        data.RootElement.GetProperty("publicationsCancelled").GetInt32().ShouldBe(1);
        data.RootElement.GetProperty("quotaUnitsReleased").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Disconnect_WithoutABearer_Answers401AndWritesNothing()
    {
        // The route is `.RequireAuthorization()`: an anonymous caller must be refused before the flag, the
        // handler and Google. Nothing in the implementer's suite sends an unauthenticated request.
        await using var harness = await StartAsync();
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        var rowBefore = harness.ChannelRow(channel.Id);

        using var client = harness.ClientFor(userId);
        client.DefaultRequestHeaders.Clear();

        using var response = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        harness.ChannelRow(channel.Id).ShouldBe(rowBefore, "an unauthorized call never reaches the handler");
        harness.Google.Calls.ShouldBeEmpty();
        harness.DeletionRequestCount(userId).ShouldBe(0);
        harness.AuditData(channel.Id).ShouldBeEmpty();
    }

    [Fact]
    public async Task Disconnect_NonGuidSegment_Answers404AndWritesNothing()
    {
        // `/{id:guid}` is the route constraint: a segment that is not a GUID matches no route at all, so the
        // caller sees a 404 — never a 400 parse error (the implementer's 404 tests all send a real GUID).
        await using var harness = await StartAsync();
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        var rowBefore = harness.ChannelRow(channel.Id);

        using var client = harness.ClientFor(userId);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"{DisconnectChannelHarness.ChannelsRoute}/not-a-guid");
        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        harness.ChannelRow(channel.Id).ShouldBe(rowBefore);
        harness.Google.Calls.ShouldBeEmpty("a route that never matched never reaches Google");
        harness.DeletionRequestCount(userId).ShouldBe(0);
    }

    [Fact]
    public async Task Disconnect_CancelledMidFlight_NeverLeavesAHalfWrittenDisconnect()
    {
        // The transaction contract (plan §3.3 invariant 2, failure handling "Crash between the Google call and
        // the commit"): the observable worlds are "untouched" and "revoked + request", never a wipe without a
        // request or a request without the wipe. The request is aborted once the first revoke reaches Google, so
        // the handler is inside the retry ladder (250 ms + 1 s of backoff) when the abort lands.
        await using var harness = await StartAsync(
            new FakeGoogleOptions { RevokeStatusCode = HttpStatusCode.InternalServerError });
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        var rowBefore = harness.ChannelRow(channel.Id);

        using var client = harness.ClientFor(userId);
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"{DisconnectChannelHarness.ChannelsRoute}/{channel.Id}");

        var call = client.SendAsync(request, abort.Token);
        await WaitUntilAsync(() => harness.Google.RevokeCount >= 1, Ct);
        await abort.CancelAsync();

        // The client either observes its own abort or — if the host finished first — a 204. Both are legal for
        // this test; what must never happen is a wipe without a request, or a request without the wipe.
        HttpResponseMessage? answered = null;
        try
        {
            answered = await call;
        }
        catch (OperationCanceledException)
        {
            // The abort reached the client, which is the case this test is built to observe.
        }

        // Whatever the host observed, give the aborted pipeline time to finish its rollback (or, when the abort
        // never reached it, the whole ladder: two more attempts inside 1.5 s).
        await Task.Delay(TimeSpan.FromMilliseconds(1500), Ct);

        using var response = answered;

        // The branch is keyed on the state the database actually holds, never on how many attempts the fake saw: a
        // stall between the first attempt and the abort lets the ladder finish and still roll back, because the
        // handler observes the token only at its next database call. The plan's invariant is what is asserted —
        // either nothing happened, or the whole disconnect committed.
        if (harness.ChannelHas(channel.Id, "status = 'revoked'"))
        {
            harness.ChannelHas(
                    channel.Id,
                    "access_token_enc IS NULL AND refresh_token_enc IS NULL")
                .ShouldBeTrue("a completed disconnect is never half written");
            harness.DeletionRequestCount(userId).ShouldBe(1);
            harness.AuditData(channel.Id).ShouldHaveSingleItem();
            harness.ConnectionPayloads(channel.Id).ShouldHaveSingleItem();
        }
        else
        {
            // The abort reached the handler: the ladder stopped, the row was never touched and nothing was
            // written, so the owner can simply retry.
            harness.ChannelRow(channel.Id).ShouldBe(
                rowBefore,
                "an aborted disconnect rolls the wipe back; the row keeps its tokens and stays active");
            harness.AuditData(channel.Id).ShouldBeEmpty("no audit row for a rolled-back command");
            harness.ConnectionPayloads(channel.Id).ShouldBeEmpty("no outbox row for a rolled-back command");
            harness.DeletionRequestCount(userId).ShouldBe(0, "no purge is scheduled for a rolled-back command");
        }

        // Either way the row is still disconnectable: a real retry succeeds and ends in the documented state.
        using var retry = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct);
        retry.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            "a disconnect the client aborted leaves no half state behind: the retry answers 204 ("
            + await retry.Content.ReadAsStringAsync(Ct) + ')');
        harness.ChannelHas(channel.Id, "status = 'revoked' AND access_token_enc IS NULL").ShouldBeTrue();
        harness.DeletionRequestCount(userId).ShouldBe(1);
    }

    [Fact]
    public async Task Disconnect_AfterTheRequestCompleted_WritesNothingAtAll()
    {
        // Failure handling "Connection already revoked and its request already completed_at" says *no second
        // request*. The engine's partial unique index covers open rows only (ux_deletion_requests_open ...
        // WHERE completed_at IS NULL), so re-entering IDeletionRequests.CreateAsync here would insert a fresh
        // pending row: a second clock over data the purge already removed. The repeat therefore asks the engine
        // whether the duty is on record at all (IDeletionRequests.ExistsAsync) and, when it is, writes nothing —
        // the row locks, the completed request untouched, no Google call, no audit and no outbox row.
        await using var harness = await StartAsync();
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, status: ChannelConnectionStatus.Revoked, ct: Ct);

        await Database.ExecuteAsync(
            "INSERT INTO data_deletion_requests "
            + "(id, user_id, scope, target_id, reason, status, requested_at, due_at, completed_at) VALUES ("
            + $"'{Guid.CreateVersion7()}', '{userId}', 'channel', '{channel.Id}', 'in_app_revoke', 'completed', "
            + $"{DisconnectChannelHarness.Instant(DisconnectChannelHarness.Start.AddDays(-30))}, "
            + $"{DisconnectChannelHarness.Instant(DisconnectChannelHarness.Start.AddDays(-23))}, "
            + $"{DisconnectChannelHarness.Instant(DisconnectChannelHarness.Start.AddDays(-23))})",
            Ct);

        using var client = harness.ClientFor(userId);
        using var response = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        harness.DeletionRequests(userId, channel.Id).Count.ShouldBe(
            1,
            "the repeat opens no second request: the duty is already on record, completed");

        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM data_deletion_requests WHERE user_id = '{userId}' "
                + $"AND target_id = '{channel.Id}' AND status = 'completed' AND completed_at IS NOT NULL "
                + $"AND requested_at = {DisconnectChannelHarness.Instant(DisconnectChannelHarness.Start.AddDays(-30))} "
                + $"AND due_at = {DisconnectChannelHarness.Instant(DisconnectChannelHarness.Start.AddDays(-23))} "
                + $"AND completed_at = {DisconnectChannelHarness.Instant(DisconnectChannelHarness.Start.AddDays(-23))}")
            .ShouldBe(
                1,
                "the completed request stays exactly as it was: the engine never reopens, moves or re-creates it");

        harness.DeletionHas(
                userId,
                channel.Id,
                "status = 'completed' AND scope = 'channel' AND reason = 'in_app_revoke'")
            .ShouldBeTrue("the one row is still the channel purge's completed duty, not a fresh pending clock");

        harness.Google.Calls.ShouldBeEmpty("a revoked row holds no ciphertext, so Google is never called");
        harness.AuditData(channel.Id).ShouldBeEmpty("the repeat changed nothing, so it writes no audit row");
        harness.ConnectionPayloads(channel.Id).ShouldBeEmpty("the repeat raises no second state-change event");
    }

    [Fact]
    public async Task Disconnect_RepeatOfARevokedConnectionWithoutAnyRequest_SchedulesTheMissingOne()
    {
        // The other side of the same repeat (Design step 2): the first attempt's response may have been lost with
        // its rolled-back transaction, so a revoked row with no duty on record still reaches the engine's idempotent
        // create. Exactly one request is written, carrying the engine's own 7-day deadline.
        await using var harness = await StartAsync();
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, status: ChannelConnectionStatus.Revoked, ct: Ct);

        harness.DeletionRequestCount(userId).ShouldBe(0, "the case under test starts with no duty on record");

        using var client = harness.ClientFor(userId);
        using var response = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        harness.DeletionRequests(userId, channel.Id).Count.ShouldBe(
            1,
            "a lost response leaves no half state: the repeat schedules the one duty the channel purge needs");

        harness.DeletionHas(
                userId,
                channel.Id,
                "status = 'pending' AND completed_at IS NULL "
                + $"AND due_at = {DisconnectChannelHarness.Instant(DisconnectChannelHarness.Start.AddDays(7))}")
            .ShouldBeTrue("the created request carries the engine's own 7-day deadline");

        harness.Google.Calls.ShouldBeEmpty("a revoked row holds no ciphertext, so Google is never called");
        harness.AuditData(channel.Id).ShouldBeEmpty("the repeat changed no state, so it writes no audit row");
        harness.ConnectionPayloads(channel.Id).ShouldBeEmpty("the repeat raises no state-change event");
    }

    [Fact]
    public async Task Disconnect_LeavesTheAccountIndex_SoTheSameChannelCanBeConnectedAgain()
    {
        // plan.md:69 (the doc fix this task carries): a reconnect after a disconnect inserts a *new* active row
        // for the same (platform, external_account_id); the revoked row is never revived. The schema is what
        // admits that — `ux_channel_connections_account` is filtered on status <> 'revoked' — so this test
        // proves the index releases the account on the disconnect and holds it before.
        await using var harness = await StartAsync();
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);

        var duplicate = (await Should.ThrowAsync<Exception>(
            () => InsertLiveConnectionAsync(userId, channel.ExternalAccountId, Ct)))
            .ShouldBeAssignableTo<PostgresException>();
        duplicate.SqlState.ShouldBe(
            PostgresErrorCodes.UniqueViolation,
            "while the row is live the account slot is taken: one live connection per Google channel");

        using var client = harness.ClientFor(userId);
        using var response = await DisconnectChannelHarness.DeleteAsync(client, channel.Id, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));

        await InsertLiveConnectionAsync(userId, channel.ExternalAccountId, Ct);

        harness.ChannelHas(channel.Id, "status = 'revoked'").ShouldBeTrue(
            "the revoked row stays as history: the connect path inserts beside it instead of reviving it");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM channel_connections WHERE external_account_id = '{channel.ExternalAccountId}' "
                + "AND status <> 'revoked'")
            .ShouldBe(1, "exactly the new row is live for that account");
    }

    /// <summary>
    /// Inserts one live (<c>active</c>) connection for <paramref name="externalAccountId"/>, i.e. the row the
    /// connect path would insert; the caller's own assertions decide whether the unique index admits it.
    /// </summary>
    /// <param name="userId">The owner of the new row.</param>
    /// <param name="externalAccountId">Google's opaque channel id the account index is keyed on.</param>
    /// <param name="ct">Cancels the write.</param>
    private async Task InsertLiveConnectionAsync(Guid userId, string externalAccountId, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        var stamp = DisconnectChannelHarness.Instant(DisconnectChannelHarness.Start);

        await Database.ExecuteAsync(
            "INSERT INTO channel_connections "
            + "(id, user_id, platform, external_account_id, display_name, status, access_token_enc, "
            + "refresh_token_enc, token_expires_at, scope, oauth_client_id, last_verified_at, connected_at, revoked_at) "
            + $"VALUES ('{id}', '{userId}', 'youtube', '{externalAccountId}', 'QA reconnect', 'active', NULL, NULL, "
            + $"{stamp}, 'https://www.googleapis.com/auth/youtube.upload', "
            + "'nts-qa-client.apps.googleusercontent.com', NULL, "
            + $"{DisconnectChannelHarness.Instant(DisconnectChannelHarness.Start)}, NULL)",
            ct);
    }

    /// <summary>Waits until <paramref name="condition"/> holds, or fails the test after ten seconds.</summary>
    /// <param name="condition">The condition to observe.</param>
    /// <param name="ct">Cancels the wait.</param>
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
        }

        throw new TimeoutException(
            "The fake Google server never recorded the first revoke attempt within ten seconds.");
    }

    /// <summary>Starts the harness, creating the flag row before the host exists.</summary>
    /// <param name="googleOptions">The fake's revoke answer.</param>
    private Task<DisconnectChannelHarness> StartAsync(FakeGoogleOptions? googleOptions = null) =>
        DisconnectChannelHarness.StartAsync(Database, Ct, flagOn: true, googleOptions);
}
