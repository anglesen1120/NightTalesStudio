using System.Net;
using System.Text.Json;
using ErrorOr;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Auth;
using NightTales.Api.IntegrationTests.Email;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Publishing.Channels;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Google;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// Independent QA pass over 06.1.2-T2 against the real host and a real PostgreSQL 17: the exploratory and
/// negative cases the task file's own suite does not run — a reconnect of a dead (<c>expired</c>/<c>error</c>)
/// grant, a revoked row reconnected by the same account, the revoked history row as <c>GET /channels</c> shows
/// it, an over-long title or scope failing loudly instead of being truncated, the two-account race repeated
/// across several channels, and a Unicode channel title in the connect e-mail's parameters, plus the HTML
/// body's escaping of a markup-bearing title.
/// </summary>
/// <remarks>
/// <para>
/// These tests were added by the independent QA agent of session 61 and assert observed behaviour of the
/// shipped code; a failing test here is a defect report, not a skipped check. They drive the real
/// <c>IConnectChannelConnection</c> (the production registration), the real callback route and the real
/// <c>channel_connections</c> store, and read the rows back with <see cref="SqlProbe"/>.
/// </para>
/// <para>
/// The port-driven cases run in their own scope with the user pinned through <c>ISystemContext.For</c>, exactly
/// as <see cref="ChannelConnectionStoreTests"/> does, because the anonymous callback cannot pin scope for the
/// second unit of work.
/// </para>
/// </remarks>
/// <param name="database">The class database, a migrated copy of the run's template.</param>
[Collection("Hangfire server")]
public sealed class ChannelConnectionQaTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The channel title the port-driven cases store (the callback uses the fake's own title).</summary>
    private const string Title = "NightTales QA Canary";

    /// <summary>The route <c>GET /channels</c> answers on.</summary>
    private const string ChannelsRoute = "/api/v1/channels";

    /// <summary>The <c>User-Agent</c> the callback navigation presents (the e-mail's device clause).</summary>
    private const string UserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36";

    /// <summary>The exact member set of one <c>GET /channels</c> item (07 members, no token, no client id).</summary>
    private static readonly string[] ChannelListMembers =
        ["connectedAt", "displayName", "externalAccountId", "id", "lastVerifiedAt", "platform", "status"];

    /// <summary>The five substitution keys the <c>channel_connected</c> catalog entry accepts, sorted.</summary>
    private static readonly string[] EmailParameterKeys =
        ["channelTitle", "device", "displayName", "occurredAt", "securityLink"];

    /// <summary>How many rounds the two-account race is repeated.</summary>
    private const int RaceRounds = 3;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------------------
    // Reconnect: a dead grant, and the revoked row of the same account (failure-table cases).
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// An <c>expired</c> or <c>error</c> row of the same account is reconnected in place: same id, same
    /// <c>connected_at</c>, the token pair and the client pin replaced, no disconnect moment.
    /// </summary>
    /// <param name="status">The dead-grant state Google left the row in.</param>
    [Theory]
    [InlineData("expired")]
    [InlineData("error")]
    public async Task Reconnect_OfADeadGrantRow_ReusesTheRowAndWritesTheNewPair(string status)
    {
        var channelId = $"canary-qa-dead-{status}";
        await using var harness = await StartAsync(Database, Ct, new FakeGoogleOptions { Channel = new GoogleChannel(channelId, Title) });
        await ResetAsync(Ct);

        var now = harness.Clock.UtcNow;
        var rowId = await ConnectAsync(harness, harness.User.Id, channelId, "access-1", "refresh-1", "client-1", now, Ct);
        var connectedAt = ConnectionEpoch("connected_at", $"id = '{rowId}'");

        await Database.ExecuteAsync(
            $"UPDATE channel_connections SET status = '{status}' WHERE id = '{rowId}'",
            Ct);

        var reconnected = await ReconnectAndCommitAsync(
            harness,
            channelId,
            "access-2",
            "refresh-2",
            "client-2",
            now.AddDays(1));

        reconnected.IsError.ShouldBeFalse($"a dead grant of the same account must be reconnected: {Codes(reconnected)}");
        reconnected.Value.ConnectionId.ShouldBe(rowId, "a reconnect keeps the row's id");
        reconnected.Value.Reconnected.ShouldBeTrue("the writer must report the reconnect, not a fresh insert");

        var where = $"external_account_id = '{channelId}'";
        SqlProbe.Count(Database, $"SELECT count(*) FROM channel_connections WHERE {where}").ShouldBe(1);
        ConnectionValue("status", where).ShouldBe("active");
        ConnectionValue("oauth_client_id", where).ShouldBe("client-2", "the client is re-pinned to the state's client");
        ConnectionValue("revoked_at", where).ShouldBeNullOrEmpty();
        ConnectionEpoch("connected_at", where).ShouldBe(connectedAt, "one row, one connected_at");
        ConnectionEpoch("last_verified_at", where).ShouldBe(now.AddDays(1).ToUnixTimeSeconds());

        var protector = harness.Protector();
        protector.TryUnprotect(rowId.ToString(), ConnectionValue("access_token_enc", where), out var access).ShouldBeTrue();
        protector.TryUnprotect(rowId.ToString(), ConnectionValue("refresh_token_enc", where), out var refresh).ShouldBeTrue();
        access.ShouldBe("access-2", "the new cipher is protected under the same connection id");
        refresh.ShouldBe("refresh-2");
    }

    /// <summary>
    /// The same account reconnecting a channel whose own row is <c>revoked</c> gets a <em>new</em> row: the
    /// revoked row left the partial unique index, so the connect is a fresh insert and the history row stays
    /// exactly as the disconnect left it.
    /// </summary>
    [Fact]
    public async Task Reconnect_AfterRevoke_SameAccountSameChannel_CreatesANewRow()
    {
        const string channelId = "canary-qa-revoked-same-user";
        await using var harness = await StartAsync(Database, Ct, new FakeGoogleOptions { Channel = new GoogleChannel(channelId, Title) });
        await ResetAsync(Ct);

        var now = harness.Clock.UtcNow;
        var revoked = await ConnectAsync(harness, harness.User.Id, channelId, "access-1", "refresh-1", "client-1", now, Ct);

        await Database.ExecuteAsync(
            "UPDATE channel_connections SET status = 'revoked', access_token_enc = NULL, refresh_token_enc = NULL, "
            + $"token_expires_at = now(), revoked_at = now() WHERE id = '{revoked}'",
            Ct);

        var connected = await ReconnectAndCommitAsync(harness, channelId, "access-2", "refresh-2", "client-2", now.AddHours(1));

        connected.IsError.ShouldBeFalse($"a revoked row must not block its own account's next connect: {Codes(connected)}");
        connected.Value.ConnectionId.ShouldNotBe(revoked, "the revoked connection is history, not a revival");
        connected.Value.Reconnected.ShouldBeFalse("the revoked row is outside the partial unique index, so this is an insert");

        var where = $"external_account_id = '{channelId}'";
        SqlProbe.Count(Database, $"SELECT count(*) FROM channel_connections WHERE {where}").ShouldBe(2);
        ConnectionValue("status", $"id = '{revoked}'").ShouldBe("revoked");
        ConnectionValue("access_token_enc", $"id = '{revoked}'").ShouldBeNullOrEmpty();
        ConnectionValue("refresh_token_enc", $"id = '{revoked}'").ShouldBeNullOrEmpty();
        ConnectionValue("status", $"id = '{connected.Value.ConnectionId}'").ShouldBe("active");
        ConnectionValue("user_id", $"id = '{connected.Value.ConnectionId}'").ShouldBe(harness.User.Id.ToString());
    }

    // ---------------------------------------------------------------------------------------------------
    // The list: a revoked history row is served without any token column.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>GET /channels</c> serves the caller's revoked history row with its identity (the 10 wave-2 "removed"
    /// rendering), the exact seven members, <c>status = revoked</c> and no token or client-id member.
    /// </summary>
    [Fact]
    public async Task ListChannels_ShowsTheRevokedHistoryRow_WithoutTokenColumns()
    {
        const string activeChannel = "canary-qa-list-active";
        const string revokedChannel = "canary-qa-list-revoked";
        await using var harness = await StartAsync(Database, Ct);
        await ResetAsync(Ct);

        var now = harness.Clock.UtcNow;
        var activeId = await ConnectAsync(harness, harness.User.Id, activeChannel, "access-active", "refresh-active", "client-1", now, Ct);
        var revokedId = await ConnectAsync(harness, harness.User.Id, revokedChannel, "access-revoked", "refresh-revoked", "client-1", now.AddHours(-1), Ct);

        await Database.ExecuteAsync(
            "UPDATE channel_connections SET status = 'revoked', access_token_enc = NULL, refresh_token_enc = NULL, "
            + $"token_expires_at = now(), revoked_at = now() WHERE id = '{revokedId}'",
            Ct);

        using var response = await harness.Client.GetAsync(ChannelsRoute, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"the list answers 200: {body}");

        using var json = JsonDocument.Parse(body);
        json.RootElement.TryGetProperty("items", out var items).ShouldBeTrue("the envelope is { items: [...] }");
        items.GetArrayLength().ShouldBe(2);

        var revokedItem = items.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == revokedId);
        revokedItem.GetProperty("status").GetString().ShouldBe("revoked");
        revokedItem.GetProperty("displayName").GetString().ShouldBe(Title, "the identity stays until the deletion engine purges it");
        revokedItem.GetProperty("externalAccountId").GetString().ShouldBe(revokedChannel);
        items.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == activeId)
            .GetProperty("status").GetString().ShouldBe("active");
        items[0].GetProperty("id").GetGuid().ShouldBe(activeId, "connected_at DESC puts the newest row first");

        foreach (var item in items.EnumerateArray())
        {
            item.EnumerateObject().Select(property => property.Name).Order().ShouldBe(ChannelListMembers);
        }

        body.ShouldNotContain("accessToken", Case.Insensitive);
        body.ShouldNotContain("refreshToken", Case.Insensitive);
        body.ShouldNotContain("oauthClientId", Case.Insensitive);
        body.ShouldNotContain("access-active");
        body.ShouldNotContain("access-revoked");
    }

    // ---------------------------------------------------------------------------------------------------
    // The column widths: an over-long identity fails loudly instead of being truncated.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A channel title over the 200-character column and a scope over the 512-character column are refused by
    /// PostgreSQL at the insert: the connect answers an exception and writes no row, so no truncated title can
    /// ever reach the security e-mail ("an over-long title fails the insert loudly rather than truncating
    /// silently", task file §Failure handling).
    /// </summary>
    [Fact]
    public async Task Connect_OverLongDisplayNameOrScope_FailsLoudlyAndWritesNothing()
    {
        const string channelId = "canary-qa-overlong";
        await using var harness = await StartAsync(Database, Ct, new FakeGoogleOptions { Channel = new GoogleChannel(channelId, Title) });
        await ResetAsync(Ct);

        var now = harness.Clock.UtcNow;
        var overLongTitle = new string('t', 201);
        var overLongScope = new string('s', 513);

        var byTitle = await AttemptPortAsync(
            harness,
            Request(harness.User.Id, channelId, "access-1", "refresh-1", overLongTitle, Scope, "client-1", now),
            Ct);

        AssertLoudWidthRefusal(byTitle, "display_name");
        SqlProbe.Count(Database, $"SELECT count(*) FROM channel_connections WHERE external_account_id = '{channelId}'")
            .ShouldBe(0, "a refused insert leaves nothing behind, truncated or otherwise");

        var byScope = await AttemptPortAsync(
            harness,
            Request(harness.User.Id, channelId, "access-2", "refresh-2", Title, overLongScope, "client-2", now),
            Ct);

        AssertLoudWidthRefusal(byScope, "scope");
        SqlProbe.Count(Database, $"SELECT count(*) FROM channel_connections WHERE external_account_id = '{channelId}'")
            .ShouldBe(0);

        // Positive control: the same writer with an in-width title and scope commits, so the zeros above are the
        // width refusal and not a path that never writes.
        var accepted = await AttemptPortAsync(
            harness,
            Request(harness.User.Id, channelId, "access-3", "refresh-3", Title, Scope, "client-3", now),
            Ct);

        accepted.Succeeded.ShouldBeTrue($"the control connect must be accepted: {accepted.Codes}");
        SqlProbe.Count(Database, $"SELECT count(*) FROM channel_connections WHERE external_account_id = '{channelId}'")
            .ShouldBe(1);
        ConnectionValue("display_name", $"external_account_id = '{channelId}'").ShouldBe(Title);
    }

    // ---------------------------------------------------------------------------------------------------
    // The race, repeated.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Two accounts racing the same channel, repeated over several channels: every round leaves exactly one
    /// active row, the loser answers <c>channel_already_connected</c>, and no round ever throws — a lost race is
    /// a refusal, never a 500.
    /// </summary>
    [Fact]
    public async Task Race_TwoAccountsOnTheSameChannel_NeverThrowsAndCreatesOneRow()
    {
        // The production sender: the queued message is a real `email_messages` row, so "the loser wrote
        // nothing" is asserted on committed state instead of on the sender stand-in's call log.
        await using var harness = await StartAsync(Database, Ct, realEmailSender: true);
        await ResetAsync(Ct);

        var second = await AuthTestFlow.CreateUserAsync(harness.Factory.Services);
        var now = harness.Clock.UtcNow;

        for (var round = 1; round <= RaceRounds; round++)
        {
            var channelId = $"canary-qa-race-{round}";

            var attempts = await Task.WhenAll(
                Task.Run(() => AttemptPortAsync(harness, Request(harness.User.Id, channelId, $"access-a{round}", $"refresh-a{round}", Title, Scope, $"client-a{round}", now), Ct), CancellationToken.None),
                Task.Run(() => AttemptPortAsync(harness, Request(second.Id, channelId, $"access-b{round}", $"refresh-b{round}", Title, Scope, $"client-b{round}", now), Ct), CancellationToken.None));

            foreach (var attempt in attempts)
            {
                attempt.Thrown.ShouldBeNull(
                    $"round {round}: a lost race must be an error answer, never a thrown exception: {attempt.Thrown}");
            }

            attempts.Count(attempt => attempt.Succeeded).ShouldBe(1, $"round {round}: exactly one connect may win");
            attempts.Count(attempt => attempt.Codes.Contains(ErrorCodes.ChannelAlreadyConnected, StringComparison.Ordinal))
                .ShouldBe(
                    1,
                    $"round {round}: the loser answers channel_already_connected, not a 500: "
                    + string.Join(" | ", attempts.Select(attempt => attempt.Codes)));

            SqlProbe.Count(
                    Database,
                    $"SELECT count(*) FROM channel_connections WHERE external_account_id = '{channelId}' AND status = 'active'")
                .ShouldBe(1, $"round {round}: the partial unique index admits one live owner");
            ConnectionValue("oauth_client_id", $"external_account_id = '{channelId}'")
                .ShouldBe(attempts[0].Succeeded ? $"client-a{round}" : $"client-b{round}");
        }

        // One queued message per winning connect and none for a loser: the refusal rolls its unit back whole.
        SqlProbe.Count(Database, "SELECT count(*) FROM email_messages WHERE template = 'channel_connected'")
            .ShouldBe(RaceRounds);
    }

    // ---------------------------------------------------------------------------------------------------
    // The connect e-mail with a Unicode title.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A channel title with Vietnamese diacritics and an emoji travels into the queued message's parameters
    /// verbatim — no mojibake, no escaping surprise in the jsonb column — and carries no token.
    /// </summary>
    [Fact]
    public async Task Callback_EmailParamsCarryAUnicodeChannelTitleVerbatim()
    {
        const string channelId = "canary-qa-unicode-channel";
        const string unicodeTitle = "Kênh Tiếng Việt — Night Tales 🎬";
        var options = new FakeGoogleOptions { Channel = new GoogleChannel(channelId, unicodeTitle) };
        await using var harness = await StartAsync(Database, Ct, options, realEmailSender: true);
        await ResetAsync(Ct);

        var location = await NavigateWithUserAgentAsync(harness, await PrepareAsync(harness, harness.Client, Ct), Ct);

        location.ShouldBe("/channels?connected=youtube");

        var where = $"user_id = '{harness.User.Id}'";
        ConnectionValueOf("email_messages", "template", where).ShouldBe("channel_connected");

        using var parsed = JsonDocument.Parse(ConnectionValueOf("email_messages", "params", where));
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in parsed.RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        values.Keys.Order().ShouldBe(EmailParameterKeys);
        values["channelTitle"].ShouldBe(unicodeTitle, "the title travels verbatim, diacritics and all");
        values["securityLink"].ShouldBe("/account/security");

        var row = RowText("email_messages", where);
        row.ShouldNotContain(harness.Google.IssuedAccessTokens.Single(), Case.Sensitive);
        row.ShouldNotContain(harness.User.Email!, Case.Insensitive);
    }

    // ---------------------------------------------------------------------------------------------------
    // The e-mail body: the channel title is the one stored value the HTML format interpolates, so its
    // escaping is pinned separately from the parameter round-trip above.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The <c>channel_connected</c> HTML body escapes the channel title and the text body keeps it raw: a
    /// title that carries markup can never inject a tag into the security e-mail (09.2.6-T3's renderer).
    /// </summary>
    [Fact]
    public async Task ChannelConnectedEmail_EscapesTheChannelTitleInHtml_AndKeepsItRawInText()
    {
        const string rawTitle = "<b>x</b> & \"q\"";
        var clock = new FakeClock();
        var owner = await Database.CreateUserBuilder().CreateAsync(ct: Ct);
        await using var harness = await EmailHarness.StartAsync(Database, clock, Ct);
        await ResetAsync(Ct);

        await harness.QueueAsync(
            owner.Id,
            "channel_connected",
            Language.Vi,
            EmailHarness.Values(
                ("displayName", "Chủ kênh QA"),
                ("channelTitle", rawTitle),
                ("occurredAt", "2026-10-18 12:00"),
                ("device", "Chrome on macOS"),
                ("securityLink", "/account/security")),
            Ct);

        (await harness.DeliverAsync(Ct)).ShouldBe(1, "the queued row reaches the job and the job sends");

        var message = harness.Smtp.LastMessage;
        message.ShouldNotBeNull();

        var html = message.HtmlBody;
        html.ShouldNotBeNull().ShouldContain(
            WebUtility.HtmlEncode(rawTitle),
            Case.Sensitive,
            "the HTML format escapes the title it interpolates");
        html.ShouldNotContain("<b>x</b>", Case.Sensitive, "no raw markup reaches the HTML body");

        message.TextBody.ShouldNotBeNull().ShouldContain(
            rawTitle,
            Case.Sensitive,
            "the text format is not markup, so it keeps the title verbatim");
    }

    // ---------------------------------------------------------------------------------------------------
    // Helpers (the store suite's shapes, kept local so this file owns its fixtures).
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Starts the world with the production writer registered (the real callback path).</summary>
    private static Task<ConnectChannelHarness> StartAsync(
        TestDatabase database,
        CancellationToken ct,
        FakeGoogleOptions? googleOptions = null,
        bool realEmailSender = false) =>
        ConnectChannelHarness.StartAsync(
            database,
            ct,
            googleOptions: googleOptions,
            flagOn: true,
            channelWriter: ConnectChannelHarness.ConnectChannelWriter.Production,
            realEmailSender: realEmailSender);

    /// <summary>Clears the three tables one test of this class writes.</summary>
    private async Task ResetAsync(CancellationToken ct)
    {
        await Database.ExecuteAsync("DELETE FROM channel_connections", ct);
        await Database.ExecuteAsync("DELETE FROM email_messages", ct);
        await Database.ExecuteAsync("DELETE FROM outbox_messages", ct);
    }

    /// <summary>Runs the connect route, drives the fake's authorize endpoint and returns the callback material.</summary>
    private static async Task<PreparedCallback> PrepareAsync(
        ConnectChannelHarness harness,
        HttpClient client,
        CancellationToken ct)
    {
        using var started = await client.PostAsync(ConnectChannelHarness.ConnectRoute, content: null, ct);
        var connect = await ConnectChannelHarness.ReadConnectAsync(started, ct);
        var code = await harness.AuthorizeAsync(connect.AuthorizationUrl, ct);

        return new PreparedCallback(code, connect.State, connect.NonceCookie);
    }

    /// <summary>Drives the anonymous callback navigation with a real <c>User-Agent</c> and answers its redirect.</summary>
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

    /// <summary>Drives the production writer in its own unit of work, as the callback does, and commits.</summary>
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
            .ConnectAsync(Request(userId, channelId, accessToken, refreshToken, Title, Scope, clientId, now), ct);

        connected.IsError.ShouldBeFalse($"the store writer must accept the connect: {Codes(connected)}");
        (await unitOfWork.CommitAsync(ct)).IsError.ShouldBeFalse("the connection's unit of work must commit");

        return connected.Value.ConnectionId;
    }

    /// <summary>One reconnect of the same channel of <paramref name="harness"/>'s own user, committed at once.</summary>
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
            .ConnectAsync(Request(harness.User.Id, channelId, accessToken, refreshToken, Title, Scope, clientId, now), CancellationToken.None);

        if (connected.IsError)
        {
            return connected.Errors;
        }

        var committed = await unitOfWork.CommitAsync(CancellationToken.None);

        return committed.IsError ? committed.Errors : connected.Value;
    }

    /// <summary>
    /// Runs one <c>ConnectAsync</c> plus its commit in its own scope and answers what happened instead of
    /// throwing, so a race or a width refusal can be asserted on the answer.
    /// </summary>
    private static async Task<PortAttempt> AttemptPortAsync(
        ConnectChannelHarness harness,
        ConnectChannelConnectionRequest request,
        CancellationToken ct)
    {
        try
        {
            await using var scope = harness.Factory.Services.CreateAsyncScope();
            using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(request.UserId);
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await unitOfWork.BeginAsync(ct);

            var connected = await scope
                .ServiceProvider
                .GetRequiredService<IConnectChannelConnection>()
                .ConnectAsync(request, ct);

            if (connected.IsError)
            {
                return new PortAttempt(false, Codes(connected), null);
            }

            var committed = await unitOfWork.CommitAsync(ct);

            return committed.IsError
                ? new PortAttempt(false, Codes(committed), null)
                : new PortAttempt(true, string.Empty, null);
        }
        catch (Exception exception)
        {
            return new PortAttempt(false, "thrown: " + exception.GetType().Name, exception);
        }
    }

    /// <summary>Asserts a width refusal is loud and names the column it was refused for.</summary>
    private static void AssertLoudWidthRefusal(PortAttempt attempt, string column)
    {
        attempt.Thrown.ShouldNotBeNull($"an over-long {column} must fail loudly, never be stored truncated");
        var text = attempt.Thrown!.ToString();
        (text.Contains("22001", StringComparison.Ordinal) || text.Contains("too long", StringComparison.OrdinalIgnoreCase))
            .ShouldBeTrue($"the refusal must be the column width of {column}, not something else: {text}");
    }

    /// <summary>The request the real writer is driven with, as the callback builds it.</summary>
    private static ConnectChannelConnectionRequest Request(
        Guid userId,
        string channelId,
        string accessToken,
        string refreshToken,
        string title,
        string scope,
        string clientId,
        DateTimeOffset now) =>
        new(
            userId,
            Platform.YouTube,
            new GoogleChannel(channelId, title),
            new OAuthTokens(accessToken, refreshToken, now.AddHours(1), scope),
            clientId,
            now);

    /// <summary>The two-URI scope string a real grant carries, and a test may lengthen.</summary>
    private static readonly string Scope = $"{FakeGoogleHosting.UploadScope} {FakeGoogleHosting.ReadonlyScope}";

    /// <summary>The error codes of a failed answer, for an assertion message.</summary>
    private static string Codes<T>(ErrorOr<T> result) =>
        string.Join(", ", result.Errors.Select(error => error.Code));

    /// <summary>One column of one <c>channel_connections</c> row.</summary>
    private string ConnectionValue(string column, string where) =>
        SqlProbe.ScalarText(Database, $"SELECT {column} FROM channel_connections WHERE {where}");

    /// <summary>One column of one row of any table of this schema.</summary>
    private string ConnectionValueOf(string table, string column, string where) =>
        SqlProbe.ScalarText(Database, $"SELECT {column} FROM {table} WHERE {where}");

    /// <summary>The whole row named by its <c>user_id</c>, as JSON text, so a grep over it proves absence.</summary>
    private string RowText(string table, string where) =>
        SqlProbe.ScalarText(Database, $"SELECT to_jsonb(candidate)::text FROM {table} candidate WHERE {where}");

    /// <summary>One <c>timestamptz</c> column as Unix seconds, so an instant compares as an instant.</summary>
    private long ConnectionEpoch(string column, string where) =>
        SqlProbe.Count(Database, $"SELECT extract(epoch from {column})::bigint FROM channel_connections WHERE {where}");

    /// <summary>One connect the callback can finish: the code, its state and the browser nonce cookie.</summary>
    private sealed record PreparedCallback(string Code, string State, string NonceCookie);

    /// <summary>What one port-driven connect answered: the outcome, its error codes, or the exception it threw.</summary>
    private sealed record PortAttempt(bool Succeeded, string Codes, Exception? Thrown);
}
