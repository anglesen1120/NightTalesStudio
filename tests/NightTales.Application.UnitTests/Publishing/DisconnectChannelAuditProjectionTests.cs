using System.Globalization;
using System.Reflection;
using ErrorOr;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Features.Compliance.Deletion;
using NightTales.Application.Features.Publishing.Channels.Commands.DisconnectChannel;
using NightTales.Application.Telemetry;
using NightTales.Domain.Audit;
using NightTales.Domain.Compliance.Deletion;
using NightTales.Domain.Publishing;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Publishing;

/// <summary>
/// The audit projection and the log contract of a disconnect (06.4.1-T1 task line 99, 06 README D17, plan §3.3):
/// <c>data</c> is the connection's ids, the two cascade counts and Google's revoke answer — never a token, the
/// channel title or any other free text — and the handler's every line is one of its five templated ids-only
/// messages.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the projection half needs no database.</b> <see cref="DisconnectChannelCommand.ToAuditData"/> is a pure
/// function of the handler's outcome, which is what the pipeline's <c>AuditBehavior</c> writes; asserting it here is
/// the metadata half of the acceptance check and the row itself belongs to the Api integration tests. The ledger,
/// the deletion request and the Google client are hand-written stand-ins (09 README D13: no mocking library).
/// </para>
/// <para>
/// <b>Why the repeat path is the runnable one.</b> A revoked connection is answered before any statement: the locked
/// read answers the row, the deletion request is scheduled again and the handler returns. That is the one path a
/// real <see cref="AppDbContext"/> is not needed for (the write model below points at a database nothing opens), and
/// it is exactly the path that must write no audit row — the repeat of a lost response changed nothing.
/// </para>
/// </remarks>
public sealed class DisconnectChannelAuditProjectionTests
{
    /// <summary>The frozen instant every test's clock reads.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);

    private const string ChannelId = "UC_x5XG1OV2P6uZZ5FSM9Ttw";
    private const string Title = "Night Tales";
    private const string ClientId = "client-a.apps.googleusercontent.com";
    private const string AccessCipher = "cfDJ8-access-token-ciphertext";
    private const string RefreshCipher = "cfDJ8-refresh-token-ciphertext";

    /// <summary>The seven keys the frozen projection carries (06 README D17; 09.3.6-T2 keeps them stable).</summary>
    private static readonly string[] ExpectedKeys =
    [
        "channelId",
        "externalAccountId",
        "googleRevoke",
        "oauthClientId",
        "platform",
        "publicationsCancelled",
        "quotaUnitsReleased",
    ];

    /// <summary>The four values of <c>data.googleRevoke</c> (06.4.1-T1 §Design steps 3-4).</summary>
    private static readonly string[] GoogleRevokeCodes = ["revoked", "alreadyRevoked", "failed", "skipped"];

    /// <summary>
    /// The only parameter types a disconnect log line may interpolate: an id, a cascade count or one frozen code.
    /// A title, a token or an address has no such type, which is what keeps D17 true by construction.
    /// </summary>
    private static readonly Type[] AllowedLogParameterTypes = [typeof(Guid), typeof(int), typeof(string)];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void DisconnectCommand_IsAuditedUnderTheFrozenAction()
    {
        // The endpoint and the pipeline agree on one action string: AuditBehavior writes the row from the attribute,
        // so a typo here would rename the audit trail of every disconnect.
        var audited = typeof(DisconnectChannelCommand).GetCustomAttribute<AuditedAttribute>().ShouldNotBeNull();

        audited.Action.ShouldBe("publishing.channel_disconnected");
        audited.Category.ShouldBe(Category.Publishing);
        audited.Visibility.ShouldBe(Visibility.User);
    }

    [Fact]
    public void Disconnect_AuditProjection_ContainsIdsOnly()
    {
        // 06 README D17/BD16: the projection is the connection's id, the two identities, the two cascade counts and
        // Google's answer — seven values, each an id, a count or a frozen code. Nothing the owner or a publication
        // typed can reach it.
        var outcome = new DisconnectedChannelOutcome(
            ConnectionId: Guid.CreateVersion7(),
            Platform: "youtube",
            OAuthClientId: ClientId,
            ExternalAccountId: ChannelId,
            PublicationsCancelled: 2,
            QuotaUnitsReleased: 1,
            GoogleRevoke: "revoked");

        var command = new DisconnectChannelCommand(outcome.ConnectionId) { Outcome = outcome };

        var data = command.ToAuditData();

        data.ShouldNotBeNull();
        data.SubjectUserId.ShouldBeNull("the row is the connection's, and the owner is on the business row");
        data.EntityType.ShouldBe("channel_connection");
        data.EntityId.ShouldBe(outcome.ConnectionId);
        data.Summary.Keys.OrderBy(key => key, StringComparer.Ordinal).ShouldBe(ExpectedKeys);
        data.Summary["channelId"].ShouldBe(outcome.ConnectionId.ToString("D", CultureInfo.InvariantCulture));
        data.Summary["platform"].ShouldBe("youtube");
        data.Summary["oauthClientId"].ShouldBe(ClientId);
        data.Summary["externalAccountId"].ShouldBe(ChannelId);
        data.Summary["publicationsCancelled"].ShouldBe(2);
        data.Summary["quotaUnitsReleased"].ShouldBe(1);
        data.Summary["googleRevoke"].ShouldBe("revoked");

        // No value is free text: every one is a string id, a count or a frozen code, so a title or a token cannot
        // even be typed into the projection without a key appearing above.
        data.Summary.Values.All(value => value is string or int).ShouldBeTrue();

        // A refusal (404, a refused deletion request) leaves no outcome, so the behaviour writes no row for it.
        var refused = command with { Outcome = null };

        refused.ToAuditData().ShouldBeNull();
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("alreadyRevoked")]
    [InlineData("failed")]
    [InlineData("skipped")]
    public void Disconnect_AuditProjection_CarriesTheGoogleRevokeAnswerVerbatim(string code)
    {
        // The four codes are the frozen vocabulary of the audit and of the log line (DisconnectGoogleRevoke); the
        // projection passes the handler's answer through instead of inventing a value.
        GoogleRevokeCodes.ShouldContain(code);

        var outcome = new DisconnectedChannelOutcome(
            Guid.CreateVersion7(),
            "youtube",
            ClientId,
            ChannelId,
            PublicationsCancelled: 0,
            QuotaUnitsReleased: 0,
            GoogleRevoke: code);

        var data = new DisconnectChannelCommand(outcome.ConnectionId) { Outcome = outcome }.ToAuditData();

        data.ShouldNotBeNull();
        data.Summary["googleRevoke"].ShouldBe(code);
    }

    [Fact]
    public async Task Disconnect_RepeatOfARevokedConnection_WritesNoAuditAndLogsIdsOnly()
    {
        // The second DELETE of one channel: the locked read answers a row that already holds the state the request
        // would write. Nothing is re-revoked (there is no ciphertext left), no audit row is projected — the command's
        // outcome stays null — and the one line carries the connection id and nothing else, even though the row still
        // holds the channel's title and Google's account id (the purge is 06.4.3-T1's, days later).
        using var world = new World();
        var connection = world.RevokedConnection();
        world.Locker.Row = connection;
        var command = new DisconnectChannelCommand(connection.Id);

        var result = await world.Handler.Handle(command, Ct);

        result.IsError.ShouldBeFalse();
        command.Outcome.ShouldBeNull("the repeat changed nothing, so AuditBehavior writes no row");
        command.ToAuditData().ShouldBeNull();

        world.YouTube.RevokeTokens.ShouldBeEmpty("both ciphertexts are NULL, so there is nothing to revoke");
        world.Ledger.Released.ShouldBeEmpty();

        // The engine's own idempotent create is called again: a first attempt whose response was lost may have
        // rolled its request back with it (06.4.1-T1 §Design step 2).
        var deletion = world.Deletions.Calls.ShouldHaveSingleItem();

        deletion.UserId.ShouldBe(world.CurrentUser.Id!.Value);
        deletion.Scope.ShouldBe(DeletionScope.Channel);
        deletion.TargetId.ShouldBe(connection.Id);
        deletion.Reason.ShouldBe(DeletionReason.InAppRevoke);

        var line = world.Log.Entries.ShouldHaveSingleItem();

        line.EventId.Id.ShouldBe(6507);
        line.Message.ShouldContain(connection.Id.ToString("D", CultureInfo.InvariantCulture));
        line.Message.ShouldNotContain(Title, Case.Insensitive, "the channel title never reaches a log line");
        line.Message.ShouldNotContain(ChannelId, Case.Insensitive, "Google's account id is not part of the line");
        line.Message.ShouldNotContain("cfDJ8", Case.Insensitive, "no ciphertext ever reaches a log line");
    }

    [Fact]
    public async Task Disconnect_WithoutAnOwner_AnswersNotFoundAndWritesNothing()
    {
        // The tenant comes from the scope, never from the command: an anonymous scope owns nothing, so the handler
        // answers the same 404 and touches neither the row nor Google nor the deletion engine.
        using var world = new World();
        world.CurrentUser.Id = null;
        var command = new DisconnectChannelCommand(Guid.CreateVersion7());

        var result = await world.Handler.Handle(command, Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("not_found");
        command.ToAuditData().ShouldBeNull();
        world.Locker.Lookups.ShouldBeEmpty();
        world.Deletions.Calls.ShouldBeEmpty();
        world.Log.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Disconnect_HandlerLoggerMessages_CarryIdsCountsAndCodesOnly()
    {
        // 06 README D17 / acceptance "no log line contains a token, a channel title or a video title": every line the
        // handler can write is one of its five templated messages, and every placeholder it interpolates is an id, a
        // count or a frozen code. The generated methods take exactly those values, so a token, a title or a display
        // name has no parameter to arrive through.
        var messages = typeof(DisconnectChannelHandler)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => (Method: method, Event: method.GetCustomAttribute<LoggerMessageAttribute>()))
            .Where(entry => entry.Event is not null)
            .OrderBy(entry => entry.Event!.EventId)
            .ToList();

        messages.Select(entry => entry.Event!.EventId)
            .ShouldBe([6506, 6507, 6508, 6509, 6510], "the disconnect's block is 6506-6510; a new line takes the next free id");

        string[] forbiddenPlaceholderParts =
        [
            "token",
            "cipher",
            "secret",
            "title",
            "name",
            "email",
            "description",
            "uri",
        ];

        foreach (var (method, loggerMessage) in messages)
        {
            var template = loggerMessage!.Message;
            var parameters = method.GetParameters();

            Placeholders(template).Count.ShouldBe(
                parameters.Length,
                $"{method.Name} interpolates one value per parameter");

            foreach (var parameter in parameters)
            {
                AllowedLogParameterTypes.ShouldContain(
                    allowed => parameter.ParameterType == allowed,
                    $"{method.Name} may only log an id, a count or a frozen code");

                var name = parameter.Name.ShouldNotBeNull();

                forbiddenPlaceholderParts.ShouldAllBe(
                    forbidden => !name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"{method.Name} logs '{name}', which names something D17 forbids");
            }

            foreach (var placement in Placeholders(template))
            {
                parameters.ShouldContain(
                    parameter => string.Equals(parameter.Name, placement, StringComparison.OrdinalIgnoreCase),
                    $"{method.Name} interpolates '{placement}', which no parameter supplies");
            }
        }
    }

    /// <summary>The <c>{Placeholder}</c> names of one <c>LoggerMessage</c> template, in order.</summary>
    /// <param name="template">The message template.</param>
    private static List<string> Placeholders(string template)
    {
        var names = new List<string>();

        for (var open = template.IndexOf('{', 0); open >= 0; open = template.IndexOf('{', open + 1))
        {
            var close = template.IndexOf('}', open);

            if (close < 0)
            {
                break;
            }

            names.Add(template[(open + 1)..close].Split(',', ':')[0].Trim());
            open = close;
        }

        return names;
    }

    /// <summary>
    /// One test's world: the five hand-written ports, the write model they write into and the handler under test.
    /// </summary>
    /// <remarks>
    /// The options point at an address nothing opens — the repeat path executes no statement (the locked read is the
    /// fake's and the deletion request is the fake's) — so the change tracker is the whole write model here. The
    /// paths that do reach PostgreSQL (the cascade, the ledger, the outbox row) belong to the Api integration tests.
    /// </remarks>
    private sealed class World : IDisposable
    {
        private const string UnusedConnectionString =
            "Host=127.0.0.1;Port=5432;Database=nts_never_connected;Username=nts;Password=nts";

        public World()
        {
            Clock = new FakeClock(Now);
            CurrentUser.Id = UserId;
            Database = TestDbContextFactory.CreateAppContext(UnusedConnectionString, CurrentUser);
            Handler = new DisconnectChannelHandler(
                Locker,
                Database,
                Protector,
                YouTube,
                Ledger,
                Deletions,
                Metrics,
                CurrentUser,
                Clock,
                TimeProvider.System,
                Log);
        }

        /// <summary>The account every request belongs to, unless a test says otherwise.</summary>
        public Guid UserId { get; } = Guid.CreateVersion7();

        /// <summary>The scope the handler reads the tenant from; a test can anonymise it.</summary>
        public TestCurrentUser CurrentUser { get; } = new();

        /// <summary>The handler's clock, pinned at <see cref="Now"/>.</summary>
        public FakeClock Clock { get; }

        /// <summary>The locked read: it answers the row a test seeded.</summary>
        public FakeChannelConnectionLocker Locker { get; } = new();

        /// <summary>The protector; the revoked row it answers is never unprotected.</summary>
        public FakeTokenProtector Protector { get; } = new();

        /// <summary>The Google client; the repeat path never calls it.</summary>
        public RecordingYouTubeClient YouTube { get; } = new();

        /// <summary>The ledger; the repeat path never releases.</summary>
        public RecordingQuotaLedger Ledger { get; } = new();

        /// <summary>The deletion engine; it records every create.</summary>
        public RecordingDeletionRequests Deletions { get; } = new();

        /// <summary>The meter the failure counter lives on; disposed with the world.</summary>
        public NightTalesMetrics Metrics { get; } = new();

        /// <summary>The handler's log, kept so a test can scan the rendered lines.</summary>
        public RecordingLogger Log { get; } = new();

        /// <summary>The tracked write model of the caller's unit of work.</summary>
        public AppDbContext Database { get; }

        /// <summary>The handler under test, over the ports above.</summary>
        public DisconnectChannelHandler Handler { get; }

        /// <summary>
        /// The row a second disconnect finds: the connect's identity and title survive, both ciphertexts are gone and
        /// the status is terminal.
        /// </summary>
        public ChannelConnection RevokedConnection()
        {
            var connection = ChannelConnection.Connect(
                Guid.CreateVersion7(),
                UserId,
                Platform.YouTube,
                ChannelId,
                Title,
                AccessCipher,
                RefreshCipher,
                Now.AddHours(1),
                "https://www.googleapis.com/auth/youtube.upload",
                ClientId,
                Now);

            connection.Revoke(Now.AddMinutes(30));

            return connection;
        }

        public void Dispose()
        {
            Database.Dispose();
            Metrics.Dispose();
        }
    }

    /// <summary>The locked read: the id lookup answers the row the test seeded and records every lookup.</summary>
    private sealed class FakeChannelConnectionLocker : IChannelConnectionLocker
    {
        /// <summary>The row the id lookup answers; null is an unknown or foreign id.</summary>
        public ChannelConnection? Row { get; set; }

        /// <summary>Every lookup, in arrival order.</summary>
        internal List<Guid> Lookups { get; } = [];

        /// <summary>The disconnect reads by id; the account read belongs to the connect write.</summary>
        public Task<ChannelConnection?> LockLiveByAccountAsync(
            Platform platform,
            string externalAccountId,
            CancellationToken ct) =>
            throw new NotSupportedException("The disconnect locks the connection id, never the account.");

        public Task<ChannelConnection?> LockByIdAsync(Guid connectionId, CancellationToken ct)
        {
            Lookups.Add(connectionId);

            return Task.FromResult(Row);
        }
    }

    /// <summary>
    /// The protector: it answers no plaintext, which is the dead-key-ring case. The repeat path never asks, so a test
    /// that reaches it is a change of the route's order.
    /// </summary>
    private sealed class FakeTokenProtector : ITokenProtector
    {
        public string Protect(string purposeSuffix, string plaintext) =>
            throw new NotSupportedException("The disconnect never protects a token.");

        public bool TryUnprotect(
            string purposeSuffix,
            string cipher,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? plaintext)
        {
            plaintext = null;

            return false;
        }
    }

    /// <summary>The Google client: it records every revoke and refuses to answer one.</summary>
    private sealed class RecordingYouTubeClient : IYouTubeClient
    {
        /// <summary>Every token handed to <see cref="RevokeAsync"/>, in arrival order.</summary>
        internal List<string> RevokeTokens { get; } = [];

        public Task<ErrorOr<RevokeOutcome>> RevokeAsync(string token, CancellationToken ct)
        {
            RevokeTokens.Add(token);

            throw new NotSupportedException("A revoked connection stores no plaintext token to revoke.");
        }

        public string BuildAuthorizationUrl(
            OAuthClient client,
            OAuthAuthorizationRequest request,
            string state,
            string? codeChallenge) =>
            throw new NotSupportedException();

        public Task<ErrorOr<OAuthTokens>> ExchangeCodeAsync(
            OAuthClient client,
            string code,
            string? codeVerifier,
            string redirectUri,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ErrorOr<OAuthTokens>> RefreshAsync(OAuthClient client, string refreshToken, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ErrorOr<GoogleChannel>> GetMyChannelAsync(string accessToken, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ErrorOr<Uri>> StartUploadAsync(
            string accessToken,
            VideoInsertRequest request,
            long length,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ErrorOr<UploadResult>> ResumeUploadAsync(
            string accessToken,
            Uri sessionUri,
            Stream content,
            long length,
            IProgress<long>? progress,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ErrorOr<UploadProbe>> ProbeUploadAsync(
            string accessToken,
            Uri sessionUri,
            long length,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ErrorOr<IReadOnlyList<YouTubeVideoStatus>>> GetVideosAsync(
            string accessToken,
            IReadOnlyList<string> ids,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// The ledger: a reservation on the repeat path is a defect, so <see cref="TryReserveAsync"/> throws; the
    /// releases are recorded so a test can assert the cascade released nothing.
    /// </summary>
    private sealed class RecordingQuotaLedger : IQuotaLedger
    {
        /// <summary>Every reservation a release was handed, in order.</summary>
        internal List<QuotaReservation> Released { get; } = [];

        public Task<ErrorOr<QuotaReservation>> TryReserveAsync(
            string platform,
            DateTimeOffset now,
            CancellationToken ct = default) =>
            throw new NotSupportedException("The disconnect never reserves quota.");

        public Task<ErrorOr<Success>> ReleaseAsync(QuotaReservation reservation, CancellationToken ct = default)
        {
            Released.Add(reservation);

            return Task.FromResult<ErrorOr<Success>>(Result.Success);
        }
    }

    /// <summary>One <c>CreateAsync</c> call: the identity the engine was handed.</summary>
    /// <param name="UserId">The owning account.</param>
    /// <param name="Scope">The scope of the request.</param>
    /// <param name="TargetId">The channel connection to purge, or null.</param>
    /// <param name="Reason">Why the request exists.</param>
    private sealed record DeletionRequestCall(Guid UserId, DeletionScope Scope, Guid? TargetId, DeletionReason Reason);

    /// <summary>The deletion engine: it records every create and answers the row the domain builds.</summary>
    private sealed class RecordingDeletionRequests : IDeletionRequests
    {
        /// <summary>Every create, in arrival order.</summary>
        internal List<DeletionRequestCall> Calls { get; } = [];

        public Task<bool> ExistsAsync(
            Guid userId,
            DeletionScope scope,
            Guid? targetId,
            DeletionReason reason,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<ErrorOr<DeletionRequest>> CreateAsync(
            Guid userId,
            DeletionScope scope,
            Guid? targetId,
            DeletionReason reason,
            CancellationToken cancellationToken)
        {
            Calls.Add(new DeletionRequestCall(userId, scope, targetId, reason));

            return Task.FromResult(DeletionRequest.Create(userId, scope, targetId, reason, Now));
        }
    }

    /// <summary>One captured line: its level, its event id and its rendered message.</summary>
    /// <param name="Level">The level the line was written at.</param>
    /// <param name="EventId">The <c>LoggerMessage</c> id the line was written under.</param>
    /// <param name="Message">The rendered message.</param>
    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message);

    /// <summary>Keeps every line the handler wrote, with its id and level, so a test can scan the rendered text.</summary>
    private sealed class RecordingLogger : ILogger<DisconnectChannelHandler>
    {
        private readonly List<LogEntry> _entries = [];

        /// <summary>Every line, in the order the handler wrote it.</summary>
        internal List<LogEntry> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            _entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception)));
        }
    }
}
