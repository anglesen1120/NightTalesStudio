using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Publishing.Channels;
using NightTales.Application.Features.Publishing.Channels.Commands.ConnectChannelConnection;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Publishing;

/// <summary>
/// <see cref="ConnectChannelConnectionHandler"/> (06.1.2-T2, 06 README D3/D4/D5/D13/D17, YR11): the insert and
/// the reconnect, the tokens protected under the connection's own purpose, the refusals, and the one
/// <c>channel_connected</c> e-mail every connect queues.
/// </summary>
/// <remarks>
/// <para>
/// The four ports are hand-written stand-ins of this file (09 README D13: no mocking library). The write model
/// is a real <see cref="AppDbContext"/> built on the production options with an <em>unused</em> connection
/// string: the handler only ever calls <c>ChannelConnections.Add</c> and mutates the row the locker answered,
/// so nothing here contacts PostgreSQL — the change tracker is the observable write. The row-level behaviour
/// that needs the database (the unique index, the <c>FOR UPDATE</c> read, the rollback of the caller's unit)
/// belongs to <c>ChannelConnectionStoreTests</c> on real PostgreSQL.
/// </para>
/// <para>
/// No test drives a <c>SaveChanges</c>: the CR-22 contract is that this handler flushes nothing and joins the
/// caller's single commit, so the tracked row and the queued message are the whole observable state.
/// </para>
/// </remarks>
public sealed class ConnectChannelConnectionHandlerTests
{
    /// <summary>The connect moment every request carries.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 30, 0, TimeSpan.Zero);

    /// <summary>The moment a pre-existing row was created; a reconnect must not move <c>connected_at</c> off it.</summary>
    private static readonly DateTimeOffset Earlier = Now.AddDays(-3);

    /// <summary>The access token's expiry the callback relayed.</summary>
    private static readonly DateTimeOffset TokenExpiresAt = Now.AddHours(1);

    /// <summary>The five keys the <c>channel_connected</c> catalog row declares, ascending.</summary>
    private static readonly string[] ExpectedParameterKeys =
        ["channelTitle", "device", "displayName", "occurredAt", "securityLink"];

    private const string ChannelId = "UC_x5XG1OV2P6uZZ5FSM9Ttw";
    private const string Title = "Night Tales";
    private const string UserDisplayName = "Mai";
    private const string UserEmail = "mai@example.test";
    private const string AccessToken = "ya29.canary-access-token";
    private const string RefreshToken = "1//canary-refresh-token";
    private const string Scope =
        "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly";
    private const string ClientId = "client-a.apps.googleusercontent.com";
    private const string OtherClientId = "client-retired.apps.googleusercontent.com";

    /// <summary>A browser <c>User-Agent</c> with a platform clause, the shape the e-mail summarises.</summary>
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NewRow_InsertsAndQueuesOneEmail()
    {
        using var world = new World();

        var result = await world.Handler.ConnectAsync(world.Request(), Ct);

        result.IsError.ShouldBeFalse($"the connect must be written: {result.FirstError}");

        var outcome = result.Value;
        outcome.Reconnected.ShouldBeFalse("no live row held the channel, so this connect inserted one");
        outcome.ConnectionId.ShouldNotBe(Guid.Empty);

        var written = world.Written().ShouldHaveSingleItem();
        written.Id.ShouldBe(outcome.ConnectionId, "the outcome names the row the callback's audit row points at");
        (written.Id.ToByteArray(bigEndian: true)[6] >> 4).ShouldBe(
            7,
            "D4: the id is the UUIDv7 the handler mints before it protects the tokens under it");
        written.UserId.ShouldBe(world.UserId);
        written.Platform.ShouldBe(Platform.YouTube);
        written.ExternalAccountId.ShouldBe(ChannelId);
        written.DisplayName.ShouldBe(Title);
        written.Status.ShouldBe(ChannelConnectionStatus.Active);
        written.Scope.ShouldBe(Scope);
        written.OAuthClientId.ShouldBe(ClientId, "D5: the client that minted the code is pinned on the row");
        written.TokenExpiresAt.ShouldBe(TokenExpiresAt);
        written.ConnectedAt.ShouldBe(Now);
        written.LastVerifiedAt.ShouldBe(Now);
        written.RevokedAt.ShouldBeNull();

        world.Protector.Calls.Count.ShouldBe(2, "both tokens are protected, and nothing else is");
        written.AccessTokenEnc.ShouldBe(world.Protector.Calls[0].Cipher);
        written.RefreshTokenEnc.ShouldBe(world.Protector.Calls[1].Cipher);

        var change = written.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<ChannelConnectionChanged>();
        change.ConnectionId.ShouldBe(written.Id);
        change.UserId.ShouldBe(world.UserId);
        change.Status.ShouldBe(ChannelConnectionStatus.Active);
        change.OccurredAt.ShouldBe(Now);

        var message = world.Email.Calls.ShouldHaveSingleItem();
        message.UserId.ShouldBe(world.UserId);
        message.Template.ShouldBe(ConnectChannelConnectionHandler.EmailTemplate);
        message.Locale.ShouldBe(Language.Vi, "the locale is the account row's (09 README D16)");
    }

    [Fact]
    public async Task ExistingRow_ReconnectsAndStillQueuesOneEmail()
    {
        using var world = new World();
        var live = Expired(world.UserId);
        world.Locker.Live = live;

        var result = await world.Handler.ConnectAsync(world.Request(), Ct);

        result.IsError.ShouldBeFalse($"the reconnect must be written: {result.FirstError}");

        var outcome = result.Value;
        outcome.ConnectionId.ShouldBe(
            live.Id,
            "YR11: a reconnect writes the same row, so already-stored ciphertext keeps its purpose");
        outcome.Reconnected.ShouldBeTrue("the caller's own live row was updated instead of inserted");

        world.Database.ChangeTracker.Entries<ChannelConnection>().ShouldBeEmpty(
            "a reconnect mutates the locked row; it never adds a second one");

        live.Status.ShouldBe(ChannelConnectionStatus.Active, "a reconnect revives an expired row");
        live.DisplayName.ShouldBe(Title, "the title Google reported this time wins");
        live.OAuthClientId.ShouldBe(ClientId, "YR11: the new code's client is re-pinned over the stored one");
        live.TokenExpiresAt.ShouldBe(TokenExpiresAt);
        live.LastVerifiedAt.ShouldBe(Now);
        live.RevokedAt.ShouldBeNull();
        live.ConnectedAt.ShouldBe(Earlier, "the row is the same row, so connected_at stays where it was");
        live.UserId.ShouldBe(world.UserId);

        world.Protector.Calls.Count.ShouldBe(2);
        world.Protector.Calls[0].Purpose.ShouldBe(
            live.Id.ToString(),
            "D4/YR11: the new ciphertext is protected under the EXISTING connection id");
        world.Protector.Calls[1].Purpose.ShouldBe(live.Id.ToString());
        world.Protector.Calls[0].Plaintext.ShouldBe(AccessToken);
        world.Protector.Calls[1].Plaintext.ShouldBe(RefreshToken);
        live.AccessTokenEnc.ShouldBe(world.Protector.Calls[0].Cipher);
        live.RefreshTokenEnc.ShouldBe(world.Protector.Calls[1].Cipher);

        live.DomainEvents.OfType<ChannelConnectionChanged>().Last().Status.ShouldBe(
            ChannelConnectionStatus.Active,
            "D13: the reconnect publishes its own change row");

        world.Email.Calls.ShouldHaveSingleItem().Template.ShouldBe(
            ConnectChannelConnectionHandler.EmailTemplate,
            "D7: a reconnect is a connect, so it queues the e-mail as well");
    }

    [Theory]
    [InlineData(Language.Vi)]
    [InlineData(Language.En)]
    public async Task UiLocale_ComesFromTheUserRow(Language locale)
    {
        using var world = new World();
        world.Profiles.Profile = world.Profiles.Profile! with { UiLocale = locale };

        var result = await world.Handler.ConnectAsync(world.Request(), Ct);

        result.IsError.ShouldBeFalse();
        world.Email.Calls.ShouldHaveSingleItem().Locale.ShouldBe(
            locale,
            "the e-mail renders in the owner's ui_locale, read from the account row");
    }

    [Fact]
    public async Task BothTokens_AreProtectedUnderTheConnectionsOwnPurposeAndStoredAsCiphertextOnly()
    {
        using var world = new World();

        var result = await world.Handler.ConnectAsync(world.Request(), Ct);

        result.IsError.ShouldBeFalse();
        var row = world.Written().ShouldHaveSingleItem();
        var purpose = row.Id.ToString();

        world.Protector.Calls.Count.ShouldBe(2, "one protect per token — the refresh token is protected too");
        world.Protector.Calls[0].Purpose.ShouldBe(purpose, "D4: the purpose is tokens.v1/{connectionId}");
        world.Protector.Calls[1].Purpose.ShouldBe(purpose);
        world.Protector.Calls[0].Plaintext.ShouldBe(AccessToken);
        world.Protector.Calls[1].Plaintext.ShouldBe(RefreshToken);

        row.AccessTokenEnc.ShouldNotBe(AccessToken, "the column holds the protector's cipher, never the token");
        row.RefreshTokenEnc.ShouldNotBe(RefreshToken);
        row.AccessTokenEnc.ShouldNotBeNull().ShouldNotContain(AccessToken, Case.Sensitive);
        row.RefreshTokenEnc.ShouldNotBeNull().ShouldNotContain(RefreshToken, Case.Sensitive);
    }

    [Fact]
    public async Task LiveRowOfAnotherUser_AnswersChannelAlreadyConnectedAndWritesNothing()
    {
        using var world = new World();
        var otherUser = Guid.CreateVersion7();
        var live = Connected(
            otherUser,
            displayName: "Someone else's channel",
            accessCipher: "cfDJ8-other-access-cipher",
            refreshCipher: "cfDJ8-other-refresh-cipher",
            oauthClientId: OtherClientId);
        var status = live.Status;
        var displayName = live.DisplayName;
        var clientId = live.OAuthClientId;
        var accessCipher = live.AccessTokenEnc;
        var refreshCipher = live.RefreshTokenEnc;
        var lastVerifiedAt = live.LastVerifiedAt;
        world.Locker.Live = live;

        var result = await world.Handler.ConnectAsync(world.Request(), Ct);

        result.IsError.ShouldBeTrue("another account's live row is a refusal, never a second owner");
        result.FirstError.Code.ShouldBe(ErrorCodes.ChannelAlreadyConnected);
        result.FirstError.Type.ShouldBe(ErrorType.Conflict);

        var lookup = world.Locker.Lookups.ShouldHaveSingleItem();
        lookup.Platform.ShouldBe(Platform.YouTube);
        lookup.ExternalAccountId.ShouldBe(ChannelId, "the lookup is the channel, whoever owns it");

        live.Status.ShouldBe(status, "the refusal leaves the other account's row exactly as it was");
        live.DisplayName.ShouldBe(displayName);
        live.OAuthClientId.ShouldBe(clientId);
        live.AccessTokenEnc.ShouldBe(accessCipher);
        live.RefreshTokenEnc.ShouldBe(refreshCipher);
        live.LastVerifiedAt.ShouldBe(lastVerifiedAt);

        world.Protector.Calls.ShouldBeEmpty(
            "the ownership check answers before any token is protected, so nothing was ever re-keyed");
        world.Written().ShouldBeEmpty("the refusal writes no row");
        world.Email.Calls.ShouldBeEmpty("and queues no e-mail");
    }

    [Fact]
    public async Task EmailQueueFailure_IsAnsweredAsAnErrorAndLeavesTheRowToTheCallersRollback()
    {
        using var world = new World();
        world.Email.Failure = Error.Unexpected(ErrorCodes.ServiceUnavailable);

        var result = await world.Handler.ConnectAsync(world.Request(), Ct);

        result.IsError.ShouldBeTrue("a queue failure is an answer, never an exception out of the port");
        result.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
        world.Email.Calls.Count.ShouldBe(1, "the queue was attempted once");

        // The row stays in the caller's unit of work: this handler flushes nothing, so the pipeline's rollback
        // is what removes it (ChannelConnectionStoreTests proves that rollback on real PostgreSQL).
        world.Written().ShouldHaveSingleItem().Status.ShouldBe(ChannelConnectionStatus.Active);
    }

    [Fact]
    public async Task MissingRefreshToken_IsRefusedBeforeAnyWrite()
    {
        using var world = new World();

        var result = await world.Handler.ConnectAsync(world.Request(refreshToken: null), Ct);

        result.IsError.ShouldBeTrue("D2: an active row without a refresh token would stop working within the hour");
        result.FirstError.Code.ShouldBe(ErrorCodes.OauthExchangeFailed);
        world.Locker.Lookups.ShouldBeEmpty("the guard runs before the locked read");
        world.Protector.Calls.ShouldBeEmpty();
        world.Email.Calls.ShouldBeEmpty();
        world.Written().ShouldBeEmpty();
    }

    [Fact]
    public async Task MissingAccount_IsRefusedBeforeAnyWrite()
    {
        using var world = new World();
        world.Profiles.Profile = null;

        var result = await world.Handler.ConnectAsync(world.Request(), Ct);

        result.IsError.ShouldBeTrue("a deleted account cannot own a live connection");
        result.FirstError.Code.ShouldBe(ErrorCodes.OauthExchangeFailed);
        world.Locker.Lookups.ShouldBeEmpty();
        world.Protector.Calls.ShouldBeEmpty();
        world.Email.Calls.ShouldBeEmpty();
        world.Written().ShouldBeEmpty();
    }

    [Fact]
    public async Task Email_ParamsAreExactlyTheFiveDeclaredKeysAndCarryNoToken()
    {
        using var world = new World();

        var result = await world.Handler.ConnectAsync(world.Request(), Ct);

        result.IsError.ShouldBeFalse();
        var message = world.Email.Calls.ShouldHaveSingleItem();
        var row = world.Written().ShouldHaveSingleItem();

        // The catalog row declares exactly these five substitutions: a sixth key, or a missing one, is a
        // template the renderer refuses.
        message.Params.Keys.Order().ShouldBe(ExpectedParameterKeys);
        message.Params["displayName"].ShouldBe(UserDisplayName);
        message.Params["channelTitle"].ShouldBe(Title);
        message.Params["occurredAt"].ShouldBe(Now.ToString("O", CultureInfo.InvariantCulture));
        message.Params["securityLink"].ShouldBe(
            ConnectChannelConnectionHandler.SecurityLink,
            "it is a static path: the template is not token-bearing");

        message.Params.Values.ShouldNotContain(AccessToken);
        message.Params.Values.ShouldNotContain(RefreshToken);
        message.Params.Values.ShouldNotContain(row.AccessTokenEnc!);
        message.Params.Values.ShouldNotContain(row.RefreshTokenEnc!);
    }

    [Theory]
    [InlineData(null, "unknown device")]
    [InlineData("", "unknown device")]
    [InlineData("curl/8.6.0", "curl/8.6.0")]
    [InlineData(
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)")]
    public async Task Email_DeviceSummary_IsThePlatformClauseOrTheNeutralFallback(string? userAgent, string expected)
    {
        using var world = new World();

        var result = await world.Handler.ConnectAsync(
            world.Request(info: new RequestInfo("203.0.113.7", userAgent)),
            Ct);

        result.IsError.ShouldBeFalse();
        world.Email.Calls.ShouldHaveSingleItem().Params["device"].ShouldBe(expected);
    }

    [Fact]
    public async Task Email_DeviceSummary_IsCutShortAtTheLimit()
    {
        // A hostile or merely verbose User-Agent must not grow the message without limit.
        var hostile = new string('a', 400);
        using var world = new World();

        var result = await world.Handler.ConnectAsync(
            world.Request(info: new RequestInfo(null, hostile)),
            Ct);

        result.IsError.ShouldBeFalse();
        var device = world.Email.Calls.ShouldHaveSingleItem().Params["device"];
        device.ShouldEndWith("…");
        device.Length.ShouldBe(120, "the summary is cut at the declared limit, ellipsis included");
        hostile.ShouldStartWith(device[..^1], Case.Sensitive);
    }

    /// <summary>The profile every world's account read answers unless a test replaces it.</summary>
    private static UserProfile Profile(Guid userId) =>
        new(userId, UserDisplayName, UserEmail, Language.Vi, PlatformRole.User, MfaEnabled: false, LastLoginAt: null);

    /// <summary>An <c>expired</c> row of <paramref name="userId"/>: the state a reconnect has to revive.</summary>
    private static ChannelConnection Expired(Guid userId)
    {
        var row = Connected(userId);
        row.MarkExpired(Earlier.AddHours(1));

        return row;
    }

    /// <summary>A row as a previous connect would have left it (the locked read's answer).</summary>
    /// <param name="userId">The owner.</param>
    /// <param name="displayName">The stored title.</param>
    /// <param name="accessCipher">The stored access cipher.</param>
    /// <param name="refreshCipher">The stored refresh cipher.</param>
    /// <param name="oauthClientId">The stored client pin.</param>
    private static ChannelConnection Connected(
        Guid userId,
        string displayName = Title,
        string accessCipher = "cfDJ8-stored-access-cipher",
        string refreshCipher = "cfDJ8-stored-refresh-cipher",
        string oauthClientId = "client-old.apps.googleusercontent.com") =>
        ChannelConnection.Connect(
            Guid.CreateVersion7(),
            userId,
            Platform.YouTube,
            ChannelId,
            displayName,
            accessCipher,
            refreshCipher,
            Earlier.AddHours(1),
            Scope,
            oauthClientId,
            Earlier);

    /// <summary>
    /// One test's world: the four hand-written ports, the tracked write model they write into, and the handler
    /// under test.
    /// </summary>
    private sealed class World : IDisposable
    {
        /// <summary>
        /// The provider options of <c>AddPersistence</c> over an address nothing opens: the handler only adds
        /// one row to the change tracker, so no statement is ever sent.
        /// </summary>
        private const string UnusedConnectionString =
            "Host=127.0.0.1;Port=5432;Database=nts_never_connected;Username=nts;Password=nts";

        public World()
        {
            Profiles.Profile = Profile(UserId);
            Database = TestDbContextFactory.CreateAppContext(UnusedConnectionString, new TestCurrentUser());

            Handler = new ConnectChannelConnectionHandler(
                Locker,
                Database,
                Protector,
                Email,
                Profiles,
                NullLogger<ConnectChannelConnectionHandler>.Instance);
        }

        /// <summary>The account every request belongs to, unless a test says otherwise.</summary>
        public Guid UserId { get; } = Guid.CreateVersion7();

        /// <summary>The locked read the handler decides insert-versus-reconnect on.</summary>
        public FakeChannelConnectionLocker Locker { get; } = new();

        /// <summary>The protector the ciphertext columns come from.</summary>
        public FakeTokenProtector Protector { get; } = new();

        /// <summary>The queue port; it records every message.</summary>
        public RecordingEmailSender Email { get; } = new();

        /// <summary>The account read, for the display name and the locale.</summary>
        public FakeUserProfileReader Profiles { get; } = new();

        /// <summary>The tracked write model of the caller's unit of work.</summary>
        public AppDbContext Database { get; }

        /// <summary>The handler under test, over the ports above.</summary>
        public ConnectChannelConnectionHandler Handler { get; }

        /// <summary>The rows the handler added to the write model — the "was a row written" evidence.</summary>
        public IReadOnlyList<ChannelConnection> Written() =>
            [.. Database.ChangeTracker.Entries<ChannelConnection>().Select(entry => entry.Entity)];

        /// <summary>The request a callback would hand over, with the pieces a test varies.</summary>
        /// <param name="userId">The owner; the world's own account unless a test connects for someone else.</param>
        /// <param name="refreshToken">The granted refresh token; null is the "no offline access" case.</param>
        /// <param name="info">The caller metadata, for the e-mail's device value.</param>
        public ConnectChannelConnectionRequest Request(
            Guid? userId = null,
            string? refreshToken = RefreshToken,
            RequestInfo? info = null) =>
            new(
                userId ?? UserId,
                Platform.YouTube,
                new GoogleChannel(ChannelId, Title),
                new OAuthTokens(AccessToken, refreshToken, TokenExpiresAt, Scope),
                ClientId,
                Now,
                info ?? new RequestInfo("203.0.113.7", BrowserUserAgent));

        public void Dispose() => Database.Dispose();
    }

    /// <summary>One <c>Protect</c> call: what it was asked for and what it answered.</summary>
    /// <param name="Purpose">The purpose suffix the handler passed.</param>
    /// <param name="Plaintext">The value it protected.</param>
    /// <param name="Cipher">The cipher it answered with, as the row should hold it.</param>
    private sealed record Protection(string Purpose, string Plaintext, string Cipher);

    /// <summary>The locked read: it answers the row the test seeded and records every lookup.</summary>
    private sealed class FakeChannelConnectionLocker : IChannelConnectionLocker
    {
        /// <summary>The live row the channel has, or null when it has none.</summary>
        public ChannelConnection? Live { get; set; }

        /// <summary>Every lookup, in arrival order.</summary>
        public List<(Platform Platform, string ExternalAccountId)> Lookups { get; } = [];

        public Task<ChannelConnection?> LockLiveByAccountAsync(
            Platform platform,
            string externalAccountId,
            CancellationToken ct)
        {
            Lookups.Add((platform, externalAccountId));

            return Task.FromResult(Live);
        }

        /// <summary>The connect write never reads by id — that is <c>ITokenService</c>'s read.</summary>
        public Task<ChannelConnection?> LockByIdAsync(Guid connectionId, CancellationToken ct) =>
            throw new NotSupportedException("The connect write reads the channel, never a connection id.");
    }

    /// <summary>
    /// The protector: a stand-in cipher (opaque, per call) plus the record of what was protected under which
    /// purpose, so a column holding the plaintext is a leak rather than a format.
    /// </summary>
    private sealed class FakeTokenProtector : ITokenProtector
    {
        private readonly Dictionary<(string Purpose, string Cipher), string?> _plaintexts = [];

        /// <summary>Every protection, in arrival order.</summary>
        public List<Protection> Calls { get; } = [];

        public string Protect(string purposeSuffix, string plaintext)
        {
            var cipher = $"cfDJ8-{Guid.CreateVersion7():N}";

            Calls.Add(new Protection(purposeSuffix, plaintext, cipher));
            _plaintexts[(purposeSuffix, cipher)] = plaintext;

            return cipher;
        }

        public bool TryUnprotect(
            string purposeSuffix,
            string cipher,
            [NotNullWhen(true)] out string? plaintext) =>
            _plaintexts.TryGetValue((purposeSuffix, cipher), out plaintext);
    }

    /// <summary>The queue port: it records every message and can answer a failure.</summary>
    private sealed class RecordingEmailSender : IEmailSender
    {
        /// <summary>Every queue request, in arrival order.</summary>
        public List<EmailMessage> Calls { get; } = [];

        /// <summary>What the next queue answers; null is the success path.</summary>
        public Error? Failure { get; set; }

        public Task<ErrorOr<Guid>> QueueAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Calls.Add(message);

            if (Failure is { } failure)
            {
                return Task.FromResult<ErrorOr<Guid>>(failure);
            }

            return Task.FromResult<ErrorOr<Guid>>(Guid.CreateVersion7());
        }
    }

    /// <summary>The account read: it answers the profile a test set and counts the reads.</summary>
    private sealed class FakeUserProfileReader : IUserProfileReader
    {
        /// <summary>The profile to answer; null is the deleted-account case.</summary>
        public UserProfile? Profile { get; set; }

        public Task<UserProfile?> GetAsync(Guid userId, CancellationToken ct) => Task.FromResult(Profile);
    }
}
