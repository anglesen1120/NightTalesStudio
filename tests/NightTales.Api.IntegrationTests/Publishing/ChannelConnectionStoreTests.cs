using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ErrorOr;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Auth;
using NightTales.Api.IntegrationTests.Email;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Abstractions;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Publishing.Channels;
using NightTales.Application.Features.Publishing.Channels.Queries.ListChannels;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using NightTales.TestKit.Google;
using NightTales.TestKit.Telemetry;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// 06.1.2-T2 over the real host and a real PostgreSQL 17: the <c>channel_connections</c> store the connect
/// callback writes through the <em>production</em> <c>IConnectChannelConnection</c> (<see
/// cref="ConnectChannelHarness.ConnectChannelWriter.Production"/>), the per-connection protection purpose,
/// the reconnect/one-owner rules, the single-statement channel list and the connect e-mail.
/// </summary>
/// <remarks>
/// <para>
/// Every test drives <see cref="ConnectChannelHarness"/> with the production writer (the harness's T1
/// stand-in stays for the callback suite that needs a forced writer outcome) plus <see
/// cref="FakeGoogleServer"/>, and reads the rows with <see cref="SqlProbe"/>: the class database is this
/// class's own, and <see cref="ResetAsync"/> clears the three tables a test writes before it starts, because
/// the rows of 06.1.2-T2 outlive one test (unlike T1's, which the stand-in never wrote).
/// </para>
/// <para>
/// The channel id is chosen per test (<see cref="FakeGoogleOptions.Channel"/>) so the partial unique index
/// <c>ux_channel_connections_account</c> can never collide across tests of the shared database.
/// </para>
/// <para>
/// The statement-count check of the list runs <see cref="ListChannelsHandler"/> directly over a
/// <see cref="ReadDbContext"/> carrying <see cref="CountingDbCommandInterceptor"/> — the same seam
/// <c>RecentMusicQueryTests</c> uses — because the query's own command count is the property under test, and
/// the HTTP route around it is covered by <see cref="ListChannels_ReturnsCallerOnlyWithoutTokenColumns"/>.
/// </para>
/// </remarks>
/// <param name="database">The class database, a migrated copy of the run's template.</param>
[Collection("Hangfire server")]
public sealed class ChannelConnectionStoreTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The channel title every connect stores and every redaction check searches for.</summary>
    private const string ChannelTitle = "NightTales Store Canary";

    /// <summary>The static account-security path the connect e-mail carries (the task's <c>securityLink</c>).</summary>
    private const string SecurityLink = "/account/security";

    /// <summary>The route the flag-off case reads (06.1.2-T2 <c>GET /channels</c>).</summary>
    private const string ChannelsRoute = "/api/v1/channels";

    /// <summary>The legal acceptance route the second account must pass, as the harness runs it for the first.</summary>
    private const string LegalAcceptRoute = "/api/v1/me/legal/accept";

    /// <summary>
    /// The <c>User-Agent</c> the e-mail's <c>device</c> value is derived from: the handler keeps the first
    /// parenthesised platform clause, which is exactly what <see cref="DeviceClause"/> pins.
    /// </summary>
    private const string UserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36";

    /// <summary>The clause of <see cref="UserAgent"/> a connect e-mail must report.</summary>
    private const string DeviceClause = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)";

    /// <summary>The two-URI scope string the fake's token response echoes, and the row must store.</summary>
    private static readonly string GrantedScope =
        $"{FakeGoogleHosting.UploadScope} {FakeGoogleHosting.ReadonlyScope}";

    /// <summary>The two pairs one serialised reconnect pair may have committed, for the reader's assertion.</summary>
    private static readonly string[] CommittedPairs = ["access-a", "access-b"];

    /// <summary>The five substitution keys the <c>channel_connected</c> catalog entry accepts, sorted.</summary>
    private static readonly string[] EmailParameterKeys =
        ["channelTitle", "device", "displayName", "occurredAt", "securityLink"];

    /// <summary>The seven members one <c>GET /channels</c> item carries — and no token or client id (D17).</summary>
    private static readonly string[] ChannelListMembers =
        ["connectedAt", "displayName", "externalAccountId", "id", "lastVerifiedAt", "platform", "status"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------------------
    // The callback's own write: one active row, one protected pair, one queued message (checks 1, 2, 8, 13).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Callback_InsertsActiveRowWithEncryptedTokens()
    {
        const string channelId = "canary-store-callback-channel";
        var options = new FakeGoogleOptions { Channel = new GoogleChannel(channelId, ChannelTitle) };
        await using var harness = await StartAsync(Database, Ct, options);
        await ResetAsync(Ct);

        var location = await NavigateAsync(harness, await PrepareAsync(harness, harness.Client, Ct), Ct);

        location.ShouldBe("/channels?connected=youtube", "a valid callback connects the channel");

        var userId = harness.User.Id;
        SqlProbe.Count(Database, $"SELECT count(*) FROM channel_connections WHERE user_id = '{userId}'")
            .ShouldBe(1, "the production writer leaves exactly one row");

        var where = $"external_account_id = '{channelId}'";
        ConnectionValue("user_id", where).ShouldBe(userId.ToString());
        ConnectionValue("status", where).ShouldBe("active");
        ConnectionValue("external_account_id", where).ShouldBe(channelId);
        ConnectionValue("display_name", where).ShouldBe(ChannelTitle, "the title channels.list answered");
        ConnectionValue("scope", where).ShouldBe(GrantedScope, "the scope of the token response, verbatim");
        ConnectionValue("oauth_client_id", where).ShouldBe(
            options.ActiveClientId,
            "a first connect pins the running deployment's client");
        ConnectionValue("connected_at", where).ShouldNotBeNullOrEmpty();
        ConnectionValue("last_verified_at", where).ShouldNotBeNullOrEmpty();
        ConnectionValue("revoked_at", where).ShouldBeNullOrEmpty("a live row has no disconnect instant");
        ConnectionEpoch("token_expires_at", where).ShouldBe(
            harness.Clock.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            "the response's expires_in is what the row stores (the fake answers 3600 s)");

        // Check 1's identity columns being the fake's own values is what makes the encryption check below
        // evidence about this callback rather than about a row a test seeded.
        var connectionId = Guid.Parse(ConnectionValue("id", where));
        var accessToken = harness.Google.IssuedAccessTokens.Single();

        var protector = harness.Protector();
        protector.TryUnprotect(connectionId.ToString(), ConnectionValue("access_token_enc", where), out var access)
            .ShouldBeTrue("both tokens are protected under tokens.v1/{connectionId} (D4)");
        string.Equals(access, accessToken, StringComparison.Ordinal)
            .ShouldBeTrue("the stored access token is the one the token endpoint issued");

        protector.TryUnprotect(connectionId.ToString(), ConnectionValue("refresh_token_enc", where), out var refresh)
            .ShouldBeTrue();
        string.Equals(refresh, ExpectedRefreshToken(accessToken), StringComparison.Ordinal)
            .ShouldBeTrue("the refresh token of the same response is stored under the same purpose");

        // No plaintext token exists in any column of the row (check 2's first half).
        var row = RowText("channel_connections", where);
        row.ShouldNotContain(accessToken);
        row.ShouldNotContain(ExpectedRefreshToken(accessToken));
        harness.AuditData().ShouldNotContain(accessToken, Case.Sensitive, "the audit row carries ids only (D17)");
    }

    [Fact]
    public async Task Tokens_DoNotDecryptUnderAnotherPurpose()
    {
        var options = new FakeGoogleOptions();
        await using var harness = await StartAsync(Database, Ct, options);
        await ResetAsync(Ct);

        var now = harness.Clock.UtcNow;
        var first = await ConnectAsync(harness, harness.User.Id, "canary-purpose-one", "access-one", "refresh-one", "client-one", now, Ct);
        var second = await ConnectAsync(harness, harness.User.Id, "canary-purpose-two", "access-two", "refresh-two", "client-two", now, Ct);

        first.ShouldNotBe(second, "two channels of one account are two rows");

        var firstAccess = ConnectionValue("access_token_enc", $"id = '{first}'");
        var secondRefresh = ConnectionValue("refresh_token_enc", $"id = '{second}'");
        var protector = harness.Protector();

        protector.TryUnprotect(first.ToString(), firstAccess, out var plain).ShouldBeTrue();
        plain.ShouldBe("access-one");

        protector.TryUnprotect(second.ToString(), firstAccess, out _)
            .ShouldBeFalse("another connection's purpose must not read this cipher");
        protector.TryUnprotect("oauth.v1", firstAccess, out _).ShouldBeFalse();
        protector.TryUnprotect("tokens.v1", firstAccess, out _)
            .ShouldBeFalse("the suffix is the connection id, not the purpose root");
        protector.TryUnprotect(first.ToString(), secondRefresh, out _)
            .ShouldBeFalse("the second row's cipher belongs to the second id only");

        // The ciphertext is not the plaintext, and the two rows of one account do not share a purpose.
        firstAccess.ShouldNotBe("access-one");
        firstAccess.ShouldNotBe(ConnectionValue("access_token_enc", $"id = '{second}'"));
    }

    // ---------------------------------------------------------------------------------------------------
    // Reconnect: one row, re-pinned client, the lock before the tokens (checks 3, 4).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reconnect_SameUserSameChannel_UpdatesOneRow_RepinsClientId()
    {
        const string channelId = "canary-store-reconnect-channel";
        var options = new FakeGoogleOptions { Channel = new GoogleChannel(channelId, ChannelTitle) };
        await using var harness = await StartAsync(Database, Ct, options);
        await ResetAsync(Ct);

        var first = await PrepareAsync(harness, harness.Client, Ct);
        (await NavigateAsync(harness, first, Ct)).ShouldBe("/channels?connected=youtube");

        var where = $"external_account_id = '{channelId}'";
        var connectionId = Guid.Parse(ConnectionValue("id", where));
        var firstAccess = ConnectionValue("access_token_enc", where);
        var firstAccessToken = harness.Google.IssuedAccessTokens.Single();

        // The reconnect happens five minutes later, so "connected_at unchanged" is evidence and not two
        // identical clock readings.
        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        // The deployment that minted the state had the second client active and the operator has since moved
        // on: the state row is the authority for the exchange (D5/YR11), and the IOptions snapshot the host
        // booted with cannot be rebound mid-test, so the rotation is expressed where the task file says it
        // matters — the client the second state pins and the URL it authorizes with.
        var second = await PrepareAsync(harness, harness.Client, Ct, options.SecondClientId);
        await Database.ExecuteAsync(
            $"UPDATE oauth_states SET oauth_client_id = '{options.SecondClientId}' "
            + $"WHERE state_hash = '{AuthTestFlow.Hash(second.State)}'",
            Ct);

        (await NavigateAsync(harness, second, Ct)).ShouldBe("/channels?connected=youtube");

        SqlProbe.Count(Database, $"SELECT count(*) FROM channel_connections WHERE user_id = '{harness.User.Id}'")
            .ShouldBe(1, "a second connect of the same channel updates the one row, never inserts");
        ConnectionValue("id", where).ShouldBe(connectionId.ToString(), "the connection id never changes (YR11)");
        ConnectionEpoch("connected_at", where).ShouldBe(
            harness.Clock.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(),
            "a reconnect keeps the first connect instant");
        ConnectionValue("oauth_client_id", where).ShouldBe(
            options.SecondClientId,
            "the row is re-pinned to the client of the state that minted the new code (YR11)");
        ConnectionValue("oauth_client_id", where).ShouldNotBe(
            options.ActiveClientId,
            "the re-pin is a change, which is why the member is settable and not init-only");
        ConnectionValue("status", where).ShouldBe("active");
        ConnectionValue("revoked_at", where).ShouldBeNullOrEmpty();

        // The state of the second connect is what the row now matches.
        harness.StateValueFor("oauth_client_id", second.State)
            .ShouldBe(ConnectionValue("oauth_client_id", where));

        // The pair was replaced and re-protected under the same id: the first ciphertext is gone.
        var secondAccess = ConnectionValue("access_token_enc", where);
        secondAccess.ShouldNotBe(firstAccess, "a reconnect overwrites the token pair");
        var protector = harness.Protector();
        protector.TryUnprotect(connectionId.ToString(), secondAccess, out var plain).ShouldBeTrue();
        var issued = harness.Google.IssuedAccessTokens;
        issued.Count.ShouldBe(2);
        string.Equals(plain, issued[^1], StringComparison.Ordinal)
            .ShouldBeTrue("the row carries the second exchange's access token");
        plain.ShouldNotBe(firstAccessToken);
    }

    [Fact]
    public async Task Reconnect_LocksRowBeforeWritingTokens_ConcurrentConnectsSerialise()
    {
        const string channelId = "canary-store-lock-channel";
        var options = new FakeGoogleOptions { Channel = new GoogleChannel(channelId, ChannelTitle) };
        await using var harness = await StartAsync(Database, Ct, options);
        await ResetAsync(Ct);

        var now = harness.Clock.UtcNow;
        var rowId = await ConnectAsync(harness, harness.User.Id, channelId, "access-seed", "refresh-seed", "client-seed", now, Ct);

        // The seed pair stays the committed one until writer A commits, so the probe below can prove that an
        // uncommitted pair is invisible.
        var seedAccessEnc = ConnectionValue("access_token_enc", $"id = '{rowId}'");

        // Writer A opens its unit of work and reconnects the row without committing: from here until its
        // commit the row's FOR UPDATE lock is held, which is the property the test asserts.
        await using var holder = harness.Factory.Services.CreateAsyncScope();
        using var holderPinned = holder.ServiceProvider.GetRequiredService<ISystemContext>().For(harness.User.Id);
        var holderUnit = holder.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await holderUnit.BeginAsync(Ct);
        var holderWriter = holder.ServiceProvider.GetRequiredService<IConnectChannelConnection>();
        var holderResult = await holderWriter.ConnectAsync(
            Request(harness.User.Id, channelId, "access-a", "refresh-a", "client-a", now.AddMinutes(1)),
            Ct);

        holderResult.IsError.ShouldBeFalse($"the first reconnect must be accepted: {Codes(holderResult)}");
        holderResult.Value.ConnectionId.ShouldBe(rowId, "a reconnect keeps the row's id");
        holderResult.Value.Reconnected.ShouldBeTrue();

        // The lock is proven by the database instead of by timing: a FOR UPDATE NOWAIT read of the row fails
        // with 55P03 (lock_not_available) exactly while writer A's transaction is open, and a plain read still
        // answers the committed pair — the state a reader interleaved with the write must see.
        RowLockSqlState(rowId).ShouldBe(
            "55P03",
            "the reconnect must take the row's lock before it writes a token pair");
        ConnectionValue("access_token_enc", $"id = '{rowId}'")
            .ShouldBe(seedAccessEnc, "writer A's pair is uncommitted and therefore invisible");

        // The second reconnect and a token read both start while A's transaction is open. Neither may complete:
        // the write must wait for the lock (otherwise the pair can be written beside A's) and the read must
        // wait too (otherwise it would observe the uncommitted pair).
        var second = Task.Run(() => ReconnectAndCommitAsync(harness, channelId, "access-b", "refresh-b", "client-b", now.AddMinutes(2)), CancellationToken.None);
        var read = Task.Run(() => ReadTokenAsync(harness, rowId), CancellationToken.None);

        // The wait is the evidence, not a fixed pause: the first of the two to finish within the deadline fails
        // the check, while nobody finishing (the deadline wins) is the pass — and the NowAit probe above has
        // already established that A is the reason, so this cannot pass by accident on a fast or a slow machine.
        var firstToFinish = await Task.WhenAny(second, read, Task.Delay(TimeSpan.FromSeconds(2), Ct));
        var blocked = !ReferenceEquals(firstToFinish, second) && !ReferenceEquals(firstToFinish, read);
        blocked.ShouldBeTrue(
            "the second connect and the token read must both wait for the row lock the first reconnect holds");

        (await holderUnit.CommitAsync(Ct)).IsError.ShouldBeFalse("the first reconnect must commit");

        var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        secondResult.IsError.ShouldBeFalse($"the second reconnect must be accepted: {Codes(secondResult)}");
        secondResult.Value.ConnectionId.ShouldBe(rowId, "both reconnects write the one row");

        var token = await read.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        token.IsError.ShouldBeFalse($"the token read must succeed once the lock is free: {Codes(token)}");
        CommittedPairs.ShouldContain(
            token.Value.AccessToken,
            "the read answers one of the committed pairs, never a mixed one");

        // Exactly one row, and it is the last writer's whole pair — client, tokens and expiry from one write.
        SqlProbe.Count(Database, $"SELECT count(*) FROM channel_connections WHERE user_id = '{harness.User.Id}'")
            .ShouldBe(1);
        var where = $"id = '{rowId}'";
        ConnectionValue("oauth_client_id", where).ShouldBe("client-b");
        ConnectionEpoch("token_expires_at", where).ShouldBe(now.AddMinutes(2).ToUnixTimeSeconds());
        var protector = harness.Protector();
        protector.TryUnprotect(rowId.ToString(), ConnectionValue("access_token_enc", where), out var accessB).ShouldBeTrue();
        protector.TryUnprotect(rowId.ToString(), ConnectionValue("refresh_token_enc", where), out var refreshB).ShouldBeTrue();
        accessB.ShouldBe("access-b");
        refreshB.ShouldBe("refresh-b");
        harness.Google.TokenRequestCount.ShouldBe(0, "a fresh pair is handed out without a Google call");
    }

    // ---------------------------------------------------------------------------------------------------
    // Ownership: another account's live row, and the revoked row that frees the channel (checks 5, 6).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Connect_OtherUserOwnsChannel_RedirectsChannelAlreadyConnected()
    {
        const string channelId = "canary-store-other-user-channel";
        var options = new FakeGoogleOptions { Channel = new GoogleChannel(channelId, ChannelTitle) };
        await using var harness = await StartAsync(Database, Ct, options);
        await ResetAsync(Ct);

        (await NavigateAsync(harness, await PrepareAsync(harness, harness.Client, Ct), Ct))
            .ShouldBe("/channels?connected=youtube");
        var (other, otherClient) = await SecondUserAsync(harness, Ct);

        var where = $"external_account_id = '{channelId}'";
        var connectionId = ConnectionValue("id", where);
        var accessEnc = ConnectionValue("access_token_enc", where);

        var location = await NavigateAsync(harness, await PrepareAsync(harness, otherClient, Ct), Ct);

        location.ShouldBe(
            "/channels?error=" + ErrorCodes.ChannelAlreadyConnected,
            "a live row of another account is the already-connected refusal, never a 500");

        // The owner's row is untouched and no second row exists.
        SqlProbe.Count(Database, $"SELECT count(*) FROM channel_connections WHERE external_account_id = '{channelId}'")
            .ShouldBe(1);
        ConnectionValue("user_id", where).ShouldBe(harness.User.Id.ToString());
        ConnectionValue("id", where).ShouldBe(connectionId);
        ConnectionValue("access_token_enc", where).ShouldBe(accessEnc, "the refusal writes nothing to the owner's row");

        // The refused account wrote nothing of its own: no connection, no message, no outbox row.
        SqlProbe.Count(Database, $"SELECT count(*) FROM channel_connections WHERE user_id = '{other.Id}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE user_id = '{other.Id}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE payload::text LIKE '%{other.Id}%'")
            .ShouldBe(0, "the loser's transaction rolled unit #2 back whole");
    }

    [Fact]
    public async Task Connect_AfterRevoke_SucceedsForAnotherUser()
    {
        const string channelId = "canary-store-revoked-channel";
        var options = new FakeGoogleOptions { Channel = new GoogleChannel(channelId, ChannelTitle) };
        await using var harness = await StartAsync(Database, Ct, options);
        await ResetAsync(Ct);

        (await NavigateAsync(harness, await PrepareAsync(harness, harness.Client, Ct), Ct))
            .ShouldBe("/channels?connected=youtube");
        var (other, otherClient) = await SecondUserAsync(harness, Ct);

        var where = $"external_account_id = '{channelId}'";
        var revoked = Guid.Parse(ConnectionValue("id", where));

        // The user disconnects (06.4.1-T1's Revoke): both token columns and the disconnect instant.
        await Database.ExecuteAsync(
            "UPDATE channel_connections SET status = 'revoked', access_token_enc = NULL, refresh_token_enc = NULL, "
            + $"token_expires_at = now(), revoked_at = now() WHERE id = '{revoked}'",
            Ct);

        var location = await NavigateAsync(harness, await PrepareAsync(harness, otherClient, Ct), Ct);

        location.ShouldBe(
            "/channels?connected=youtube",
            "a revoked row leaves ux_channel_connections_account, so another account may connect the channel");

        SqlProbe.Count(Database, $"SELECT count(*) FROM channel_connections WHERE external_account_id = '{channelId}'")
            .ShouldBe(2, "the revoked history row stays and the new owner gets a new one");
        var second = Guid.Parse(ConnectionValue("id", $"external_account_id = '{channelId}' AND status = 'active'"));
        second.ShouldNotBe(revoked, "the connect after a revoke is a new row, never a revival of the old one");
        ConnectionValue("user_id", $"id = '{second}'").ShouldBe(other.Id.ToString());
        ConnectionValue("status", $"id = '{revoked}'").ShouldBe("revoked");
        ConnectionValue("access_token_enc", $"id = '{revoked}'").ShouldBeNullOrEmpty("Revoke nulls both tokens");
        ConnectionValue("refresh_token_enc", $"id = '{revoked}'").ShouldBeNullOrEmpty();
        ConnectionValue("revoked_at", $"id = '{revoked}'").ShouldNotBeNullOrEmpty();
        ConnectionValue("display_name", $"id = '{revoked}'").ShouldBe(
            ChannelTitle,
            "the identity columns stay until the deletion engine purges them");
    }

    [Fact]
    public async Task ConcurrentConnects_SameChannel_CreateOneRow()
    {
        const string channelId = "canary-store-race-channel";
        var options = new FakeGoogleOptions { Channel = new GoogleChannel(channelId, ChannelTitle) };
        await using var harness = await StartAsync(Database, Ct, options);
        await ResetAsync(Ct);

        var (other, otherClient) = await SecondUserAsync(harness, Ct);

        // Both accounts run the whole flow, then the two callbacks land at the same time.
        var mine = await PrepareAsync(harness, harness.Client, Ct);
        var theirs = await PrepareAsync(harness, otherClient, Ct);

        var responses = await Task.WhenAll(
            harness.CallbackAsync(mine.Code, mine.State, mine.NonceCookie, Ct),
            harness.CallbackAsync(theirs.Code, theirs.State, theirs.NonceCookie, Ct));

        var locations = responses.Select(response => response.Headers.Location?.ToString() ?? string.Empty).ToList();
        foreach (var response in responses)
        {
            response.Dispose();
        }

        locations.Count(location => location == "/channels?connected=youtube")
            .ShouldBe(1, $"one connect wins: {string.Join(" | ", locations)}");
        locations.Count(location => location == "/channels?error=" + ErrorCodes.ChannelAlreadyConnected)
            .ShouldBe(1, $"the loser is refused as already connected, never a 500: {string.Join(" | ", locations)}");

        // The one-owner rule is the partial unique index, so exactly one active row survives the race.
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM channel_connections WHERE external_account_id = '{channelId}' AND status = 'active'")
            .ShouldBe(1);
        SqlProbe.Count(Database, $"SELECT count(*) FROM channel_connections WHERE external_account_id = '{channelId}'")
            .ShouldBe(1);

        // The loser wrote nothing: its whole unit of work rolled back with the refusal.
        var winner = locations[0] == "/channels?connected=youtube" ? harness.User.Id : other.Id;
        var loser = winner == harness.User.Id ? other.Id : harness.User.Id;

        ConnectionValue("user_id", $"external_account_id = '{channelId}'").ShouldBe(winner.ToString());
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE user_id = '{winner}'").ShouldBe(1);
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE user_id = '{loser}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE payload::text LIKE '%{loser}%'")
            .ShouldBe(0, "the loser queues neither the message nor the connection-change event");
    }

    // ---------------------------------------------------------------------------------------------------
    // The channel list (checks 7's second half and 11's endpoint contract).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListChannels_ReturnsCallerOnlyWithoutTokenColumns()
    {
        var options = new FakeGoogleOptions();
        await using var harness = await StartAsync(Database, Ct, options);
        await ResetAsync(Ct);

        var now = harness.Clock.UtcNow;
        var (other, _) = await SecondUserAsync(harness, Ct);

        // The newer row is written first, so an implementation ordering by id or by insertion would answer the
        // opposite order and fail the DESC assertion below.
        var newer = await ConnectAsync(harness, harness.User.Id, "canary-list-newer", "access-newer", "refresh-newer", "client-newer", now, Ct);
        var older = await ConnectAsync(harness, harness.User.Id, "canary-list-older", "access-older", "refresh-older", "client-older", now.AddHours(-2), Ct);
        var foreign = await ConnectAsync(harness, other.Id, "canary-list-foreign", "access-foreign", "refresh-foreign", "client-foreign", now, Ct);

        using var response = await harness.Client.GetAsync(ChannelsRoute, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"the list answers 200: {body}");

        using var json = JsonDocument.Parse(body);
        json.RootElement.TryGetProperty("items", out var items).ShouldBeTrue("the envelope is { items: [...] }");
        items.GetArrayLength().ShouldBe(2, "only the caller's own rows are listed");

        items[0].GetProperty("id").GetGuid().ShouldBe(newer, "ordered by connected_at DESC");
        items[1].GetProperty("id").GetGuid().ShouldBe(older);
        items[0].GetProperty("status").GetString().ShouldBe("active");
        items[0].GetProperty("platform").GetString().ShouldBe("youtube");
        items[0].GetProperty("externalAccountId").GetString().ShouldBe("canary-list-newer");
        items[0].GetProperty("displayName").GetString().ShouldBe(ChannelTitle);
        items[0].GetProperty("connectedAt").GetDateTimeOffset().ShouldBe(now);
        items[0].GetProperty("lastVerifiedAt").GetDateTimeOffset().ShouldBe(now);

        foreach (var item in items.EnumerateArray())
        {
            item.EnumerateObject().Select(property => property.Name).Order().ShouldBe(ChannelListMembers);
        }

        body.ShouldNotContain("oauthClientId", Case.Insensitive, "the pinned client is internal (D17)");
        body.ShouldNotContain("accessToken", Case.Insensitive);
        body.ShouldNotContain("refreshToken", Case.Insensitive);
        body.ShouldNotContain("access-newer");
        body.ShouldNotContain(foreign.ToString(), Case.Insensitive, "another account's row is not on the wire");
        body.ShouldNotContain("canary-list-foreign");
    }

    [Fact]
    public async Task ListChannels_UsesOneStatement()
    {
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);
        await ResetAsync(Ct);

        // Raw rows: the projection of the list may not read a token column, so the ciphertext here is a canary
        // the query could only leak by reading it.
        var now = DateTimeOffset.UtcNow;
        var rows = new[]
        {
            (Id: Guid.Parse("019a2c00-0000-7000-8000-000000000001"), Channel: "canary-statement-newer", At: now),
            (Id: Guid.Parse("019a2c00-0000-7000-8000-000000000002"), Channel: "canary-statement-older", At: now.AddHours(-1)),
        };

        foreach (var row in rows)
        {
            SqlProbe.Execute(
                Database,
                "INSERT INTO channel_connections (id, user_id, platform, external_account_id, display_name, status, "
                + "access_token_enc, refresh_token_enc, token_expires_at, scope, oauth_client_id, last_verified_at, "
                + $"connected_at) VALUES ('{row.Id}', '{user.Id}', 'youtube', "
                + $"'{row.Channel}', '{ChannelTitle}', 'active', 'canary-ciphertext-access', 'canary-ciphertext-refresh', "
                + $"'{row.At:O}'::timestamptz, '{GrantedScope}', 'canary-statement-client', "
                + $"'{row.At:O}'::timestamptz, '{row.At:O}'::timestamptz)");
        }

        var interceptor = new CountingDbCommandInterceptor();
        var options = new DbContextOptionsBuilder<ReadDbContext>().UseNpgsql(Database.ConnectionString);
        options.AddInterceptors(interceptor);
        await using var readDb = new ReadDbContext(options.Options, new TestCurrentUser { Id = user.Id });

        interceptor.Reset();
        var channels = await new ListChannelsHandler(readDb).Handle(new ListChannelsQuery(), Ct);

        channels.IsError.ShouldBeFalse($"the list has no failure mode: {Codes(channels)}");
        channels.Value.Count.ShouldBe(2);
        interceptor.Count.ShouldBe(
            1,
            $"one SELECT for the whole list, no N+1 and no follow-up: {string.Join(" | ", interceptor.Texts)}");
        interceptor.ObservedConcurrentExecution.ShouldBeFalse("the list is one statement, so nothing overlaps");

        var text = interceptor.Texts.Single();
        text.ShouldContain("channel_connections");
        text.ShouldNotContain("access_token_enc", Case.Insensitive);
        text.ShouldNotContain("refresh_token_enc", Case.Insensitive);
        text.ShouldNotContain("oauth_client_id", Case.Insensitive, "the pinning detail is not in the projection");
    }

    /// <remarks>
    /// The other half of acceptance check 8 — another platform's connect route answering 404 <c>not_found</c> — is
    /// already covered by <c>ConnectChannelQaTests.Callback_UnknownPlatform_AndUnservedConnectPlatform_Answer404WithoutSideEffects</c>,
    /// which posts <c>/api/v1/channels/tiktok/connect</c> and asserts the code and the untouched state row; this
    /// file adds the T2-owned route instead of repeating it.
    /// </remarks>
    [Fact]
    public async Task GetChannels_FlagOff_Returns404FeatureDisabled()
    {
        await using var harness = await StartAsync(Database, Ct, flagOn: false);
        await ResetAsync(Ct);

        using var response = await harness.Client.GetAsync(ChannelsRoute, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(
            ErrorCodes.FeatureDisabled,
            "the flag gate runs before the query, so a disabled deployment cannot read channels at all");
    }

    // ---------------------------------------------------------------------------------------------------
    // The connect e-mail and the redaction duties (checks 8's message, 9, 12, 13).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Callback_QueuesChannelConnectedEmailWithoutToken()
    {
        const string channelId = "canary-store-email-channel";
        var options = new FakeGoogleOptions { Channel = new GoogleChannel(channelId, ChannelTitle) };
        await using var harness = await StartAsync(Database, Ct, options, realEmailSender: true);
        await ResetAsync(Ct);

        // The real sender: the row, its template validation against the catalog and its outbox event are all
        // production code (the harness's stand-in writes `{}` params and is kept for the rollback case).
        var prepared = await PrepareAsync(harness, harness.Client, Ct);
        var location = await NavigateWithUserAgentAsync(harness, prepared, Ct);

        location.ShouldBe("/channels?connected=youtube");

        var userId = harness.User.Id;
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE user_id = '{userId}'")
            .ShouldBe(1, "one message per connect");

        var where = $"user_id = '{userId}'";
        ConnectionValueOf("email_messages", "template", where).ShouldBe("channel_connected");
        ConnectionValueOf("email_messages", "status", where).ShouldBe("queued", "the queue never sends (A8)");
        ConnectionValueOf("email_messages", "locale", where).ShouldBe(
            ConnectionValueOf("users", "ui_locale", $"id = '{userId}'"),
            "the locale is the account's own ui_locale (09 D16)");

        var paramsJson = ConnectionValueOf("email_messages", "params", where);
        using (var parsed = JsonDocument.Parse(paramsJson))
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in parsed.RootElement.EnumerateObject())
            {
                values[property.Name] = property.Value.GetString() ?? string.Empty;
            }

            values.Keys.Order().ShouldBe(EmailParameterKeys);
            values["displayName"].ShouldBe("auth test", "the harness account's display name");
            values["channelTitle"].ShouldBe(ChannelTitle);
            values["securityLink"].ShouldBe(SecurityLink, "a static path, never a minted link (WT17/WT30)");
            values["device"].ShouldBe(DeviceClause, "a short User-Agent summary, not the whole header");
            DateTimeOffset.Parse(values["occurredAt"], System.Globalization.CultureInfo.InvariantCulture)
                .ShouldBe(harness.Clock.UtcNow, "occurredAt is the connect instant, ISO-8601");
        }

        // No token, no address and no link value reaches the message, and only the hash of the address is stored.
        var emailRow = RowTextBy("email_messages", "id", ConnectionValueOf("email_messages", "id", where));
        emailRow.ShouldNotContain(harness.Google.IssuedAccessTokens.Single(), Case.Sensitive);
        emailRow.ShouldNotContain(harness.User.Email!, Case.Insensitive, "the row keeps only to_hash (BD16)");
        emailRow.ShouldNotContain("access_token");
        emailRow.ShouldNotContain("\"link\"", Case.Sensitive);
        ConnectionValueOf("email_messages", "to_hash", where).ShouldNotBeNullOrEmpty();
        ConnectionValueOf("email_messages", "to_hash", where).ShouldNotBe(harness.User.Email);

        // Both outbox rows of the one unit: the request for the message and the connection change.
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type = 'email_requested.v1' AND payload::text LIKE '%{userId}%'")
            .ShouldBe(1);
        var changePayload = SqlProbe.ScalarText(
            Database,
            $"SELECT payload::text FROM outbox_messages WHERE type = 'channel_connection_changed.v1' "
            + $"AND payload::text LIKE '%{userId}%'");
        changePayload.ShouldNotBeNullOrEmpty("the connect raises channel_connection_changed.v1 (D13)");
        var connectionId = ConnectionValue("id", $"external_account_id = '{channelId}'");
        changePayload.ShouldContain(connectionId, Case.Sensitive);

        // The payload is read as JSON, not as a text fragment: the column is jsonb, whose text rendering spaces
        // its separators, and D13 fixes the member set — the ids and the state, never a token or the title.
        using (var change = JsonDocument.Parse(changePayload))
        {
            var root = change.RootElement;
            root.EnumerateObject().Select(property => property.Name).Order().ShouldBe(
                ["connectionId", "occurredAt", "status", "userId"]);
            root.GetProperty("connectionId").GetGuid().ShouldBe(Guid.Parse(connectionId));
            root.GetProperty("userId").GetGuid().ShouldBe(userId);
            root.GetProperty("status").GetString().ShouldBe("active", "ids and the state only");
        }

        changePayload.ShouldNotContain(ChannelTitle, Case.Sensitive);
        changePayload.ShouldNotContain(harness.Google.IssuedAccessTokens.Single(), Case.Sensitive);
    }

    [Fact]
    public async Task SendEmailJob_ChannelConnected_RendersAndNullsParams()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        await using var harness = await EmailHarness.StartAsync(Database, clock, ct);

        var id = await harness.QueueAsync(
            owner.Id,
            "channel_connected",
            Language.Vi,
            EmailHarness.Values(
                ("displayName", "Chủ kênh"),
                ("channelTitle", ChannelTitle),
                ("occurredAt", "2026-10-18 12:00"),
                ("device", DeviceClause),
                ("securityLink", SecurityLink)),
            ct);

        (await harness.DeliverAsync(ct)).ShouldBe(1, "the outbox row reaches the job and the job sends");

        harness.Smtp.CallCount.ShouldBe(1, "one queued message is one SMTP conversation");
        var message = harness.Smtp.LastMessage;
        message.ShouldNotBeNull();
        message.Subject.ShouldNotBeNullOrWhiteSpace();
        message.To.Mailboxes.Single().Address.ShouldBe(owner.Email);
        message.TextBody.ShouldNotBeNull().ShouldContain(ChannelTitle);
        message.TextBody.ShouldContain(SecurityLink);
        message.TextBody.ShouldContain("Chủ kênh", Case.Sensitive, "the substituted display name travels verbatim");
        message.HtmlBody.ShouldNotBeNull().ShouldContain(WebUtility.HtmlEncode(ChannelTitle));

        // The template is not token-bearing: nothing mints or carries a one-time token (WT30).
        message.TextBody.ShouldNotContain("#t=");
        message.HtmlBody.ShouldNotContain("#t=");

        SqlProbe.ScalarText(Database, $"SELECT status FROM email_messages WHERE id = '{id}'").ShouldBe("sent");
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE id = '{id}' AND attempts = 1").ShouldBe(1);
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE id = '{id}' AND params IS NULL")
            .ShouldBe(1, "a sent row keeps no render values (WT17)");
    }

    [Fact]
    public async Task Callback_LogsNoChannelTitleTokenOrAddress()
    {
        const string channelId = "canary-store-log-channel";
        var options = new FakeGoogleOptions { Channel = new GoogleChannel(channelId, ChannelTitle) };
        var sink = new TestLogSink();
        await using var harness = await StartAsync(Database, Ct, options, sink, realEmailSender: true);
        await ResetAsync(Ct);

        var location = await NavigateAsync(harness, await PrepareAsync(harness, harness.Client, Ct), Ct);
        location.ShouldBe("/channels?connected=youtube");

        var accessToken = harness.Google.IssuedAccessTokens.Single();
        var refreshToken = ExpectedRefreshToken(accessToken);
        var connectionId = Guid.Parse(ConnectionValue("id", $"external_account_id = '{channelId}'"));

        var text = sink.AllText();
        text.ShouldNotBeNullOrWhiteSpace("the sink must have seen the host's lines, or the search proves nothing");

        // The successful write is logged by ids only (the two log lines of the handler), so nothing secret and
        // nothing user-visible about the channel may appear in any captured line.
        text.ShouldContain(connectionId.ToString(), Case.Sensitive, "the write's own line names the connection id");
        text.ShouldNotContain(ChannelTitle, Case.Sensitive);
        text.ShouldNotContain(accessToken, Case.Sensitive);
        text.ShouldNotContain(refreshToken, Case.Sensitive);
        text.ShouldNotContain(harness.User.Email!, Case.Insensitive);
        text.ShouldNotContain(options.ActiveClientSecret, Case.Sensitive);
        text.ShouldNotContain("client_secret", Case.Insensitive);

        // The audit row of the same write carries ids only, too (D17).
        harness.AuditData().ShouldNotContain(accessToken, Case.Sensitive);
        harness.AuditData().ShouldNotContain(refreshToken, Case.Sensitive);
    }

    [Fact]
    public async Task FailedConnect_WithQueueRefusal_LeavesNoConnectionEmailOrOutboxRow()
    {
        const string channelId = "canary-store-rollback-channel";
        var options = new FakeGoogleOptions { Channel = new GoogleChannel(channelId, ChannelTitle) };
        await using var harness = await StartAsync(Database, Ct, options);
        await ResetAsync(Ct);

        // The sender fails *after* it wrote its own real row, and the writer has already tracked the
        // connection row by then: whether anything survives is exactly what the rollback of unit #2 decides.
        harness.Email.WriteBeforeFailing = true;
        harness.Email.Fail = true;

        var refused = await NavigateAsync(harness, await PrepareAsync(harness, harness.Client, Ct), Ct);

        refused.ShouldBe("/channels?error=" + ErrorCodes.OauthExchangeFailed);
        harness.Email.CallCount.ShouldBe(1, "the sender must have been reached, or the rollback proves nothing");

        var userId = harness.User.Id;
        SqlProbe.Count(Database, $"SELECT count(*) FROM channel_connections WHERE user_id = '{userId}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE user_id = '{userId}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE payload::text LIKE '%{userId}%'").ShouldBe(0);
        harness.AuditRows().ShouldBe(0, "a refused connect writes no audit row");

        // Positive control: the same write path with the sender answering success commits all three rows, so
        // the zeros above cannot be a path that never wrote anything.
        harness.Email.Fail = false;

        var connected = await NavigateAsync(harness, await PrepareAsync(harness, harness.Client, Ct), Ct);

        connected.ShouldBe("/channels?connected=youtube");
        SqlProbe.Count(Database, $"SELECT count(*) FROM channel_connections WHERE user_id = '{userId}'")
            .ShouldBe(1, "the control writes the row the refused run rolled back");
        SqlProbe.Count(Database, $"SELECT count(*) FROM email_messages WHERE user_id = '{userId}'").ShouldBe(1);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM outbox_messages WHERE type IN ('channel_connection_changed.v1', 'email_requested.v1') "
                + $"AND payload::text LIKE '%{userId}%'")
            .ShouldBe(2);
    }

    // ---------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts the world with the <em>production</em> <c>IConnectChannelConnection</c> registered: the real
    /// store writer, the real OAuth state store and the real e-mail sender when a test asks for it.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="ct">Cancels the start.</param>
    /// <param name="googleOptions">The fake's switches; a default instance when null.</param>
    /// <param name="logSink">A sink the host logs through, for the redaction check.</param>
    /// <param name="realEmailSender">Whether the production sender stays in place (the stand-in otherwise).</param>
    /// <param name="flagOn">Whether <c>youtube_publish</c> is on.</param>
    private static Task<ConnectChannelHarness> StartAsync(
        TestDatabase database,
        CancellationToken ct,
        FakeGoogleOptions? googleOptions = null,
        TestLogSink? logSink = null,
        bool realEmailSender = false,
        bool flagOn = true) =>
        ConnectChannelHarness.StartAsync(
            database,
            ct,
            googleOptions: googleOptions,
            flagOn: flagOn,
            logSink: logSink,
            channelWriter: ConnectChannelHarness.ConnectChannelWriter.Production,
            realEmailSender: realEmailSender);

    /// <summary>
    /// Clears the three tables one test of this class writes, so a row, a message or an outbox event of an
    /// earlier test can never be read as this test's (T1 needed none: its stand-in wrote no connection row).
    /// </summary>
    /// <param name="ct">Cancels the delete.</param>
    private async Task ResetAsync(CancellationToken ct)
    {
        await Database.ExecuteAsync("DELETE FROM channel_connections", ct);
        await Database.ExecuteAsync("DELETE FROM email_messages", ct);
        await Database.ExecuteAsync("DELETE FROM outbox_messages", ct);
    }

    /// <summary>
    /// Runs the connect route as <paramref name="client"/> and drives the fake's authorization endpoint, so
    /// the returned pair is one single-use code with the state and nonce cookie its navigation must present.
    /// </summary>
    /// <param name="harness">The test's world.</param>
    /// <param name="client">The authenticated client that starts the flow.</param>
    /// <param name="ct">Cancels the calls.</param>
    /// <param name="clientId">
    /// The client the authorization URL must carry, when a test simulates the client rotation of YR11 (the
    /// state row is updated to the same id by the caller).
    /// </param>
    private static async Task<PreparedCallback> PrepareAsync(
        ConnectChannelHarness harness,
        HttpClient client,
        CancellationToken ct,
        string? clientId = null)
    {
        using var started = await client.PostAsync(ConnectChannelHarness.ConnectRoute, content: null, ct);
        var connect = await ConnectChannelHarness.ReadConnectAsync(started, ct);

        var authorizationUrl = connect.AuthorizationUrl;
        if (clientId is not null)
        {
            authorizationUrl = authorizationUrl.Replace(
                $"client_id={harness.Google.Options.ActiveClientId}",
                $"client_id={clientId}",
                StringComparison.Ordinal);
            authorizationUrl.ShouldNotBe(
                connect.AuthorizationUrl,
                "the authorization URL must be rewritten for the rotated client, or the fake refuses the code");
        }

        var code = await harness.AuthorizeAsync(authorizationUrl, ct);

        return new PreparedCallback(code, connect.State, connect.NonceCookie);
    }

    /// <summary>Drives the anonymous callback navigation and answers its <c>Location</c>.</summary>
    /// <param name="harness">The test's world.</param>
    /// <param name="prepared">The code, state and nonce cookie the connect minted.</param>
    /// <param name="ct">Cancels the call.</param>
    private static async Task<string> NavigateAsync(
        ConnectChannelHarness harness,
        PreparedCallback prepared,
        CancellationToken ct)
    {
        using var response = await harness.CallbackAsync(
            prepared.Code,
            prepared.State,
            prepared.NonceCookie,
            ct);

        return response.Headers.Location?.ToString()
            ?? throw new InvalidOperationException("the callback must answer its fixed redirect");
    }

    /// <summary>
    /// The same navigation with a real <c>User-Agent</c>, for the e-mail's <c>device</c> value: the callback
    /// route reads it from the request, and the harness's own helper sends no such header.
    /// </summary>
    /// <param name="harness">The test's world.</param>
    /// <param name="prepared">The code, state and nonce cookie the connect minted.</param>
    /// <param name="ct">Cancels the call.</param>
    private static async Task<string> NavigateWithUserAgentAsync(
        ConnectChannelHarness harness,
        PreparedCallback prepared,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            QueryHelpers.AddQueryString(
                ConnectChannelHarness.CallbackRoute,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["code"] = prepared.Code,
                    ["state"] = prepared.State,
                }));
        request.Headers.Add("Cookie", prepared.NonceCookie);
        request.Headers.Add("User-Agent", UserAgent);

        using var response = await harness.Anonymous.SendAsync(request, ct);

        return response.Headers.Location?.ToString()
            ?? throw new InvalidOperationException("the callback must answer its fixed redirect");
    }

    /// <summary>
    /// Connects one channel through the production writer in its own unit of work, as the callback does,
    /// which is how a test seeds a row without driving the whole OAuth flow.
    /// </summary>
    /// <param name="harness">The test's world.</param>
    /// <param name="userId">The owner the scope is pinned to.</param>
    /// <param name="channelId">Google's channel id.</param>
    /// <param name="accessToken">The plaintext access token the writer protects.</param>
    /// <param name="refreshToken">The plaintext refresh token the writer protects.</param>
    /// <param name="clientId">The client the row pins.</param>
    /// <param name="now">The connect instant (the row's <c>connected_at</c>).</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>The new row's id.</returns>
    private static async Task<Guid> ConnectAsync(
        ConnectChannelHarness harness,
        Guid userId,
        string channelId,
        string accessToken,
        string refreshToken,
        string clientId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var scope = harness.Factory.Services.CreateAsyncScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(userId);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginAsync(ct);

        var connected = await scope
            .ServiceProvider
            .GetRequiredService<IConnectChannelConnection>()
            .ConnectAsync(Request(userId, channelId, accessToken, refreshToken, clientId, now), ct);

        connected.IsError.ShouldBeFalse($"the store writer must accept the connect: {Codes(connected)}");
        (await unitOfWork.CommitAsync(ct)).IsError.ShouldBeFalse("the connection's unit of work must commit");

        return connected.Value.ConnectionId;
    }

    /// <summary>One reconnect of the same channel in its own scope and unit of work, committing at once.</summary>
    /// <param name="harness">The test's world.</param>
    /// <param name="channelId">Google's channel id.</param>
    /// <param name="accessToken">The new plaintext access token.</param>
    /// <param name="refreshToken">The new plaintext refresh token.</param>
    /// <param name="clientId">The client the row is re-pinned to.</param>
    /// <param name="now">The refresh instant.</param>
    private static async Task<ErrorOr<ConnectChannelOutcome>> ReconnectAndCommitAsync(
        ConnectChannelHarness harness,
        string channelId,
        string accessToken,
        string refreshToken,
        string clientId,
        DateTimeOffset now)
    {
        await using var scope = harness.Factory.Services.CreateAsyncScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(harness.User.Id);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginAsync(CancellationToken.None);

        var connected = await scope
            .ServiceProvider
            .GetRequiredService<IConnectChannelConnection>()
            .ConnectAsync(
                Request(harness.User.Id, channelId, accessToken, refreshToken, clientId, now),
                CancellationToken.None);

        if (connected.IsError)
        {
            return connected.Errors;
        }

        var committed = await unitOfWork.CommitAsync(CancellationToken.None);

        return committed.IsError ? committed.Errors : connected.Value;
    }

    /// <summary>Reads one connection's access token through the real <c>ITokenService</c>, own scope pinned.</summary>
    /// <param name="harness">The test's world.</param>
    /// <param name="connectionId">The row to read.</param>
    private static async Task<ErrorOr<ChannelAccessToken>> ReadTokenAsync(
        ConnectChannelHarness harness,
        Guid connectionId)
    {
        await using var scope = harness.Factory.Services.CreateAsyncScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(harness.User.Id);

        return await scope
            .ServiceProvider
            .GetRequiredService<ITokenService>()
            .GetAccessTokenAsync(connectionId, CancellationToken.None);
    }

    /// <summary>
    /// Creates a second account of the same host and returns it with a client that passed the connect route's
    /// gates (a live family, the real re-authentication and the current legal acceptance), so a connect can be
    /// started as that account.
    /// </summary>
    /// <param name="harness">The test's world.</param>
    /// <param name="ct">Cancels the setup.</param>
    private async Task<(AppUser User, HttpClient Client)> SecondUserAsync(
        ConnectChannelHarness harness,
        CancellationToken ct)
    {
        var user = await AuthTestFlow.CreateUserAsync(harness.Factory.Services);
        var familyId = await MfaTestFlow.SeedFamilyAsync(Database, user, harness.Clock.UtcNow);

        var client = harness.Factory.ClientFor(user.Id, user.Role, sessionId: familyId);
        client.DefaultRequestHeaders.Add(AuthTestFlow.OriginHeaderName, AuthTestFlow.Origin);

        using (var reauth = await client.PostAsJsonAsync(
                   MfaTestFlow.ReauthPath,
                   new { password = MfaTestFlow.Password },
                   ct))
        {
            reauth.StatusCode.ShouldBe(
                HttpStatusCode.OK,
                "the second account must satisfy the connect route's re-authentication gate");
        }

        var configuration = harness.Factory.Services.GetRequiredService<IConfiguration>();
        using (var accepted = await client.PostAsJsonAsync(
                   LegalAcceptRoute,
                   new
                   {
                       tos = configuration["LegalDocuments:Current:Tos"] ?? "1.0.0",
                       privacy = configuration["LegalDocuments:Current:Privacy"] ?? "1.0.0",
                   },
                   ct))
        {
            accepted.StatusCode.ShouldBe(HttpStatusCode.OK, "the second account must accept the legal documents");
        }

        return (user, client);
    }

    /// <summary>The request the real writer is driven with, as the callback builds it.</summary>
    /// <param name="userId">The owner.</param>
    /// <param name="channelId">Google's channel id.</param>
    /// <param name="accessToken">The plaintext access token.</param>
    /// <param name="refreshToken">The plaintext refresh token.</param>
    /// <param name="clientId">The client the state pinned.</param>
    /// <param name="now">The connect instant.</param>
    private static ConnectChannelConnectionRequest Request(
        Guid userId,
        string channelId,
        string accessToken,
        string refreshToken,
        string clientId,
        DateTimeOffset now) =>
        new(
            userId,
            Platform.YouTube,
            new GoogleChannel(channelId, ChannelTitle),
            new OAuthTokens(accessToken, refreshToken, now.AddHours(1), GrantedScope),
            clientId,
            now);

    /// <summary>The fake's refresh token for the exchange that issued <paramref name="accessToken"/>.</summary>
    /// <param name="accessToken">The <c>canary-access-token-N</c> the fake answered.</param>
    private static string ExpectedRefreshToken(string accessToken)
    {
        const string prefix = "canary-access-token-";

        accessToken.ShouldStartWith(prefix, Case.Sensitive);

        return "canary-refresh-token-" + accessToken[prefix.Length..];
    }

    /// <summary>The error codes of a failed answer, for an assertion message.</summary>
    /// <param name="result">The failed result.</param>
    private static string Codes<T>(ErrorOr<T> result) =>
        string.Join(", ", result.Errors.Select(error => error.Code));

    /// <summary>One column of one <c>channel_connections</c> row.</summary>
    /// <param name="column">The column name.</param>
    /// <param name="where">The row's predicate.</param>
    private string ConnectionValue(string column, string where) =>
        SqlProbe.ScalarText(Database, $"SELECT {column} FROM channel_connections WHERE {where}");

    /// <summary>
    /// The PostgreSQL SQLSTATE of a <c>FOR UPDATE NOWAIT</c> read of one connection row: <c>55P03</c>
    /// (<c>lock_not_available</c>) while another open transaction holds it. The statement itself runs outside any
    /// transaction, so on an unlocked row it takes and releases the lock at once.
    /// </summary>
    /// <param name="connectionId">The row to probe.</param>
    private string RowLockSqlState(Guid connectionId) =>
        SqlProbe.SqlState(
            Database,
            $"SELECT id FROM channel_connections WHERE id = '{connectionId}' FOR UPDATE NOWAIT");

    /// <summary>
    /// One <c>timestamptz</c> column of one <c>channel_connections</c> row as Unix seconds, so an instant is
    /// compared as an instant: Npgsql reads the column back as a <see cref="DateTime"/> whose text rendering
    /// is culture- and precision-dependent, while the harness's clock only ever reads whole seconds.
    /// </summary>
    /// <param name="column">The column name.</param>
    /// <param name="where">The row's predicate.</param>
    private long ConnectionEpoch(string column, string where) =>
        SqlProbe.Count(Database, $"SELECT extract(epoch from {column})::bigint FROM channel_connections WHERE {where}");

    /// <summary>One column of one row of any table of this test's schema.</summary>
    /// <param name="table">The table name.</param>
    /// <param name="column">The column name.</param>
    /// <param name="where">The row's predicate.</param>
    private string ConnectionValueOf(string table, string column, string where) =>
        SqlProbe.ScalarText(Database, $"SELECT {column} FROM {table} WHERE {where}");

    /// <summary>The whole row as JSON text, so a grep over it proves no column holds a value.</summary>
    /// <param name="table">The table name.</param>
    /// <param name="where">The row's predicate.</param>
    private string RowText(string table, string where) =>
        SqlProbe.ScalarText(Database, $"SELECT to_jsonb(candidate)::text FROM {table} candidate WHERE {where}");

    /// <summary>The whole row named by one column as JSON text.</summary>
    /// <param name="table">The table name.</param>
    /// <param name="key">The identifying column.</param>
    /// <param name="value">Its value.</param>
    private string RowTextBy(string table, string key, string value) => RowText(table, $"{key} = '{value}'");

    /// <summary>The pair one connect minted: the code to exchange, its state and its browser nonce.</summary>
    /// <param name="Code">The one-shot authorization code.</param>
    /// <param name="State">The state the connect route minted and the callback must present.</param>
    /// <param name="NonceCookie">The nonce cookie pair the callback must present.</param>
    private sealed record PreparedCallback(string Code, string State, string NonceCookie);
}
