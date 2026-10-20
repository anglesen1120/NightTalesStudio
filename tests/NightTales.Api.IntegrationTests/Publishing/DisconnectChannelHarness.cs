using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Security;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Google;
using NightTales.TestKit.Time;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// The world the 06.4.1-T1 disconnect tests need: the <c>youtube_publish</c> flag row, a
/// <see cref="FakeGoogleServer"/> whose <c>POST /revoke</c> answer the test scripts, the real Api pointed at
/// it with a <see cref="FakeClock"/> pinned to <see cref="Start"/>, the host's own
/// <see cref="ITokenProtector"/> for seeding production-shaped ciphers, and raw-SQL probes for the four
/// tables a disconnect writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a harness and not the class's private helpers.</b> The disconnect assertions are split over two
/// test classes (the route's revoke/wipe/idempotency behaviour and the cascade's quota/deletion behaviour),
/// and both need the same seed — a connection whose token columns are protected under the row's own purpose
/// <c>{connectionId}</c>, which <see cref="ChannelConnectionBuilder"/> cannot write because it stores a fixed
/// non-decryptable placeholder. The protector is resolved from <see cref="ApiFactory.Services"/>, i.e. the
/// same key ring the request scope unprotects with, exactly as <c>TokenServiceTests</c> seeds.
/// </para>
/// <para>
/// The flag row is written <em>before</em> the factory is built, because the feature guard caches the flags
/// per host (09.9.2-T2). Every probe is scoped to this test's own ids, so a class can share one database
/// without reading another test's rows.
/// </para>
/// </remarks>
internal sealed class DisconnectChannelHarness : IAsyncDisposable
{
    /// <summary>The channels group the disconnect route lives in (06.4.1-T1 §Endpoint).</summary>
    internal const string ChannelsRoute = "/api/v1/channels";

    /// <summary>The flag the whole channels group is behind.</summary>
    internal const string FeatureKey = "youtube_publish";

    /// <summary>D17's audit action of this command.</summary>
    internal const string AuditAction = "publishing.channel_disconnected";

    /// <summary>The D13 event one connection state change raises.</summary>
    internal const string ConnectionEventType = "channel_connection_changed.v1";

    /// <summary>The D13 event one cancelled publication raises.</summary>
    internal const string PublicationEventType = "publication_updated.v1";

    /// <summary>The D17 audit action of the cancel route — a disconnect's cascade must never write one.</summary>
    internal const string PublicationAuditAction = "publishing.cancelled";

    /// <summary>The LoggerMessage event id of the "no usable plaintext token" warning (6508).</summary>
    internal const int RevokeSkippedEventId = 6508;

    /// <summary>The LoggerMessage event id of the disconnect's id-only success line (6506).</summary>
    internal const int DisconnectedEventId = 6506;

    /// <summary>The LoggerMessage event id of the revoke ladder's exhaustion (6509).</summary>
    internal const int RevokeFailedEventId = 6509;

    /// <summary>The LoggerMessage event id of the cascade's skip line (6510).</summary>
    internal const int CascadeSkippedEventId = 6510;

    /// <summary>
    /// The per-row "reserved day present but unusable" warning — 6505, spelled literally because the step
    /// that logs it (<c>PublicationQuotaRelease</c>) is internal to the Application assembly.
    /// </summary>
    internal const int ReservedDayUnreadableEventId = 6505;

    /// <summary>The plaintext access token every seeded row stores: a canary, never a real credential.</summary>
    internal const string AccessToken = "canary-06.4.1-access-token";

    /// <summary>The plaintext refresh token every seeded row stores.</summary>
    internal const string RefreshToken = "canary-06.4.1-refresh-token";

    /// <summary>A channel title only the seeded connection carries.</summary>
    internal const string ChannelTitle = "Kênh canary 06.4.1";

    /// <summary>A publication title only the seeded rows carry.</summary>
    internal const string PublicationTitle = "Tựa đề canary 06.4.1";

    /// <summary>A publication description only the seeded rows carry.</summary>
    internal const string PublicationDescription = "Mô tả canary 06.4.1";

    /// <summary>The Pacific quota day the reserved rows name.</summary>
    internal const string QuotaDay = "2026-10-02";

    /// <summary>A second day no disconnect may touch.</summary>
    internal const string UntouchedQuotaDay = "2026-10-03";

    /// <summary>
    /// A cipher that decodes as nothing: the "the key ring rotated away" row shape, which
    /// <see cref="ITokenProtector.TryUnprotect"/> answers false for instead of throwing.
    /// </summary>
    internal const string UnreadableCipher = "not-a-cipher-this-ring-can-open";

    /// <summary>The scope a connect pins (06 README D5).</summary>
    private const string Scope =
        "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly";

    /// <summary>The OAuth client a seeded connection is pinned to.</summary>
    private const string OAuthClientId = "nts-test-client.apps.googleusercontent.com";

    /// <summary>The instant every seeded row and the host's clock agree on.</summary>
    internal static readonly DateTimeOffset Start = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly ApiFactory _factory;
    private readonly FakeGoogleServer _google;
    private readonly CapturingLoggerProvider _logs;
    private readonly Dictionary<Guid, Guid> _seriesByUser = [];
    private ITokenProtector? _protector;

    private DisconnectChannelHarness(
        TestDatabase database,
        FakeClock clock,
        FakeGoogleServer google,
        ApiFactory factory,
        CapturingLoggerProvider logs)
    {
        Database = database;
        Clock = clock;
        _google = google;
        _factory = factory;
        _logs = logs;
    }

    /// <summary>The class database, for the raw-SQL probes.</summary>
    internal TestDatabase Database { get; }

    /// <summary>The host's clock; every seeded expiry and the disconnect instant are relative to it.</summary>
    internal FakeClock Clock { get; }

    /// <summary>The fake Google endpoints the host is pointed at.</summary>
    internal FakeGoogleServer Google => _google;

    /// <summary>Every line this host rendered, with its <c>EventId</c>, for the redaction and skip assertions.</summary>
    internal CapturingLoggerProvider Logs => _logs;

    /// <summary>The host's protector, for the ciphers a real request unprotects.</summary>
    private ITokenProtector Protector =>
        _protector ??= _factory.Services.GetRequiredService<ITokenProtector>();

    /// <summary>
    /// Builds the world: the flag row (before the host, because the guard caches it), the fake Google, the
    /// host with the log capture, and one protector-resolved key ring.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="ct">Cancels the setup.</param>
    /// <param name="flagOn">Whether <c>youtube_publish</c> is on; false is the 404 <c>feature_disabled</c> case.</param>
    /// <param name="googleOptions">The fake's switches, for the revoke answer.</param>
    /// <param name="services">
    /// An extra registration pass over this host's own container, applied after every registration the factory
    /// makes (so one test can replace a port without touching another test's host); null keeps the production
    /// graph. It replaces nothing by itself: a caller that needs another implementation removes the production
    /// one first (<c>RemoveAll&lt;T&gt;</c>) and then registers its own.
    /// </param>
    internal static async Task<DisconnectChannelHarness> StartAsync(
        TestDatabase database,
        CancellationToken ct,
        bool flagOn = true,
        FakeGoogleOptions? googleOptions = null,
        Action<IServiceCollection>? services = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        var clock = new FakeClock(Start);

        await database.ExecuteAsync($"DELETE FROM feature_flags WHERE key = '{FeatureKey}'", ct);
        if (flagOn)
        {
            await database.ExecuteAsync(
                "INSERT INTO feature_flags (key, enabled, rules, created_at, updated_at) "
                + $"VALUES ('{FeatureKey}', true, '{{}}', now(), now())",
                ct);
        }

        var google = await FakeGoogleServer.StartAsync(googleOptions, ct);
        var logs = new CapturingLoggerProvider();
        var factory = new ApiFactory(database, clock)
            .WithFakeGoogle(google)
            .WithLoggerProvider(logs);

        if (services is not null)
        {
            factory = factory.WithServices(services);
        }

        return new DisconnectChannelHarness(database, clock, google, factory, logs);
    }

    /// <summary>Creates one account (a real <c>users</c> row) and returns its id.</summary>
    /// <param name="ct">Cancels the write.</param>
    internal async Task<Guid> CreateUserAsync(CancellationToken ct)
    {
        var owner = new TestCurrentUser();
        var user = await new UserBuilder(Database, owner).CreateAsync(ct: ct);

        return user.Id;
    }

    /// <summary>An authenticated client of <paramref name="userId"/>'s token (the test authentication scheme).</summary>
    /// <param name="userId">The <c>sub</c> claim every request presents.</param>
    internal HttpClient ClientFor(Guid userId) => _factory.ClientFor(userId);

    /// <summary>
    /// Inserts one real <c>channel_connections</c> row whose two cipher columns are protected under the row's
    /// own purpose <c>{connectionId}</c> (06 README D4), exactly what the connect write stores.
    /// </summary>
    /// <param name="userId">The owner.</param>
    /// <param name="accessToken">The plaintext access token, or null to store a null cipher.</param>
    /// <param name="refreshToken">The plaintext refresh token, or null to store a null cipher.</param>
    /// <param name="accessTokenCipher">A cipher written verbatim instead of protecting <paramref name="accessToken"/>.</param>
    /// <param name="refreshTokenCipher">A cipher written verbatim instead of protecting <paramref name="refreshToken"/>.</param>
    /// <param name="displayName">The channel title the row stores.</param>
    /// <param name="status">The row's state; <c>active</c> is the disconnectable one.</param>
    /// <param name="ct">Cancels the write.</param>
    internal async Task<SeededChannel> SeedChannelAsync(
        Guid userId,
        string? accessToken = AccessToken,
        string? refreshToken = RefreshToken,
        string? accessTokenCipher = null,
        string? refreshTokenCipher = null,
        string displayName = ChannelTitle,
        ChannelConnectionStatus status = ChannelConnectionStatus.Active,
        CancellationToken ct = default)
    {
        var id = Guid.CreateVersion7();
        var purpose = id.ToString("D", CultureInfo.InvariantCulture);
        var accessCipher = accessTokenCipher
            ?? (accessToken is null ? null : Protector.Protect(purpose, accessToken));
        var refreshCipher = refreshTokenCipher
            ?? (refreshToken is null ? null : Protector.Protect(purpose, refreshToken));
        var externalAccountId = $"UC-{id:N}";

        await Database.ExecuteAsync(
            "INSERT INTO channel_connections "
            + "(id, user_id, platform, external_account_id, display_name, status, access_token_enc, "
            + "refresh_token_enc, token_expires_at, scope, oauth_client_id, last_verified_at, connected_at, revoked_at) "
            + $"VALUES ('{id}', '{userId}', 'youtube', '{externalAccountId}', {Literal(displayName)}, "
            + $"'{Wire(status)}', {NullableLiteral(accessCipher)}, {NullableLiteral(refreshCipher)}, "
            + $"{Instant(Clock.UtcNow.AddHours(1))}, {Literal(Scope)}, '{OAuthClientId}', "
            + $"{Instant(Clock.UtcNow.AddHours(-1))}, {Instant(Clock.UtcNow.AddDays(-10))}, NULL)",
            ct);

        return new SeededChannel(id, externalAccountId, accessToken, refreshToken);
    }

    /// <summary>
    /// Inserts one <c>publications</c> row of <paramref name="channelConnectionId"/> on its own episode (the
    /// <c>ux_publications_active_episode_platform</c> index admits one live row per episode and platform).
    /// </summary>
    /// <param name="userId">The owner.</param>
    /// <param name="channelConnectionId">The connection the row publishes through.</param>
    /// <param name="status">The lifecycle state.</param>
    /// <param name="platformPayloadJson">The <c>platform_payload</c> document, for the quota-key cases.</param>
    /// <param name="title">The title, or the canary this file declares.</param>
    /// <param name="description">The description, or the canary this file declares.</param>
    /// <param name="ct">Cancels the writes.</param>
    internal async Task<SeededPublication> SeedPublicationAsync(
        Guid userId,
        Guid channelConnectionId,
        PublicationStatus status = PublicationStatus.Scheduled,
        string? platformPayloadJson = null,
        string? title = null,
        string? description = null,
        CancellationToken ct = default)
    {
        var actor = new TestCurrentUser { Id = userId };
        var seriesId = await SeriesForAsync(userId, actor, ct);

        var episode = await new EpisodeBuilder(Database, actor, Clock).CreateAsync(
            userId,
            seriesId,
            brief: "Nội dung tập canary 06.4.1.",
            status: EpisodeStatus.Ready,
            createdAt: Start,
            ct: ct);

        var publicationId = await new PublicationBuilder(Database).CreateAsync(
            userId,
            episode.Id,
            channelConnectionId,
            status: status,
            title: title ?? PublicationTitle,
            description: description ?? PublicationDescription,
            platformPayloadJson: platformPayloadJson,
            createdAt: Start,
            ct: ct);

        return new SeededPublication(publicationId, episode.Id, status);
    }

    /// <summary>Sends <c>DELETE /api/v1/channels/{id}</c> as <paramref name="client"/>.</summary>
    /// <param name="client">The authenticated client.</param>
    /// <param name="channelId">The connection id in the path.</param>
    /// <param name="ct">Cancels the call.</param>
    internal static Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid channelId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{ChannelsRoute}/{channelId}");

        return client.SendAsync(request, ct);
    }

    /// <summary>The whole mutable row as one text, so "unchanged" is one comparison.</summary>
    /// <param name="channelId">The connection.</param>
    internal string ChannelRow(Guid channelId) =>
        SqlProbe.ScalarText(
            Database,
            "SELECT status || '|' || coalesce(access_token_enc, '<null>') || '|' "
            + "|| coalesce(refresh_token_enc, '<null>') || '|' || token_expires_at::text || '|' "
            + "|| coalesce(revoked_at::text, '<null>') || '|' || coalesce(display_name, '<null>') "
            + $"|| '|' || coalesce(external_account_id, '<null>') FROM channel_connections WHERE id = '{channelId}'");

    /// <summary>Whether the one connection row satisfies <paramref name="predicate"/>.</summary>
    /// <param name="channelId">The connection.</param>
    /// <param name="predicate">A SQL predicate over the row's columns.</param>
    internal bool ChannelHas(Guid channelId, string predicate) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM channel_connections WHERE id = '{channelId}' AND ({predicate})") == 1;

    /// <summary>Whether the connection is gone, so a leak of the whole row is caught too.</summary>
    /// <param name="channelId">The connection.</param>
    internal long ChannelRowCount(Guid channelId) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM channel_connections WHERE id = '{channelId}'");

    /// <summary>The <c>data</c> of this command's audit rows for <paramref name="channelId"/> (D17).</summary>
    /// <param name="channelId">The connection.</param>
    internal List<string> AuditData(Guid channelId) =>
        SqlProbe.QueryStrings(
            Database,
            $"SELECT data::text FROM audit_logs WHERE action = '{AuditAction}' AND entity_id = '{channelId}'");

    /// <summary>Whether this command's audit row for <paramref name="channelId"/> satisfies <paramref name="predicate"/>.</summary>
    /// <param name="channelId">The connection.</param>
    /// <param name="predicate">A SQL predicate over <c>audit_logs</c>.</param>
    internal bool AuditHas(Guid channelId, string predicate) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM audit_logs WHERE action = '{AuditAction}' "
            + $"AND entity_id = '{channelId}' AND ({predicate})") == 1;

    /// <summary>The payloads of the <c>channel_connection_changed.v1</c> rows of one connection.</summary>
    /// <param name="channelId">The connection.</param>
    internal List<string> ConnectionPayloads(Guid channelId) =>
        SqlProbe.QueryStrings(
            Database,
            $"SELECT payload::text FROM outbox_messages WHERE type = '{ConnectionEventType}' "
            + $"AND payload->>'connectionId' = '{channelId}'");

    /// <summary>The payloads of the <c>publication_updated.v1</c> rows of one publication.</summary>
    /// <param name="publicationId">The publication.</param>
    internal List<string> PublicationPayloads(Guid publicationId) =>
        SqlProbe.QueryStrings(
            Database,
            $"SELECT payload::text FROM outbox_messages WHERE type = '{PublicationEventType}' "
            + $"AND payload->>'publicationId' = '{publicationId}'");

    /// <summary>How many <c>publishing.cancelled</c> audit rows one publication has — the cascade writes none.</summary>
    /// <param name="publicationId">The publication.</param>
    internal long PublicationAuditCount(Guid publicationId) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM audit_logs WHERE action = '{PublicationAuditAction}' "
            + $"AND entity_id = '{publicationId}'");

    /// <summary>
    /// Every deletion request of one user and target as <c>scope|target|reason|status|requested_at|due_at</c>,
    /// with the two date columns compared to the clock the handler ran on.
    /// </summary>
    /// <param name="userId">The owner.</param>
    /// <param name="targetId">The connection the request purges.</param>
    internal List<string> DeletionRequests(Guid userId, Guid targetId) =>
        SqlProbe.QueryStrings(
            Database,
            "SELECT scope || '|' || coalesce(target_id::text, '<null>') || '|' || reason || '|' || status "
            + "|| '|' || requested_at::text || '|' || due_at::text "
            + $"FROM data_deletion_requests WHERE user_id = '{userId}' AND target_id = '{targetId}'");

    /// <summary>How many deletion requests the user has at all, so a second one is visible.</summary>
    /// <param name="userId">The owner.</param>
    internal long DeletionRequestCount(Guid userId) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM data_deletion_requests WHERE user_id = '{userId}'");

    /// <summary>Whether the one request for this user and target satisfies <paramref name="predicate"/>.</summary>
    /// <param name="userId">The owner.</param>
    /// <param name="targetId">The connection the request purges.</param>
    /// <param name="predicate">A SQL predicate over <c>data_deletion_requests</c>.</param>
    internal bool DeletionHas(Guid userId, Guid targetId, string predicate) =>
        SqlProbe.Count(
            Database,
            "SELECT count(*) FROM data_deletion_requests "
            + $"WHERE user_id = '{userId}' AND target_id = '{targetId}' AND ({predicate})") == 1;

    /// <summary>The <c>status</c> of one publication.</summary>
    /// <param name="publicationId">The publication.</param>
    internal string PublicationStatusOf(Guid publicationId) =>
        SqlProbe.ScalarText(Database, $"SELECT status FROM publications WHERE id = '{publicationId}'");

    /// <summary>The <c>xmin</c> of one publication, so "written nothing" is one comparison.</summary>
    /// <param name="publicationId">The publication.</param>
    internal uint Xmin(Guid publicationId) =>
        uint.Parse(
            SqlProbe.ScalarText(Database, $"SELECT xmin::text FROM publications WHERE id = '{publicationId}'"),
            CultureInfo.InvariantCulture);

    /// <summary>Seeds one <c>platform_quota_usage</c> row for the given day, replacing any earlier one.</summary>
    /// <param name="day">The Pacific quota day.</param>
    /// <param name="unitsUsed">The used units.</param>
    /// <param name="unitsLimit">The day's limit.</param>
    /// <param name="ct">Cancels the writes.</param>
    internal async Task SeedQuotaRowAsync(string day, int unitsUsed, int unitsLimit, CancellationToken ct)
    {
        await Database.ExecuteAsync(
            $"DELETE FROM platform_quota_usage WHERE platform = 'youtube' AND quota_day = '{day}'",
            ct);

        await Database.ExecuteAsync(
            "INSERT INTO platform_quota_usage (platform, quota_day, units_used, units_limit, created_at, updated_at) "
            + $"VALUES ('youtube', '{day}', {unitsUsed}, {unitsLimit}, now(), now())",
            ct);
    }

    /// <summary>The <c>units_used</c> of the day's ledger row.</summary>
    /// <param name="day">The Pacific quota day.</param>
    internal int UnitsUsed(string day) =>
        int.Parse(
            SqlProbe.ScalarText(
                Database,
                $"SELECT units_used FROM platform_quota_usage WHERE platform = 'youtube' AND quota_day = '{day}'"),
            CultureInfo.InvariantCulture);

    /// <summary>A whole ledger row as one text, so "untouched" is one comparison.</summary>
    /// <param name="day">The Pacific quota day.</param>
    internal string QuotaRow(string day) =>
        SqlProbe.ScalarText(
            Database,
            "SELECT units_used::text || '|' || units_limit::text || '|' || updated_at::text "
            + $"FROM platform_quota_usage WHERE platform = 'youtube' AND quota_day = '{day}'");

    /// <summary>How many ledger rows the day has; a disconnect must never create one.</summary>
    /// <param name="day">The Pacific quota day.</param>
    internal long QuotaRowCount(string day) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM platform_quota_usage WHERE platform = 'youtube' AND quota_day = '{day}'");

    /// <summary>The <c>platform_payload</c> of the key contract with 06.2.3-T2: a Pacific day as ISO text.</summary>
    /// <param name="day">The Pacific quota day.</param>
    internal static string QuotaPayload(string day) => $$"""{"quotaReservedDay":"{{day}}"}""";

    /// <summary>An instant a raw <c>WHERE</c> can compare with, in the round-tripping format.</summary>
    /// <param name="value">The instant.</param>
    internal static string Instant(DateTimeOffset value) =>
        $"'{value.ToString("O", CultureInfo.InvariantCulture)}'::timestamptz";

    /// <summary>The snake_case wire value the <c>status</c> CHECK constraint lists.</summary>
    /// <param name="status">The connection state.</param>
    private static string Wire(ChannelConnectionStatus status) => status switch
    {
        ChannelConnectionStatus.Active => "active",
        ChannelConnectionStatus.Revoked => "revoked",
        ChannelConnectionStatus.Expired => "expired",
        ChannelConnectionStatus.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown connection status."),
    };

    private static string Literal(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>A nullable text as a SQL literal, with <c>NULL</c> for the absence of a cipher.</summary>
    /// <param name="value">The text, or null.</param>
    private static string NullableLiteral(string? value) => value is null ? "NULL" : Literal(value);

    private async Task<Guid> SeriesForAsync(Guid userId, TestCurrentUser actor, CancellationToken ct)
    {
        if (_seriesByUser.TryGetValue(userId, out var existing))
        {
            return existing;
        }

        var series = await new SeriesBuilder(Database, actor, Clock).CreateAsync(
            userId,
            title: "Chuỗi canary 06.4.1",
            createdAt: Start,
            ct: ct);

        _seriesByUser[userId] = series.Id;

        return series.Id;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _factory.Dispose();
        await _google.DisposeAsync();
    }

    /// <summary>One seeded connection: the id the path names and the plaintexts Google should be handed.</summary>
    /// <param name="Id">The <c>channel_connections.id</c>.</param>
    /// <param name="ExternalAccountId">Google's opaque channel id.</param>
    /// <param name="AccessToken">The plaintext access token, or null when the row stored none.</param>
    /// <param name="RefreshToken">The plaintext refresh token, or null when the row stored none.</param>
    internal sealed record SeededChannel(
        Guid Id,
        string ExternalAccountId,
        string? AccessToken,
        string? RefreshToken);

    /// <summary>One seeded publication and the episode it publishes.</summary>
    /// <param name="Id">The <c>publications.id</c>.</param>
    /// <param name="EpisodeId">The episode it belongs to.</param>
    /// <param name="Status">The state it was seeded in.</param>
    internal sealed record SeededPublication(Guid Id, Guid EpisodeId, PublicationStatus Status);

    /// <summary>
    /// A <see cref="ILoggerProvider"/> that keeps every line's <see cref="EventId"/>, level and rendered text,
    /// which is how a <c>[LoggerMessage]</c> id is asserted without an exporter (09.9.3-T1 keeps the host's
    /// other providers alive, so an added provider sees every event).
    /// </summary>
    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<CapturedLogLine> _lines = [];
        private readonly Lock _gate = new();

        /// <summary>A snapshot of every line received so far.</summary>
        internal List<CapturedLogLine> Lines
        {
            get
            {
                lock (_gate)
                {
                    return [.. _lines];
                }
            }
        }

        /// <summary>Every captured line as one text: rendered message plus exception.</summary>
        internal string AllText() =>
            string.Join('\n', Lines.Select(line =>
                line.EventId.ToString(CultureInfo.InvariantCulture) + ' ' + line.Message + ' ' + line.Exception));

        /// <summary>How many lines carry <paramref name="eventId"/>.</summary>
        /// <param name="eventId">The <c>[LoggerMessage]</c> id.</param>
        internal int CountOf(int eventId) => Lines.Count(line => line.EventId == eventId);

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        /// <inheritdoc />
        public void Dispose()
        {
        }

        private void Capture(CapturedLogLine line)
        {
            lock (_gate)
            {
                _lines.Add(line);
            }
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
        {
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

                owner.Capture(new CapturedLogLine(
                    eventId.Id,
                    logLevel,
                    category,
                    formatter(state, exception),
                    exception?.ToString() ?? string.Empty));
            }
        }
    }

    /// <summary>One captured log line, as the provider above holds it.</summary>
    /// <param name="EventId">The <c>[LoggerMessage]</c> id, or 0 for a framework line.</param>
    /// <param name="Level">The level it was written at.</param>
    /// <param name="Category">The logger category.</param>
    /// <param name="Message">The rendered message.</param>
    /// <param name="Exception">The rendered exception, or an empty string.</param>
    internal sealed record CapturedLogLine(int EventId, LogLevel Level, string Category, string Message, string Exception);
}
