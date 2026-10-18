using System.Globalization;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NightTales.Api.IntegrationTests.Auth;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Google;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// 06.1.2-T2 §"Token access" over a real PostgreSQL 17 and the real Google client pointed at
/// <see cref="FakeGoogleServer"/>: <c>ITokenService.GetAccessTokenAsync</c> hands out a fresh token without
/// reaching Google, refreshes through the connection's <em>pinned</em> client when fewer than five minutes
/// remain and writes the rotated pair through <c>MarkRefreshed</c>, refuses a revoked or missing row without a
/// Google call, propagates a refused refresh as a typed failure without mutating the row, spends exactly one
/// refresh when callers race, and lets no reader observe a half-written pair while a reconnect is in flight.
/// </summary>
/// <remarks>
/// <para>
/// Every test seeds a real <c>channel_connections</c> row through raw SQL with ciphers produced by the host's
/// own <see cref="ITokenProtector"/> under the per-connection purpose <c>tokens.v1/{connectionId}</c> (06 README
/// D4), then calls the service the way a Worker job does: a fresh scope pinned with
/// <c>ISystemContext.For(userId)</c> <em>before</em> anything resolves the scope's <c>AppDbContext</c>, because
/// <c>ChannelConnectionLocker.LockByIdAsync</c> reads the row under a <c>user_id = @scopeUser</c> predicate and
/// an unpinned scope fails closed.
/// </para>
/// <para>
/// The five names of the task file's Tests table are below; the five-minute boundary, the missing-id case the
/// task folds into the revoked case, and the reconnect interleave are the extra rows its acceptance checks name.
/// The row is read back through raw Npgsql, so the assertions are about committed state rather than a tracked
/// instance of the scope under test.
/// </para>
/// </remarks>
/// <param name="database">The class database, a migrated copy of the run's template.</param>
public sealed class TokenServiceTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The plaintext access token every seed stores (a canary; never a real token).</summary>
    private const string SeededAccessToken = "canary-seeded-access-token";

    /// <summary>The plaintext refresh token every seed stores.</summary>
    private const string SeededRefreshToken = "canary-seeded-refresh-token";

    /// <summary>How many callers race in the single-refresh test.</summary>
    private const int ConcurrentCallers = 4;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetAccessToken_FreshToken_ReturnsWithoutRefresh()
    {
        await using var harness = await TokenServiceHarness.StartAsync(Database, Ct);
        var seeded = await harness.SeedAsync(
            ChannelConnectionStatus.Active,
            SeededAccessToken,
            SeededRefreshToken,
            harness.Clock.UtcNow.AddHours(1),
            harness.Google.Options.ActiveClientId,
            cancellationToken: Ct);

        var result = await harness.GetAccessTokenAsync(seeded.Id, Ct);

        result.IsError.ShouldBeFalse($"a fresh token must be handed out as is: {result.FirstError}");
        result.Value.AccessToken.ShouldBe(SeededAccessToken);
        result.Value.ExpiresAt.ShouldBe(seeded.TokenExpiresAt);
        harness.Google.TokenRequestCount.ShouldBe(0, "a token with more than five minutes left never reaches Google");

        var row = await harness.RowAsync(seeded.Id, Ct);
        row.Status.ShouldBe("active");
        row.AccessTokenEnc.ShouldBe(
            seeded.AccessTokenEnc,
            "the protector's cipher is randomized, so an unchanged cipher proves nothing wrote the row");
    }

    [Fact]
    public async Task GetAccessToken_NearExpiry_RefreshesAndCallsMarkRefreshed()
    {
        await using var harness = await TokenServiceHarness.StartAsync(Database, Ct);

        // The connection is pinned to the second client while the host's active client is the primary one: the
        // refresh must use the pinned client (06 README D5/YR11), not whatever is active right now.
        var pinnedClientId = harness.Google.Options.SecondClientId;
        var seeded = await harness.SeedAsync(
            ChannelConnectionStatus.Expired,
            SeededAccessToken,
            SeededRefreshToken,
            harness.Clock.UtcNow.AddMinutes(1),
            pinnedClientId,
            lastVerifiedAt: harness.Clock.UtcNow.AddDays(-1),
            cancellationToken: Ct);

        var result = await harness.GetAccessTokenAsync(seeded.Id, Ct);

        result.IsError.ShouldBeFalse($"a refreshable connection must answer a token: {result.FirstError}");
        var issued = harness.Google.IssuedAccessTokens;
        issued.Count.ShouldBe(1);
        result.Value.AccessToken.ShouldBe(issued[0]);
        result.Value.ExpiresAt.ShouldBe(
            harness.Clock.UtcNow.AddSeconds(3600),
            "the expiry is the one the token answer named, not the seeded one");

        harness.Google.TokenRequestCount.ShouldBe(1);
        var refresh = harness.Google.Calls.Single(call => call.Is("POST", FakeGoogleServer.TokenPath));
        refresh.FormValue("grant_type").ShouldBe("refresh_token");
        refresh.FormValue("client_id").ShouldBe(pinnedClientId, "the pinned client mints every later refresh");
        refresh.FormValue("client_id").ShouldNotBe(
            harness.Google.Options.ActiveClientId,
            "the active client of the host is irrelevant to a pinned connection");
        refresh.FormValue("refresh_token").ShouldBe(SeededRefreshToken, "the stored grant is what the refresh presents");

        // MarkRefreshed's contract (ChannelConnection.MarkRefreshed): active, the rotated pair under the same
        // per-connection purpose, last_verified_at moved, revoked_at cleared, token_expires_at the new instant.
        var row = await harness.RowAsync(seeded.Id, Ct);
        row.Status.ShouldBe("active", "a successful refresh returns the row to active");
        // Only Revoke sets revoked_at and it always sets the status to revoked, so a refreshable row can only
        // carry null here; the point of the assertion is that MarkRefreshed's own contract (tokens, status,
        // last_verified_at, expiry) was applied by the port and not by a bare column write.
        row.RevokedAt.ShouldBeNull();
        row.LastVerifiedAt.ShouldBe(harness.Clock.UtcNow);
        row.TokenExpiresAt.ShouldBe(result.Value.ExpiresAt);
        row.RefreshTokenEnc.ShouldNotBe(seeded.RefreshTokenEnc);

        harness.Protector.TryUnprotect(seeded.Id.ToString(), row.AccessTokenEnc!, out var access).ShouldBeTrue();
        access.ShouldBe(issued[0]);
        harness.Protector.TryUnprotect(seeded.Id.ToString(), row.RefreshTokenEnc!, out var refreshPlain).ShouldBeTrue();
        refreshPlain.ShouldBe(
            issued[0].Replace("access", "refresh", StringComparison.Ordinal),
            "Google usually rotates the refresh token too, and the rotated one replaces the stored grant");
    }

    [Fact]
    public async Task GetAccessToken_FiveMinuteBoundary_IsFreshAtTheWindowAndRefreshesBelowIt()
    {
        await using var harness = await TokenServiceHarness.StartAsync(Database, Ct);

        // The design (06.1.2-T2 §"Token access"): `TokenExpiresAt >= now + 5 min` hands out the stored token,
        // anything below refreshes. The comparison is inclusive, so exactly five minutes is still fresh.
        var atWindow = await harness.SeedAsync(
            ChannelConnectionStatus.Active,
            SeededAccessToken,
            SeededRefreshToken,
            harness.Clock.UtcNow.AddMinutes(5),
            harness.Google.Options.ActiveClientId,
            cancellationToken: Ct);

        var atWindowResult = await harness.GetAccessTokenAsync(atWindow.Id, Ct);

        atWindowResult.IsError.ShouldBeFalse($"exactly five minutes left must not fail: {atWindowResult.FirstError}");
        atWindowResult.Value.AccessToken.ShouldBe(SeededAccessToken);
        harness.Google.TokenRequestCount.ShouldBe(0, "exactly five minutes left is >= the window, so it is fresh");

        var belowWindow = await harness.SeedAsync(
            ChannelConnectionStatus.Active,
            SeededAccessToken,
            SeededRefreshToken,
            harness.Clock.UtcNow.AddMinutes(5).AddSeconds(-1),
            harness.Google.Options.ActiveClientId,
            cancellationToken: Ct);

        var belowWindowResult = await harness.GetAccessTokenAsync(belowWindow.Id, Ct);

        belowWindowResult.IsError.ShouldBeFalse($"one second under the window must refresh: {belowWindowResult.FirstError}");
        belowWindowResult.Value.AccessToken.ShouldBe(harness.Google.IssuedAccessTokens.Single());
        harness.Google.TokenRequestCount.ShouldBe(1, "one second under the window refreshes");
    }

    [Fact]
    public async Task GetAccessToken_RevokedConnection_FailsWithoutGoogleCall()
    {
        await using var harness = await TokenServiceHarness.StartAsync(Database, Ct);
        var revoked = await harness.SeedAsync(
            ChannelConnectionStatus.Revoked,
            accessToken: null,
            refreshToken: null,
            harness.Clock.UtcNow.AddHours(1),
            harness.Google.Options.ActiveClientId,
            revokedAt: harness.Clock.UtcNow,
            cancellationToken: Ct);

        var refused = await harness.GetAccessTokenAsync(revoked.Id, Ct);

        refused.IsError.ShouldBeTrue("a revoked connection is a dead end");
        refused.FirstError.Code.ShouldBe(ErrorCodes.ChannelReauthRequired);

        // An id that is not this scope's row at all is the same answer — never a Google call, never an oracle
        // that tells a revoked row from an unknown one.
        var missing = await harness.GetAccessTokenAsync(Guid.CreateVersion7(), Ct);

        missing.IsError.ShouldBeTrue("a missing connection is a dead end");
        missing.FirstError.Code.ShouldBe(ErrorCodes.ChannelReauthRequired);

        // A dead end never writes either: the revoked row keeps its own shape rather than being touched by the
        // failed read (`Status` stays revoked and both token columns stay NULL, `Revoke`'s own shape).
        var row = await harness.RowAsync(revoked.Id, Ct);
        row.Status.ShouldBe("revoked");
        row.AccessTokenEnc.ShouldBeNull();
        row.RefreshTokenEnc.ShouldBeNull();
        row.RevokedAt.ShouldBe(harness.Clock.UtcNow);
        harness.Google.Calls.ShouldBeEmpty("neither dead end may reach Google");
    }

    [Fact]
    public async Task GetAccessToken_ConcurrentCallers_RefreshOnce()
    {
        await using var harness = await TokenServiceHarness.StartAsync(Database, Ct);
        var seeded = await harness.SeedAsync(
            ChannelConnectionStatus.Expired,
            SeededAccessToken,
            SeededRefreshToken,
            harness.Clock.UtcNow.AddMinutes(1),
            harness.Google.Options.ActiveClientId,
            cancellationToken: Ct);

        var scopes = new List<IServiceScope>(ConcurrentCallers);
        var pins = new List<IDisposable>(ConcurrentCallers);
        var calls = new List<Task<ErrorOr<ChannelAccessToken>>>(ConcurrentCallers);

        for (var i = 0; i < ConcurrentCallers; i++)
        {
            var scope = harness.NewScopeWithPinnedUser(out var pin);
            scopes.Add(scope);
            pins.Add(pin);
            calls.Add(scope.ServiceProvider.GetRequiredService<ITokenService>().GetAccessTokenAsync(seeded.Id, Ct));
        }

        var results = await Task.WhenAll(calls);

        foreach (var pin in pins)
        {
            pin.Dispose();
        }

        foreach (var scope in scopes)
        {
            scope.Dispose();
        }

        results.ShouldAllBe(result => !result.IsError, "every caller must be answered a token");
        var token = results[0].Value.AccessToken;
        results.ShouldAllBe(result => result.Value.AccessToken == token, "all callers see the one refreshed token");
        harness.Google.TokenRequestCount.ShouldBe(
            1,
            "the row lock is held across the Google call, so one expiry spends exactly one refresh");

        var row = await harness.RowAsync(seeded.Id, Ct);
        row.Status.ShouldBe("active");
        harness.Protector.TryUnprotect(seeded.Id.ToString(), row.AccessTokenEnc!, out var persisted).ShouldBeTrue();
        persisted.ShouldBe(token, "the single persisted pair is the one every caller was handed");
    }

    [Fact]
    public async Task GetAccessToken_InvalidGrant_PropagatesTypedFailure()
    {
        await using var harness = await TokenServiceHarness.StartAsync(Database, Ct);

        // Google refuses the refresh; the fake answers the 400 `invalid_grant` Google would.
        harness.Google.Options.TokenError = "invalid_grant";

        var verifiedAt = harness.Clock.UtcNow.AddDays(-2);
        var seeded = await harness.SeedAsync(
            ChannelConnectionStatus.Expired,
            SeededAccessToken,
            SeededRefreshToken,
            harness.Clock.UtcNow.AddMinutes(1),
            harness.Google.Options.ActiveClientId,
            lastVerifiedAt: verifiedAt,
            cancellationToken: Ct);

        var refused = await harness.GetAccessTokenAsync(seeded.Id, Ct);

        refused.IsError.ShouldBeTrue("a refused refresh is a typed failure, never a token");
        refused.FirstError.Code.ShouldBe(
            ErrorCodes.OauthExchangeFailed,
            "a 4xx token answer travels as the client's own typed failure, `invalid_grant` included");
        harness.Google.TokenRequestCount.ShouldBe(1, "the refusal is Google's, so the refresh was attempted");

        // The port never mutates on failure: the caller (06.4.2-T1) owns the status transition.
        var row = await harness.RowAsync(seeded.Id, Ct);
        row.Status.ShouldBe("expired", "a refused refresh must not move the status");
        row.AccessTokenEnc.ShouldBe(seeded.AccessTokenEnc);
        row.RefreshTokenEnc.ShouldBe(seeded.RefreshTokenEnc);
        row.TokenExpiresAt.ShouldBe(seeded.TokenExpiresAt);
        row.LastVerifiedAt.ShouldBe(verifiedAt);
    }

    [Fact]
    public async Task GetAccessToken_InterleavedWithReconnect_ReadsOnlyACommittedPair()
    {
        await using var harness = await TokenServiceHarness.StartAsync(Database, Ct);
        var seeded = await harness.SeedAsync(
            ChannelConnectionStatus.Expired,
            SeededAccessToken,
            SeededRefreshToken,
            harness.Clock.UtcNow.AddMinutes(1),
            harness.Google.Options.ActiveClientId,
            cancellationToken: Ct);

        const string reconnectAccess = "canary-reconnect-access-token";
        const string reconnectRefresh = "canary-reconnect-refresh-token";
        var reconnectExpiry = harness.Clock.UtcNow.AddHours(1);

        // The writer takes the row lock, writes the reconnect's pair and keeps the unit of work open: the pair is
        // invisible to every other session until the commit below, and the lock is what parks the reader instead
        // of letting it read the row mid-write.
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = harness.ReconnectHoldingTheLockAsync(
            seeded,
            reconnectAccess,
            reconnectRefresh,
            reconnectExpiry,
            locked,
            release,
            Ct);

        try
        {
            await locked.Task.WaitAsync(Ct);

            var reader = harness.GetAccessTokenAsync(seeded.Id, Ct);

            // Deterministic, and the lock regression detector: the reader's locked read is parked in
            // pg_stat_activity on the writer's row lock. Without that lock it would find the row free and return
            // within milliseconds instead of queueing.
            await WaitForTheReaderToQueueOnTheRowLockAsync(harness.Database, Ct);
            reader.IsCompleted.ShouldBeFalse(
                "the reader must wait for the writer's row lock, never read the pair mid-write");

            release.TrySetResult();

            await writer.WaitAsync(Ct);
            var result = await reader;

            result.IsError.ShouldBeFalse($"the reader waits for the lock and re-reads a committed row: {result.FirstError}");
            result.Value.AccessToken.ShouldBe(
                reconnectAccess,
                "the reconnect committed first, so the reader reads the reconnect's token and not the seeded one");
            result.Value.ExpiresAt.ShouldBe(reconnectExpiry);
            harness.Google.TokenRequestCount.ShouldBe(
                0,
                "the reconnect's pair is fresh, so the reader that waited must not spend a refresh of its own");

            SqlProbe.Count(harness.Database, $"SELECT count(*) FROM channel_connections WHERE id = '{seeded.Id}'")
                .ShouldBe(1L, "both writers target one row; neither of them inserts a second");

            var row = await harness.RowAsync(seeded.Id, Ct);
            row.Status.ShouldBe("active");
            row.TokenExpiresAt.ShouldBe(reconnectExpiry);
            harness.Protector.TryUnprotect(seeded.Id.ToString(), row.AccessTokenEnc!, out var access).ShouldBeTrue();
            harness.Protector.TryUnprotect(seeded.Id.ToString(), row.RefreshTokenEnc!, out var refresh).ShouldBeTrue();

            // The committed pair is the reconnect's from end to end: it never mixes with the pair the seed wrote.
            access.ShouldBe(reconnectAccess);
            refresh.ShouldBe(reconnectRefresh);
            row.RefreshTokenEnc.ShouldNotBe(seeded.RefreshTokenEnc);
        }
        finally
        {
            // A failed assertion above must not leave the writer holding the row lock.
            release.TrySetResult();
        }
    }

    /// <summary>
    /// Waits, with a bounded real-time deadline (never the fake clock), for a session to be parked in
    /// <c>pg_stat_activity</c> on a lock while running a <c>channel_connections</c> statement — the reader of the
    /// interleave test queued behind the reconnect's row lock. A row lock that is never taken is exactly the case
    /// that makes this time out, so the poll doubles as the regression check.
    /// </summary>
    /// <param name="database">The class database, whose sessions the probe reads.</param>
    /// <param name="cancellationToken">Cancels the bounded wait.</param>
    private static async Task WaitForTheReaderToQueueOnTheRowLockAsync(
        TestDatabase database,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var waiting = SqlProbe.Count(
                database,
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' "
                + "AND datname = current_database() AND query LIKE '%channel_connections%'");
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(20, cancellationToken);
        }

        throw new TimeoutException("The reader never queued on the reconnect's channel_connections row lock.");
    }

    /// <summary>
    /// One 06.1.2-T2 token world: the real Api host (its <see cref="ITokenService"/>, row lock, token
    /// protector and Google client) pointed at a <see cref="FakeGoogleServer"/>, plus the raw seeding and
    /// reading a token test needs.
    /// </summary>
    /// <remarks>
    /// The service is resolved from a scope pinned with <c>ISystemContext.For(userId)</c> exactly as a Worker
    /// job pins its scope before it resolves anything that touches the <c>AppDbContext</c> — the same shape
    /// <see cref="Email.EmailHarness"/> uses.
    /// </remarks>
    private sealed class TokenServiceHarness : IAsyncDisposable
    {
        private readonly ApiFactory _factory;

        private TokenServiceHarness(ApiFactory factory, FakeGoogleServer google, FakeClock clock, TestDatabase database, Guid userId)
        {
            _factory = factory;
            Google = google;
            Clock = clock;
            Database = database;
            UserId = userId;
        }

        /// <summary>The fake Google listener the host's <c>YouTube</c> endpoints point at.</summary>
        internal FakeGoogleServer Google { get; }

        /// <summary>The host's clock; the seeded expiries are relative to it.</summary>
        internal FakeClock Clock { get; }

        /// <summary>The class database, for the raw row reads.</summary>
        internal TestDatabase Database { get; }

        /// <summary>The owner of every seeded row, and the id every scope is pinned to.</summary>
        internal Guid UserId { get; }

        /// <summary>The host's protector, for writing ciphers under a connection's own purpose.</summary>
        internal ITokenProtector Protector => _factory.Services.GetRequiredService<ITokenProtector>();

        /// <summary>Starts the world: an empty <c>channel_connections</c>, the fake Google, the host, one user.</summary>
        /// <param name="database">The class database.</param>
        /// <param name="cancellationToken">Cancels the setup.</param>
        internal static async Task<TokenServiceHarness> StartAsync(TestDatabase database, CancellationToken cancellationToken)
        {
            var clock = new FakeClock();

            // The class shares one database; every token test seeds its own row, so the table starts empty and
            // the unique index over (platform, external_account_id) can never carry a sibling's row.
            await database.ExecuteAsync("DELETE FROM channel_connections", cancellationToken);

            var google = await FakeGoogleServer.StartAsync(cancellationToken: cancellationToken);
            var factory = new ApiFactory(database, clock).WithFakeGoogle(google);
            var user = await AuthTestFlow.CreateUserAsync(factory.Services);

            return new TokenServiceHarness(factory, google, clock, database, user.Id);
        }

        /// <summary>
        /// Inserts one real <c>channel_connections</c> row whose token columns are protected under the row's own
        /// purpose <c>tokens.v1/{id}</c>; null plaintexts write a null cipher, which is a revoked row's shape.
        /// </summary>
        /// <param name="status">The row's state.</param>
        /// <param name="accessToken">The plaintext access token, or null (a revoked row).</param>
        /// <param name="refreshToken">The plaintext refresh token, or null (a revoked row).</param>
        /// <param name="tokenExpiresAt">When the access token stops working.</param>
        /// <param name="oauthClientId">The client the connection is pinned to.</param>
        /// <param name="lastVerifiedAt">The stored verification instant; null is the never-verified shape.</param>
        /// <param name="revokedAt">The disconnect instant, or null for a live row.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        internal async Task<SeededConnection> SeedAsync(
            ChannelConnectionStatus status,
            string? accessToken,
            string? refreshToken,
            DateTimeOffset tokenExpiresAt,
            string oauthClientId,
            DateTimeOffset? lastVerifiedAt = null,
            DateTimeOffset? revokedAt = null,
            CancellationToken cancellationToken = default)
        {
            var id = Guid.CreateVersion7();
            var externalAccountId = $"canary-channel-{id:N}";
            var scope = $"{FakeGoogleHosting.UploadScope} {FakeGoogleHosting.ReadonlyScope}";
            var accessCipher = accessToken is null ? null : Protector.Protect(id.ToString(), accessToken);
            var refreshCipher = refreshToken is null ? null : Protector.Protect(id.ToString(), refreshToken);

            await Database.ExecuteAsync(
                "INSERT INTO channel_connections "
                + "(id, user_id, platform, external_account_id, display_name, status, access_token_enc, "
                + "refresh_token_enc, token_expires_at, scope, oauth_client_id, last_verified_at, connected_at, revoked_at) "
                + "VALUES ("
                + $"{SqlText(id.ToString())}, {SqlText(UserId.ToString())}, 'youtube', {SqlText(externalAccountId)}, "
                + $"{SqlText("NightTales Canary Channel")}, {SqlText(Wire(status))}, {SqlText(accessCipher)}, "
                + $"{SqlText(refreshCipher)}, {SqlInstant(tokenExpiresAt)}, {SqlText(scope)}, {SqlText(oauthClientId)}, "
                + $"{SqlInstant(lastVerifiedAt)}, {SqlInstant(Clock.UtcNow.AddDays(-10))}, {SqlInstant(revokedAt)})",
                cancellationToken);

            return new SeededConnection(
                id,
                externalAccountId,
                scope,
                accessCipher,
                refreshCipher,
                tokenExpiresAt);
        }

        /// <summary>Calls <c>GetAccessTokenAsync</c> from a fresh scope pinned to <see cref="UserId"/>.</summary>
        /// <param name="connectionId">The row to read.</param>
        /// <param name="cancellationToken">Cancels the call.</param>
        internal async Task<ErrorOr<ChannelAccessToken>> GetAccessTokenAsync(Guid connectionId, CancellationToken cancellationToken)
        {
            var scope = NewScopeWithPinnedUser(out var pin);

            try
            {
                return await scope.ServiceProvider
                    .GetRequiredService<ITokenService>()
                    .GetAccessTokenAsync(connectionId, cancellationToken);
            }
            finally
            {
                pin.Dispose();
                scope.Dispose();
            }
        }

        /// <summary>
        /// Writes a reconnect to <paramref name="seed"/>'s row the way the connect writer does — a unit of work
        /// around the account's locked read and one <c>Reconnect</c> — and then <em>holds</em> the row lock: the
        /// unit stays uncommitted until <paramref name="release"/> is set, so the pair it wrote is invisible to
        /// every other session and a reader that touches the row can only queue behind it.
        /// </summary>
        /// <param name="seed">The row to reconnect.</param>
        /// <param name="accessToken">The plaintext access token the reconnect stores.</param>
        /// <param name="refreshToken">The plaintext refresh token the reconnect stores.</param>
        /// <param name="tokenExpiresAt">The new expiry — fresh, so the reader that waited needs no Google call.</param>
        /// <param name="locked">
        /// Completed once the row lock is held and the new pair is written, still uncommitted: the signal that a
        /// reader started afterwards can only queue.
        /// </param>
        /// <param name="release">Completed by the test to let the reconnect commit and drop the lock.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        internal async Task ReconnectHoldingTheLockAsync(
            SeededConnection seed,
            string accessToken,
            string refreshToken,
            DateTimeOffset tokenExpiresAt,
            TaskCompletionSource locked,
            TaskCompletionSource release,
            CancellationToken cancellationToken)
        {
            var scope = NewScopeWithPinnedUser(out var pin);

            try
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var locker = scope.ServiceProvider.GetRequiredService<IChannelConnectionLocker>();

                await unitOfWork.BeginAsync(cancellationToken);
                var row = await locker.LockLiveByAccountAsync(Platform.YouTube, seed.ExternalAccountId, cancellationToken);
                row.ShouldNotBeNull("the reconnect's locked read must find the seeded live row");

                var purpose = row!.Id.ToString();
                row.Reconnect(
                    "NightTales Canary Channel (reconnected)",
                    Protector.Protect(purpose, accessToken),
                    Protector.Protect(purpose, refreshToken),
                    tokenExpiresAt,
                    seed.Scope,
                    row.OAuthClientId,
                    Clock.UtcNow);

                // The row lock is held and the new pair is tracked but uncommitted: from here on the caller of
                // this method owns the moment that releases both.
                locked.SetResult();

                await release.Task.WaitAsync(cancellationToken);

                var committed = await unitOfWork.CommitAsync(cancellationToken);
                committed.IsError.ShouldBeFalse($"the reconnect must commit: {committed.FirstError}");
            }
            finally
            {
                pin.Dispose();
                scope.Dispose();
            }
        }

        /// <summary>A fresh scope whose <c>AppDbContext</c> filter is already pinned to <see cref="UserId"/>.</summary>
        /// <param name="pin">The pin handle, which the caller disposes with the scope.</param>
        internal IServiceScope NewScopeWithPinnedUser(out IDisposable pin)
        {
            var scope = _factory.Services.CreateScope();
            pin = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(UserId);

            return scope;
        }

        /// <summary>Reads the committed row back through raw Npgsql (never a tracked instance of a test scope).</summary>
        /// <param name="connectionId">The row to read.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        internal async Task<ChannelRow> RowAsync(Guid connectionId, CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand(
                "SELECT status, access_token_enc, refresh_token_enc, token_expires_at, last_verified_at, "
                + "revoked_at FROM channel_connections WHERE id = @id",
                connection);
            command.Parameters.AddWithValue("id", connectionId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            (await reader.ReadAsync(cancellationToken)).ShouldBeTrue("the seeded connection row must exist");

            return new ChannelRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5));
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _factory.DisposeAsync();
            await Google.DisposeAsync();
        }

        /// <summary>The enum member's lower-case wire value, the shape <c>HasEnumCheck</c> guards.</summary>
        private static string Wire(ChannelConnectionStatus status) => status.ToString().ToLowerInvariant();

        /// <summary>A SQL text literal, or <c>NULL</c>; the seed values are canaries and base64 ciphers.</summary>
        private static string SqlText(string? value) =>
            value is null ? "NULL" : $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

        /// <summary>A SQL <c>timestamptz</c> literal, or <c>NULL</c>.</summary>
        private static string SqlInstant(DateTimeOffset? value) =>
            value is { } instant
                ? $"'{instant.ToString("O", CultureInfo.InvariantCulture)}'::timestamptz"
                : "NULL";
    }

    /// <summary>What <see cref="TokenServiceHarness.SeedAsync"/> inserted, so a test can compare against it.</summary>
    /// <param name="Id">The row's id (also its protection purpose).</param>
    /// <param name="ExternalAccountId">Google's channel id of the row.</param>
    /// <param name="Scope">The granted scope string.</param>
    /// <param name="AccessTokenEnc">The access cipher, or null.</param>
    /// <param name="RefreshTokenEnc">The refresh cipher, or null.</param>
    /// <param name="TokenExpiresAt">The expiry the row was written with.</param>
    private sealed record SeededConnection(
        Guid Id,
        string ExternalAccountId,
        string Scope,
        string? AccessTokenEnc,
        string? RefreshTokenEnc,
        DateTimeOffset TokenExpiresAt);

    /// <summary>One committed <c>channel_connections</c> row, read through raw Npgsql.</summary>
    /// <param name="Status">The status column's wire value.</param>
    /// <param name="AccessTokenEnc">The access cipher, or null.</param>
    /// <param name="RefreshTokenEnc">The refresh cipher, or null.</param>
    /// <param name="TokenExpiresAt">The token expiry.</param>
    /// <param name="LastVerifiedAt">The last verification instant, or null.</param>
    /// <param name="RevokedAt">The disconnect instant, or null.</param>
    private sealed record ChannelRow(
        string Status,
        string? AccessTokenEnc,
        string? RefreshTokenEnc,
        DateTimeOffset TokenExpiresAt,
        DateTimeOffset? LastVerifiedAt,
        DateTimeOffset? RevokedAt);
}
