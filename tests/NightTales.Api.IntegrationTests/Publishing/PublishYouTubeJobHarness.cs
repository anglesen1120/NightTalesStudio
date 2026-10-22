using System.Globalization;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Features.Publishing.Jobs;
using NightTales.Application.Features.Publishing.Upload;
using NightTales.Application.Publishing;
using NightTales.Application.Publishing.Quota;
using NightTales.Domain.Assets;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Google;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// The world <c>PublishYouTubeJob</c> runs in (06.2.3-T2): the real Worker composition
/// (<see cref="WorkerHost"/>: <c>AddWorkerServices</c> over the class database, the class's
/// <see cref="FakeClock"/> in place of the system clock, the inline job scheduler), pointed at a
/// <see cref="FakeGoogleServer"/> and a recording <see cref="IBlobStore"/>, with a log capture over every category
/// of the host.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the Worker host and not an Api host.</b> The upload is a background job: it takes ids only, opens its own
/// scope, resolves its dependencies from the Worker's graph and drives the database through the same
/// <c>IAppDbContext</c>/<c>IUnitOfWork</c> a deployment does. Running it against the production composition is what
/// makes the lock order, the ambient transaction the incident store joins, and the feature-flag cache observable at
/// all; a hand-built container would prove nothing beyond a unit test.
/// </para>
/// <para>
/// <b>Seeding is raw SQL, on purpose.</b> The states these tests need — a row with no <c>consent_at</c>, a row
/// <c>uploading</c> with a protected session URI and a reserved quota day, a row already holding
/// <c>external_post_id</c> — are not reachable through the aggregate's public API without re-running the code under
/// test. The connection's two cipher columns are written through the host's own <see cref="ITokenProtector"/> under
/// the row's per-connection purpose (<c>tokens.v1/{connectionId}</c>, 06 README D4), exactly what the connect write
/// stores, so the job unprotects what a deployment would have written.
/// </para>
/// <para>
/// <b>The flag row is written before the host is built.</b> The feature guard caches the flags per host (09.9.2-T2),
/// so a row written afterwards would be invisible to the run under test.
/// </para>
/// </remarks>
internal sealed class PublishYouTubeJobHarness : IAsyncDisposable
{
    /// <summary>The flag the upload job and the connect flow are behind.</summary>
    internal const string FeatureKey = "youtube_publish";

    /// <summary>The chunk size the tests run with: a 256 KiB multiple (G7) that keeps a payload cheap.</summary>
    internal const int ChunkSizeBytes = 256 * 1024;

    /// <summary>The payload length most tests use: exactly three chunks, so the third one ends the object.</summary>
    internal const long PayloadLength = 3 * ChunkSizeBytes;

    /// <summary>The plaintext access token every seeded connection stores: a canary, never a real credential.</summary>
    internal const string AccessToken = "canary-06.2.3-upload-access-token";

    /// <summary>The plaintext refresh token every seeded connection stores.</summary>
    internal const string RefreshToken = "canary-06.2.3-upload-refresh-token";

    /// <summary>The <c>oauth_client_id</c> a seeded connection is pinned to, and the fake's primary client.</summary>
    internal const string OAuthClientId = FakeGoogleOptions.DefaultClientId;

    /// <summary>A channel title only the seeded connection carries.</summary>
    internal const string ChannelTitle = "Kênh canary 06.2.3-T2";

    /// <summary>The episode title a seeded row publishes: it must never reach a log line.</summary>
    internal const string EpisodeTitle = "Tập canary 06.2.3-T2";

    /// <summary>The description a seeded row carries: it must never reach a log line either.</summary>
    internal const string Description = "Mô tả canary 06.2.3-T2";

    /// <summary>The one key <c>platform_payload</c> carries when a test does not shape it.</summary>
    internal const string DefaultPlatformPayload =
        """{"upload":{"privacyStatus":"private","preAudit":true},"preAudit":true,"requestedPrivacy":"private"}""";

    /// <summary>The scope a seeded connection was granted (06 README D5).</summary>
    private const string Scope =
        "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly";

    /// <summary>The instant every seeded row and the host's clock agree on.</summary>
    internal static readonly DateTimeOffset Start = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly Dictionary<Guid, Guid> _seriesByUser = [];
    private ITokenProtector? _protector;

    private PublishYouTubeJobHarness(
        TestDatabase database,
        FakeClock clock,
        FakeGoogleServer google,
        WorkerHost host,
        RecordingBlobStore blobs,
        CapturingJobLogs logs)
    {
        Database = database;
        Clock = clock;
        Google = google;
        Host = host;
        Blobs = blobs;
        Logs = logs;
    }

    /// <summary>The class database, for the raw-SQL seeds and probes.</summary>
    internal TestDatabase Database { get; }

    /// <summary>The clock the host resolves: every expiry, deferral and deadline is relative to it.</summary>
    internal FakeClock Clock { get; }

    /// <summary>The fake every Google call of this harness lands on.</summary>
    internal FakeGoogleServer Google { get; }

    /// <summary>The started Worker host.</summary>
    internal WorkerHost Host { get; }

    /// <summary>The store the job opens the MP4 from; it counts its opens and the stream's disposals.</summary>
    internal RecordingBlobStore Blobs { get; }

    /// <summary>Every line this host rendered, with its <see cref="EventId"/>, from every category.</summary>
    internal CapturingJobLogs Logs { get; }

    /// <summary>The host's protector, for the ciphers a real run unprotects.</summary>
    internal ITokenProtector Protector =>
        _protector ??= Host.Services.GetRequiredService<ITokenProtector>();

    /// <summary>
    /// Builds the world: the flag row (before the host, because the guard caches it), the fake Google, the started
    /// Worker host with the recording store and the log capture.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="ct">Cancels the setup.</param>
    /// <param name="flagOn">Whether <c>youtube_publish</c> is on; false is the 404 <c>feature_disabled</c> case.</param>
    /// <param name="google">Shapes the fake's switches and identities before the first request.</param>
    /// <param name="auditStatus">The live <c>YouTube:AuditStatus</c> option (F13: the plan wins over it).</param>
    /// <param name="unitsPerDay">The <c>YouTube:Quota:UnitsPerDay</c> the real ledger seeds a day with.</param>
    /// <param name="payloadLength">The MP4 length the store answers with (and the asset's <c>size_bytes</c>).</param>
    /// <param name="services">An extra registration pass, applied after every production registration.</param>
    /// <param name="clock">The clock to run at; the harness's own <see cref="Start"/> when null.</param>
    internal static async Task<PublishYouTubeJobHarness> StartAsync(
        TestDatabase database,
        CancellationToken ct,
        bool flagOn = true,
        Action<FakeGoogleOptions>? google = null,
        string auditStatus = YouTubeAuditStatuses.Pending,
        int unitsPerDay = 100,
        long payloadLength = PayloadLength,
        Action<IServiceCollection>? services = null,
        FakeClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        var fakeClock = clock ?? new FakeClock(Start);

        await database.ExecuteAsync($"DELETE FROM feature_flags WHERE key = '{FeatureKey}'", ct);

        // A class shares one database across its tests, so the three tables whose rows outlive a test are cleared here:
        // the ledger's day rows (a spent day would refuse every later test's reservation — the row's limit is the one
        // a previous harness seeded), the outbox (a stale row would wake its own publication on another test's
        // dispatcher run), and the incidents (an open incident holds every later run of the same platform and OAuth
        // client — UC4/YR24 — and every test here pins the same canary client, so one test's `invalid_client` raise
        // would leave later tests' rows `scheduled`/`platform_incident` with no reservation and no Google call).
        // Each test then seeds exactly the counter, the events and the incident it is about.
        await database.ExecuteAsync("DELETE FROM platform_quota_usage", ct);
        await database.ExecuteAsync("DELETE FROM outbox_messages", ct);
        await database.ExecuteAsync("DELETE FROM platform_incidents", ct);

        if (flagOn)
        {
            await database.ExecuteAsync(
                "INSERT INTO feature_flags (key, enabled, rules, created_at, updated_at) "
                + $"VALUES ('{FeatureKey}', true, '{{}}', now(), now())",
                ct);
        }

        var options = new FakeGoogleOptions();
        google?.Invoke(options);

        var server = await FakeGoogleServer.StartAsync(options, ct);
        var blobs = new RecordingBlobStore(payloadLength);
        var logs = new CapturingJobLogs();

        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in FakeGoogleHosting.SettingsFor(server, ChunkSizeBytes))
        {
            settings[key] = value;
        }

        settings["YouTube:AuditStatus"] = auditStatus;
        settings[$"YouTube:Quota:UnitsPerDay"] = unitsPerDay.ToString(CultureInfo.InvariantCulture);
        settings["YouTube:Poll:IntervalSeconds"] = "60";
        settings["YouTube:Poll:MaxMinutes"] = "45";

        // 06.2.3-T2 has no per-user upload slot (06.3.2 was dropped), so no `userQuota` port is registered and the
        // global ledger is the only reservation this run can take.
        var host = new WorkerHost(database, fakeClock)
            .WithEnvironment(Environments.Development)
            .WithLoggerProvider(logs);

        foreach (var (key, value) in settings)
        {
            host.WithSetting(key, value);
        }

        host.ConfigureServices(collection =>
        {
            // The Worker composition registers the upload graph itself (`AddYouTubePublishing(configuration)`), and
            // `HarnessSettings` carries the `YouTube` client keys that composition's ValidateOnStart needs. A second
            // registration here would bind the options to this harness's partial document instead, so the host is the
            // only place that composes it.
            collection.RemoveAll<IBlobStore>();
            collection.AddSingleton<IBlobStore>(blobs);

            services?.Invoke(collection);
        });

        await host.StartAsync(ct);

        return new PublishYouTubeJobHarness(database, fakeClock, server, host, blobs, logs);
    }

    /// <summary>Creates one account (a real <c>users</c> row) and returns its id.</summary>
    /// <param name="ct">Cancels the write.</param>
    internal async Task<Guid> CreateUserAsync(CancellationToken ct)
    {
        var owner = new TestCurrentUser();
        var user = await new UserBuilder(Database, owner).CreateAsync(ct: ct);

        return user.Id;
    }

    /// <summary>
    /// Inserts one real <c>channel_connections</c> row whose two cipher columns are protected under the row's own
    /// purpose <c>tokens.v1/{connectionId}</c>, exactly what the connect write stores.
    /// </summary>
    /// <param name="userId">The owner.</param>
    /// <param name="status">The row's state; <c>active</c> is the only state an upload may use.</param>
    /// <param name="expiresIn">How long the access token has left.</param>
    /// <param name="ct">Cancels the write.</param>
    internal async Task<SeededChannel> SeedChannelAsync(
        Guid userId,
        ChannelConnectionStatus status = ChannelConnectionStatus.Active,
        TimeSpan? expiresIn = null,
        CancellationToken ct = default)
    {
        var id = Guid.CreateVersion7();
        var purpose = id.ToString("D", CultureInfo.InvariantCulture);
        var accessCipher = Protector.Protect(purpose, AccessToken);
        var refreshCipher = Protector.Protect(purpose, RefreshToken);
        var expiresAt = Clock.UtcNow + (expiresIn ?? TimeSpan.FromHours(1));

        await Database.ExecuteAsync(
            "INSERT INTO channel_connections "
            + "(id, user_id, platform, external_account_id, display_name, status, access_token_enc, "
            + "refresh_token_enc, token_expires_at, scope, oauth_client_id, last_verified_at, connected_at, revoked_at) "
            + $"VALUES ('{id}', '{userId}', 'youtube', 'UC-{id:N}', {Literal(ChannelTitle)}, "
            + $"'{Wire(status)}', {Literal(accessCipher)}, {Literal(refreshCipher)}, {Instant(expiresAt)}, "
            + $"{Literal(Scope)}, '{OAuthClientId}', {Instant(Clock.UtcNow.AddHours(-1))}, "
            + $"{Instant(Clock.UtcNow.AddDays(-10))}, NULL)",
            ct);

        return new SeededChannel(id, userId);
    }

    /// <summary>
    /// Creates one ready episode of its own series, with a <c>final_video</c> asset of
    /// <paramref name="sizeBytes"/> already pointed at by <c>episodes.final_video_asset_id</c> — the shape the ingest
    /// leaves behind and the one the upload job reads its title, storage key and length from.
    /// </summary>
    /// <param name="userId">The owner.</param>
    /// <param name="sizeBytes">The asset's declared size, which the upload must send as the object's length.</param>
    /// <param name="title">The episode title, or the canary this file declares.</param>
    /// <param name="ct">Cancels the writes.</param>
    internal async Task<SeededEpisode> SeedEpisodeAsync(
        Guid userId,
        long sizeBytes,
        string? title = null,
        CancellationToken ct = default)
    {
        var actor = new TestCurrentUser { Id = userId };
        var seriesId = await SeriesForAsync(userId, actor, ct);

        var episode = await new EpisodeBuilder(Database, actor, Clock).CreateAsync(
            userId,
            seriesId,
            brief: "Nội dung tập canary 06.2.3-T2.",
            title: title ?? EpisodeTitle,
            status: EpisodeStatus.Ready,
            createdAt: Start,
            ct: ct);

        var asset = await new EpisodeBuilder(Database, actor, Clock).CreateAssetAsync(
            userId,
            episode.Id,
            kind: AssetKind.Video,
            sizeBytes: sizeBytes,
            ct: ct);

        await new EpisodeBuilder(Database, actor, Clock)
            .AttachFinalVideoAsync(userId, episode.Id, asset.Id, ct);

        Blobs.Seed(asset.StorageKey, sizeBytes);

        return new SeededEpisode(episode.Id, asset.Id, asset.StorageKey);
    }

    /// <summary>Inserts one <c>publications</c> row of <paramref name="episodeId"/> and returns its id.</summary>
    /// <param name="userId">The owner.</param>
    /// <param name="episodeId">The episode it publishes.</param>
    /// <param name="channelConnectionId">The connection it publishes through.</param>
    /// <param name="status">The lifecycle state.</param>
    /// <param name="consent">Whether <c>consent_at</c> is set.</param>
    /// <param name="platformPayloadJson">The <c>platform_payload</c> document, or this file's default plan.</param>
    /// <param name="nextAttemptAt">A pre-set <c>next_attempt_at</c>, for the early-delivery guard.</param>
    /// <param name="errorCode">A pre-set <c>error_code</c>.</param>
    /// <param name="externalPostId">A pre-set <c>external_post_id</c>, for the reconcile case.</param>
    /// <param name="privacy">The row's <c>privacy</c> column.</param>
    /// <param name="publishAt">The row's <c>publish_at</c> column.</param>
    /// <param name="ct">Cancels the write.</param>
    internal async Task<Guid> SeedPublicationAsync(
        Guid userId,
        Guid episodeId,
        Guid channelConnectionId,
        PublicationStatus status = PublicationStatus.Scheduled,
        bool consent = true,
        string? platformPayloadJson = null,
        DateTimeOffset? nextAttemptAt = null,
        string? errorCode = null,
        string? externalPostId = null,
        string privacy = "private",
        DateTimeOffset? publishAt = null,
        CancellationToken ct = default)
    {
        var id = Guid.CreateVersion7();

        await Database.ExecuteAsync(
            "INSERT INTO publications (id, user_id, episode_id, channel_connection_id, platform, status, title, "
            + "description, tags, privacy, publish_at, veto_ends_at, contains_synthetic_media, "
            + "self_declared_made_for_kids, published_at, platform_payload, created_at, updated_at, consent_at, "
            + "consent_text_version, consent_source, next_attempt_at, external_post_id, external_url, error_code) VALUES ("
            + $"'{id}', '{userId}', '{episodeId}', '{channelConnectionId}', 'youtube', '{Wire(status)}', "
            + $"{Literal(EpisodeTitle)}, {Literal(Description)}, ARRAY['canary']::text[], {Literal(privacy)}, "
            + $"{Instant(publishAt)}, NULL, true, false, NULL, "
            + $"{Json(platformPayloadJson ?? DefaultPlatformPayload)}, {Instant(Start)}, {Instant(Start)}, "
            + $"{Instant(consent ? Start : null)}, {(consent ? Literal("pc.v1") : "NULL")}, "
            + $"{(consent ? Literal("user") : "NULL")}, {Instant(nextAttemptAt)}, "
            + $"{NullableLiteral(externalPostId)}, NULL, {NullableLiteral(errorCode)})",
            ct);

        return id;
    }

    /// <summary>Seeds the ledger's row of a Pacific quota day, so a test starts from a known counter.</summary>
    /// <param name="day">The Pacific quota day.</param>
    /// <param name="unitsUsed">The units already spent.</param>
    /// <param name="unitsLimit">The day's limit.</param>
    /// <param name="ct">Cancels the write.</param>
    internal Task SeedQuotaAsync(
        DateOnly day,
        int unitsUsed,
        int unitsLimit,
        CancellationToken ct = default) =>
        Database.ExecuteAsync(
            "INSERT INTO platform_quota_usage (platform, quota_day, units_used, units_limit, created_at, updated_at) "
            + $"VALUES ('youtube', '{PublicationPayload.FormatQuotaDay(day)}', {unitsUsed}, {unitsLimit}, "
            + $"{Instant(Clock.UtcNow)}, {Instant(Clock.UtcNow)}) "
            + "ON CONFLICT (platform, quota_day) DO UPDATE SET units_used = EXCLUDED.units_used, "
            + "units_limit = EXCLUDED.units_limit",
            ct);

    /// <summary>The ledger's <c>units_used</c> of one Pacific day, or null when the day has no row.</summary>
    /// <param name="day">The Pacific quota day.</param>
    internal async Task<int?> QuotaUnitsAsync(DateOnly day)
    {
        var rows = await Database.QueryStringsAsync(
            "SELECT units_used::text FROM platform_quota_usage "
            + $"WHERE platform = 'youtube' AND quota_day = '{PublicationPayload.FormatQuotaDay(day)}'");

        return rows.Count == 0 ? null : int.Parse(rows[0], CultureInfo.InvariantCulture);
    }

    /// <summary>The quota day of the harness clock, the day every reservation of a run is booked on.</summary>
    internal DateOnly QuotaDayToday() => PacificQuotaDay.Day(Clock.UtcNow);

    /// <summary>Runs the job once, the way Hangfire activates it: a fresh scope, then <c>RunAsync</c>.</summary>
    /// <param name="publicationId">The row to publish.</param>
    /// <param name="ct">Cancels the run.</param>
    internal async Task RunAsync(Guid publicationId, CancellationToken ct)
    {
        using var scope = Host.CreateScope();

        await scope.ServiceProvider
            .GetRequiredService<PublishYouTubeJob>()
            .RunAsync(publicationId, ct);
    }

    /// <summary>
    /// Runs one scheduler attempt of the job, publishing the attempt number the Worker's job filter publishes, so a
    /// job that tells "retryable" from "last chance" sees the same numbers a deployment would hand it.
    /// </summary>
    /// <param name="publicationId">The row to publish.</param>
    /// <param name="attempt">The 1-based attempt now running.</param>
    /// <param name="maxAttempts">How many attempts the job's policy allows.</param>
    /// <param name="ct">Cancels the run.</param>
    internal async Task RunAttemptAsync(Guid publicationId, int attempt, int maxAttempts, CancellationToken ct)
    {
        using var attemptScope = NightTales.Application.Abstractions.Jobs.JobAttemptScope.Set(attempt, maxAttempts);

        await RunAsync(publicationId, ct);
    }

    /// <summary>
    /// Runs the command the job runs, so a test can assert the typed outcome the job only logs
    /// (<see cref="UploadOutcome"/>).
    /// </summary>
    /// <remarks>
    /// It takes the job's own path, not Mediator's: the owner comes from the allow-listed cross-user read, the scope is
    /// pinned with <see cref="ISystemContext.For"/> and the handler is called directly. <c>ISender</c> is deliberately
    /// not used — <c>AuthorizationBehavior</c> is deny-by-default for HTTP callers and answers <c>unauthorized</c> for a
    /// job-dispatched message that carries no principal, which is why the job itself bypasses it.
    /// </remarks>
    /// <param name="publicationId">The row to publish.</param>
    /// <param name="ct">Cancels the run.</param>
    internal async Task<ErrorOr<UploadOutcome>> SendAsync(Guid publicationId, CancellationToken ct)
    {
        // Two scopes, exactly as PublishYouTubeJob does it: the owner read runs in its own scope (ISystemWorkQuery
        // reads through the scope's IAppDbContext, which fixes that scope's tenant), and the command runs in a second
        // one, pinned before anything — a single scope would trip "ISystemContext.For must run before any DbContext".
        Guid? owner = null;

        using (var read = Host.CreateScope())
        {
            await foreach (var (_, userId) in read.ServiceProvider
                .GetRequiredService<ISystemWorkQuery>()
                .PendingAsync<Publication>(publication => publication.Id == publicationId, 1, ct)
                .ConfigureAwait(false))
            {
                owner = userId;
            }
        }

        if (owner is not { } ownerId)
        {
            throw new InvalidOperationException(
                $"The publication '{publicationId}' is not readable through the system work port, so no command can be sent for it.");
        }

        using var scope = Host.CreateScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(ownerId);

        return await scope.ServiceProvider
            .GetRequiredService<PublishYouTubeHandler>()
            .Handle(new PublishYouTubeCommand(publicationId), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Delivers every due outbox row with the host's own dispatcher, so a subscriber runs.</summary>
    /// <param name="ct">Cancels the delivery.</param>
    internal Task<int> DeliverOutboxAsync(CancellationToken ct) => Host.RunOutboxOnceAsync(ct);

    /// <summary>The host's inline scheduler: every enqueue and schedule call the host recorded.</summary>
    internal InlineJobScheduler Jobs => (InlineJobScheduler)Host.Scheduler;

    /// <summary>Runs every job the host scheduled but has not run yet, the way Hangfire would.</summary>
    /// <param name="ct">Cancels the runs.</param>
    internal Task<int> RunPendingJobsAsync(CancellationToken ct) => Jobs.RunPendingAsync(ct);

    /// <summary>The row's mutable state as one comparable text, so "unchanged" is one comparison.</summary>
    /// <param name="publicationId">The row.</param>
    internal Task<string> PublicationRowAsync(Guid publicationId) =>
        ScalarAsync(
            "SELECT status || '|' || coalesce(external_post_id, '<null>') || '|' || "
            + "coalesce(next_attempt_at::text, '<null>') || '|' || coalesce(error_code, '<null>') || '|' || "
            + "coalesce(platform_payload::text, '<null>') || '|' || coalesce(consent_at::text, '<null>') "
            + $"FROM publications WHERE id = '{publicationId}'");

    /// <summary>The row's <c>status</c>.</summary>
    /// <param name="publicationId">The row.</param>
    internal Task<string> StatusAsync(Guid publicationId) =>
        ScalarAsync($"SELECT status FROM publications WHERE id = '{publicationId}'");

    /// <summary>The row's <c>error_code</c> column (empty when null).</summary>
    /// <param name="publicationId">The row.</param>
    internal Task<string> ErrorCodeAsync(Guid publicationId) =>
        ScalarAsync($"SELECT coalesce(error_code, '') FROM publications WHERE id = '{publicationId}'");

    /// <summary>The row's <c>external_post_id</c> column (empty when null).</summary>
    /// <param name="publicationId">The row.</param>
    internal Task<string> ExternalPostIdAsync(Guid publicationId) =>
        ScalarAsync($"SELECT coalesce(external_post_id, '') FROM publications WHERE id = '{publicationId}'");

    /// <summary>The row's <c>next_attempt_at</c>, or null when the column is null.</summary>
    /// <remarks>
    /// The instant is normalised to a zero offset: PostgreSQL renders a <c>timestamptz</c> in the connection's session
    /// zone, so the text may carry the server's own offset (this workstation's is <c>+07</c>), while a
    /// <see cref="FakeClock"/> set from it must keep answering the UTC instants a deployment's clock answers.
    /// </remarks>
    /// <param name="publicationId">The row.</param>
    internal async Task<DateTimeOffset?> NextAttemptAtAsync(Guid publicationId)
    {
        var raw = await ScalarAsyncOrNull($"SELECT next_attempt_at::text FROM publications WHERE id = '{publicationId}'");

        // The test kit renders a NULL scalar as an empty string, so blank is "the column is null" here.
        return string.IsNullOrWhiteSpace(raw)
            ? null
            : DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    }

    /// <summary>The row's whole <c>platform_payload</c> document (empty when null).</summary>
    /// <param name="publicationId">The row.</param>
    internal Task<string> PlatformPayloadAsync(Guid publicationId) =>
        ScalarAsync($"SELECT coalesce(platform_payload::text, '') FROM publications WHERE id = '{publicationId}'");

    /// <summary>One key of the row's <c>platform_payload</c> (empty when the key is absent).</summary>
    /// <param name="publicationId">The row.</param>
    /// <param name="key">The top-level key.</param>
    internal Task<string> PayloadKeyAsync(Guid publicationId, string key) =>
        ScalarAsync(
            $"SELECT coalesce(platform_payload->>'{key}', '') FROM publications WHERE id = '{publicationId}'");

    /// <summary>The JSON kind of one <c>platform_payload</c> key (<c>string</c>, <c>object</c>, …, empty when absent).</summary>
    /// <param name="publicationId">The row.</param>
    /// <param name="key">The top-level key.</param>
    internal Task<string> PayloadKeyKindAsync(Guid publicationId, string key) =>
        ScalarAsync(
            $"SELECT coalesce(jsonb_typeof(platform_payload->'{key}'), '') FROM publications "
            + $"WHERE id = '{publicationId}'");

    /// <summary>The stored <c>platform_payload.uploadSessionEnc</c> cipher (empty when the key is absent).</summary>
    /// <param name="publicationId">The row.</param>
    internal Task<string> SessionCipherAsync(Guid publicationId) =>
        PayloadKeyAsync(publicationId, PublicationPayload.UploadSessionEncKey);

    /// <summary>
    /// Builds a <c>platform_payload</c> that names a session and a reserved quota day, the shape a run that died
    /// after its session create leaves behind: the cipher is the host's own, under the row's purpose, so the job
    /// unprotects a value a deployment would have written.
    /// </summary>
    /// <param name="channelId">The connection the purpose is derived from.</param>
    /// <param name="sessionUri">The plaintext session URI the row stores in cipher.</param>
    /// <param name="quotaDay">The Pacific day of the outstanding reservation.</param>
    /// <param name="ct">Cancels the write (unused: the payload is built in memory).</param>
    internal Task<string> PayloadForSessionAsync(
        Guid channelId,
        string sessionUri,
        DateOnly quotaDay,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var cipher = Protector.Protect(channelId.ToString("D", CultureInfo.InvariantCulture), sessionUri);
        var withSession = PublicationPayload.WithKey(
            DefaultPlatformPayload,
            PublicationPayload.UploadSessionEncKey,
            cipher);

        return Task.FromResult(
            PublicationPayload.WithKey(
                withSession,
                PublicationPayload.QuotaReservedDayKey,
                PublicationPayload.FormatQuotaDay(quotaDay))
            ?? DefaultPlatformPayload);
    }

    /// <summary>How many outbox rows of one type this test's own publication raised.</summary>
    /// <param name="type">The <c>outbox_messages.type</c>.</param>
    /// <param name="publicationId">A publication id the payload must name.</param>
    internal async Task<int> OutboxCountAsync(string type, Guid publicationId) =>
        int.Parse(
            await ScalarAsync(
                "SELECT count(*)::text FROM outbox_messages "
                + $"WHERE type = '{type}' AND payload::text LIKE '%{publicationId}%'"),
            CultureInfo.InvariantCulture);

    /// <summary>How many outbox rows of one type this test's client raised, whatever publication they name.</summary>
    /// <remarks>
    /// An incident event names the OAuth client and the incident, never a publication, so the per-publication filter
    /// of <see cref="OutboxCountAsync(string, Guid)"/> cannot see it.
    /// </remarks>
    /// <param name="type">The <c>outbox_messages.type</c>.</param>
    internal async Task<int> OutboxCountAsync(string type) =>
        int.Parse(
            await ScalarAsync($"SELECT count(*)::text FROM outbox_messages WHERE type = '{type}'"),
            CultureInfo.InvariantCulture);

    /// <summary>The outbox rows of one type this test's publication raised, as payload texts.</summary>
    /// <param name="type">The <c>outbox_messages.type</c>.</param>
    /// <param name="publicationId">A publication id the payload must name.</param>
    internal Task<IReadOnlyList<string>> OutboxPayloadsAsync(string type, Guid publicationId) =>
        Database.QueryStringsAsync(
            "SELECT payload::text FROM outbox_messages "
            + $"WHERE type = '{type}' AND payload::text LIKE '%{publicationId}%' ORDER BY occurred_at");

    /// <summary>How many <c>platform_incidents</c> rows the fake's client has, open or cleared.</summary>
    internal async Task<int> IncidentCountAsync() =>
        int.Parse(await ScalarAsync("SELECT count(*)::text FROM platform_incidents"), CultureInfo.InvariantCulture);

    /// <summary>How many of the client's incident rows are still open.</summary>
    internal async Task<int> OpenIncidentCountAsync() =>
        int.Parse(
            await ScalarAsync(
                "SELECT count(*)::text FROM platform_incidents "
                + $"WHERE platform = 'youtube' AND oauth_client_id = '{OAuthClientId}' AND cleared_at IS NULL"),
            CultureInfo.InvariantCulture);

    /// <summary>Raises one open incident for the fake's client, outside any job (the 06.4.2-T1 path's shape).</summary>
    /// <param name="errorCode">The stored <c>error_code</c>.</param>
    /// <param name="ct">Cancels the write.</param>
    internal Task SeedOpenIncidentAsync(string errorCode = "invalid_client", CancellationToken ct = default) =>
        Database.ExecuteAsync(
            "INSERT INTO platform_incidents (id, platform, oauth_client_id, error_code, raised_at) "
            + $"VALUES ('{Guid.CreateVersion7()}', 'youtube', '{OAuthClientId}', '{errorCode}', "
            + $"{Instant(Clock.UtcNow)})",
            ct);

    /// <summary>Clears every open incident of the client, the way 06.2.3-T3's operator command would.</summary>
    /// <param name="ct">Cancels the write.</param>
    internal Task ClearIncidentsAsync(CancellationToken ct = default) =>
        Database.ExecuteAsync(
            "UPDATE platform_incidents SET cleared_at = now(), cleared_by = 'operator', note = 'canary clear' "
            + $"WHERE platform = 'youtube' AND oauth_client_id = '{OAuthClientId}' AND cleared_at IS NULL",
            ct);

    /// <summary>The connection row's mutable state as one comparable text.</summary>
    /// <param name="channelId">The connection.</param>
    internal Task<string> ChannelRowAsync(Guid channelId) =>
        ScalarAsync(
            "SELECT status || '|' || coalesce(access_token_enc, '<null>') || '|' || "
            + "coalesce(refresh_token_enc, '<null>') || '|' || token_expires_at::text "
            + $"FROM channel_connections WHERE id = '{channelId}'");

    /// <summary>The connection's <c>status</c>.</summary>
    /// <param name="channelId">The connection.</param>
    internal Task<string> ChannelStatusAsync(Guid channelId) =>
        ScalarAsync($"SELECT status FROM channel_connections WHERE id = '{channelId}'");

    /// <summary>Moves the connection's expiry, so the next run must refresh (or fail to).</summary>
    /// <param name="channelId">The connection.</param>
    /// <param name="expiresAt">The new <c>token_expires_at</c>.</param>
    /// <param name="ct">Cancels the write.</param>
    internal Task ExpireTokenAsync(Guid channelId, DateTimeOffset expiresAt, CancellationToken ct = default) =>
        Database.ExecuteAsync(
            $"UPDATE channel_connections SET token_expires_at = {Instant(expiresAt)} WHERE id = '{channelId}'",
            ct);

    /// <summary>How many <c>audit_logs</c> rows mention <paramref name="needle"/> anywhere in their columns.</summary>
    /// <param name="needle">The text that must never be written.</param>
    internal async Task<int> AuditRowsMentioningAsync(string needle) =>
        int.Parse(
            await ScalarAsync(
                "SELECT count(*)::text FROM audit_logs "
                + $"WHERE to_jsonb(audit_logs)::text LIKE {Literal($"%{needle}%")}"),
            CultureInfo.InvariantCulture);

    /// <summary>How many outbox payloads mention <paramref name="needle"/> anywhere.</summary>
    /// <param name="needle">The text that must never be written.</param>
    internal async Task<int> OutboxRowsMentioningAsync(string needle) =>
        int.Parse(
            await ScalarAsync(
                "SELECT count(*)::text FROM outbox_messages "
                + $"WHERE payload::text LIKE {Literal($"%{needle}%")}"),
            CultureInfo.InvariantCulture);

    /// <summary>The wire value of a publication status (the CHECK constraint's spelling).</summary>
    /// <param name="status">The state.</param>
    internal static string Wire(PublicationStatus status) =>
        System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString());

    /// <summary>The wire value of a connection status.</summary>
    /// <param name="status">The state.</param>
    internal static string Wire(ChannelConnectionStatus status) => status switch
    {
        ChannelConnectionStatus.Active => "active",
        ChannelConnectionStatus.Revoked => "revoked",
        ChannelConnectionStatus.Expired => "expired",
        ChannelConnectionStatus.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown connection status."),
    };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        await Google.DisposeAsync();
    }

    private async Task<string> ScalarAsync(string sql) => (await ScalarAsyncOrNull(sql)) ?? string.Empty;

    private async Task<string?> ScalarAsyncOrNull(string sql)
    {
        var rows = await Database.QueryStringsAsync(sql);

        return rows.Count == 0 ? null : rows[0];
    }

    private async Task<Guid> SeriesForAsync(Guid userId, TestCurrentUser actor, CancellationToken ct)
    {
        if (_seriesByUser.TryGetValue(userId, out var existing))
        {
            return existing;
        }

        var series = await new SeriesBuilder(Database, actor, Clock).CreateAsync(
            userId,
            title: "Chuỗi canary 06.2.3-T2",
            createdAt: Start,
            ct: ct);

        _seriesByUser[userId] = series.Id;

        return series.Id;
    }

    private static string Literal(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string NullableLiteral(string? value) => value is null ? "NULL" : Literal(value);

    private static string Instant(DateTimeOffset? value) =>
        value is null
            ? "NULL"
            : Literal(value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    private static string Json(string value) => $"{Literal(value)}::jsonb";

    /// <summary>One seeded connection: the id the row names and its owner.</summary>
    /// <param name="Id">The <c>channel_connections.id</c>.</param>
    /// <param name="UserId">The owner.</param>
    internal sealed record SeededChannel(Guid Id, Guid UserId);

    /// <summary>One seeded episode with its final video asset.</summary>
    /// <param name="EpisodeId">The <c>episodes.id</c>.</param>
    /// <param name="AssetId">The <c>assets.id</c> the episode's final video points at.</param>
    /// <param name="StorageKey">The asset's object key in the media bucket.</param>
    internal sealed record SeededEpisode(Guid EpisodeId, Guid AssetId, string StorageKey);
}

/// <summary>
/// The <see cref="IBlobStore"/> the upload tests run against: it answers an open of the seeded object with a fresh
/// stream over a deterministic payload, counts every open and every disposal of the stream it handed out, and can
/// report a length that disagrees with the row's <c>size_bytes</c> (the CR-18 cross-check).
/// </summary>
internal sealed class RecordingBlobStore : IBlobStore
{
    private readonly Dictionary<string, long> _objects = new(StringComparer.Ordinal);
    private readonly List<long> _rangeOpens = [];
    private int _openReads;
    private int _disposals;

    /// <summary>Creates a store that answers objects of <paramref name="defaultLength"/> bytes when unfed.</summary>
    /// <param name="defaultLength">The length every key answers with unless <see cref="Seed"/> named one.</param>
    internal RecordingBlobStore(long defaultLength) => DefaultLength = defaultLength;

    /// <summary>The length an unseeded key answers with.</summary>
    internal long DefaultLength { get; }

    /// <summary>How many times an object was opened for reading.</summary>
    internal int OpenReads => Volatile.Read(ref _openReads);

    /// <summary>How many times a stream this store handed out was disposed.</summary>
    internal int Disposals => Volatile.Read(ref _disposals);

    /// <summary>A length the store reports regardless of the row, or null to report the seeded one.</summary>
    internal long? LengthOverride { get; set; }

    /// <summary>
    /// The <c>fromByte</c> of every ranged open, in order (PERF-2): a resume must ask the store for the slice Google
    /// still needs instead of reading the whole object and discarding the prefix.
    /// </summary>
    internal IReadOnlyList<long> RangeOpens
    {
        get
        {
            lock (_objects)
            {
                return [.. _rangeOpens];
            }
        }
    }

    /// <summary>
    /// The lowest absolute byte offset this store ever handed out, or null when nothing was read: a resume that reads
    /// from byte 0 shows up here as 0 however the caller later positions the stream.
    /// </summary>
    internal long? FirstByteRead => Interlocked.Read(ref _firstByteReadOffset);

    private long _firstByteReadOffset = -1;

    /// <summary>The largest read the caller asked for, so "streamed, not buffered" stays assertable.</summary>
    internal long MaxRequestedBytes { get; private set; }

    /// <summary>Places one object in the store without recording an open.</summary>
    /// <remarks>
    /// Keyed by the object key alone: the bucket is the production code's business (<c>BlobLocator</c> resolves it from
    /// the storage settings), and a store that insisted on a bucket name would pin an implementation detail the job is
    /// free to compute differently.
    /// </remarks>
    /// <param name="key">The object key (<c>assets.storage_key</c>).</param>
    /// <param name="length">The object's length in bytes.</param>
    internal void Seed(string key, long length)
    {
        lock (_objects)
        {
            _objects[key] = length;
        }
    }

    /// <inheritdoc />
    public Task<ErrorOr<BlobObject>> OpenReadAsync(string bucket, string key, CancellationToken cancellationToken) =>
        OpenAsync(key, fromByte: 0, ranged: false);

    /// <inheritdoc />
    public Task<ErrorOr<BlobObject>> OpenReadAsync(
        string bucket,
        string key,
        long fromByte,
        CancellationToken cancellationToken) =>
        OpenAsync(key, fromByte, ranged: true);

    /// <summary>
    /// Opens one seeded object, optionally as the slice from <paramref name="fromByte"/> on — the shape the production
    /// store answers a ranged read with: a body that starts at that byte while <see cref="BlobObject.Length"/> stays
    /// the whole object's size.
    /// </summary>
    /// <param name="key">The object key.</param>
    /// <param name="fromByte">The first byte of the slice.</param>
    /// <param name="ranged">Whether the caller asked for a slice (recorded either way).</param>
    /// <returns>The opened object.</returns>
    private Task<ErrorOr<BlobObject>> OpenAsync(string key, long fromByte, bool ranged)
    {
        Interlocked.Increment(ref _openReads);

        long length;
        lock (_objects)
        {
            length = LengthOverride ?? (_objects.TryGetValue(key, out var seeded) ? seeded : DefaultLength);

            if (ranged)
            {
                _rangeOpens.Add(fromByte);
            }
        }

        // The store's body is one seekable stream over the whole object, pre-positioned at the requested byte and
        // reporting the object's full length — the shape the S3 range read reports (a `ContentRange` total), which
        // the handler's positioning fallback tolerates because the position is already the requested one.
        var payload = Payload(length);
        payload.Seek(fromByte, SeekOrigin.Begin);

        var content = new DisposalCountingStream(
            new MeasuringStream(payload, this, origin: 0),
            () => Interlocked.Increment(ref _disposals));

        return Task.FromResult<ErrorOr<BlobObject>>(new BlobObject(content, "video/mp4", length));
    }

    /// <summary>Forgets the ranged opens and the lowest byte handed out, so a test can assert one resume only.</summary>
    internal void ResetReads()
    {
        lock (_objects)
        {
            _rangeOpens.Clear();
            Interlocked.Exchange(ref _firstByteReadOffset, -1);
        }
    }

    /// <summary>Records the lowest absolute byte offset handed out (the PERF-2 assertion).</summary>
    /// <param name="absoluteOffset">The absolute offset the stream is about to serve.</param>
    private void RecordFirstByte(long absoluteOffset)
    {
        var current = Interlocked.Read(ref _firstByteReadOffset);

        if (current >= 0 && current <= absoluteOffset)
        {
            return;
        }

        Interlocked.CompareExchange(ref _firstByteReadOffset, absoluteOffset, current);
    }

    /// <inheritdoc />
    public Task<ErrorOr<BlobInfo>> PutAsync(
        string bucket,
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("the upload job never writes the store.");

    /// <inheritdoc />
    public Task<ErrorOr<Success>> DeleteAsync(string bucket, string key, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the upload job never deletes from the store.");

    /// <inheritdoc />
    public Task<ErrorOr<bool>> ExistsAsync(string bucket, string key, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the upload job never probes the store.");

    /// <inheritdoc />
    public IAsyncEnumerable<string> ListKeysAsync(string bucket, string prefix, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the upload job never lists the store.");

    /// <inheritdoc />
    public ErrorOr<Uri> CreateReadUrl(string bucket, string key, TimeSpan ttl, string? downloadFileName) =>
        throw new NotSupportedException("the upload job never presigns.");

    private void Record(long requested)
    {
        if (requested > MaxRequestedBytes)
        {
            MaxRequestedBytes = requested;
        }
    }

    /// <summary>A deterministic payload: no zero-filled body, so a truncated stream is still detectable.</summary>
    /// <param name="length">The number of bytes.</param>
    private static MemoryStream Payload(long length)
    {
        var bytes = new byte[checked((int)length)];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(index % 251);
        }

        return new MemoryStream(bytes, writable: false);
    }

    /// <summary>Records the largest read the caller asked for while passing every read through.</summary>
    /// <param name="inner">The stream that holds the bytes.</param>
    /// <param name="owner">The store the measurement belongs to.</param>
    private sealed class MeasuringStream(Stream inner, RecordingBlobStore owner, long origin) : Stream
    {
        /// <inheritdoc />
        public override bool CanRead => inner.CanRead;

        /// <inheritdoc />
        public override bool CanSeek => inner.CanSeek;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => inner.Length;

        /// <inheritdoc />
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        /// <inheritdoc />
        public override void Flush() => inner.Flush();

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count)
        {
            owner.Record(count);
            owner.RecordFirstByte(origin + inner.Position);

            return inner.Read(buffer, offset, count);
        }

        /// <inheritdoc />
        public override int Read(Span<byte> buffer)
        {
            owner.Record(buffer.Length);
            owner.RecordFirstByte(origin + inner.Position);

            return inner.Read(buffer);
        }

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            owner.Record(buffer.Length);
            owner.RecordFirstByte(origin + inner.Position);

            return inner.ReadAsync(buffer, cancellationToken);
        }

        /// <inheritdoc />
        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            owner.Record(count);
            owner.RecordFirstByte(origin + inner.Position);

            return inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException("the stream is read-only.");

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("the stream is read-only.");

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Runs one action when its stream is disposed, so a test can assert the job's <c>finally</c>.</summary>
    /// <param name="inner">The stream that holds the bytes.</param>
    /// <param name="onDispose">Runs once per disposal.</param>
    private sealed class DisposalCountingStream(Stream inner, Action onDispose) : Stream
    {
        /// <inheritdoc />
        public override bool CanRead => inner.CanRead;

        /// <inheritdoc />
        public override bool CanSeek => inner.CanSeek;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => inner.Length;

        /// <inheritdoc />
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        /// <inheritdoc />
        public override void Flush() => inner.Flush();

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        /// <inheritdoc />
        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        /// <inheritdoc />
        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException("the stream is read-only.");

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("the stream is read-only.");

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                onDispose();
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// Keeps every line the Worker host rendered — <see cref="EventId"/>, level, category, message and exception — which is
/// how a redaction check reads what the whole process wrote rather than what one component claims to have written
/// (06 README D17). Framework categories included: the upload client's request logging is filtered to
/// <c>LogLevel.None</c> by the product, so no line may carry the session URI even here.
/// </summary>
internal sealed class CapturingJobLogs : ILoggerProvider
{
    private readonly List<CapturedJobLog> _lines = [];
    private readonly Lock _gate = new();

    /// <summary>A snapshot of every line received so far.</summary>
    internal List<CapturedJobLog> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    /// <summary>Every captured line as one text: <see cref="EventId"/>, message and exception.</summary>
    internal string AllText() =>
        string.Join('\n', Lines.Select(line =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"{line.EventId} {line.Category} {line.Message} {line.Exception}")));

    /// <summary>How many lines carry <paramref name="eventId"/>.</summary>
    /// <param name="eventId">The <c>[LoggerMessage]</c> id.</param>
    internal int CountOf(int eventId) => Lines.Count(line => line.EventId == eventId);

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release: the lines are the sink.
    }

    private void Capture(CapturedJobLog line)
    {
        lock (_gate)
        {
            _lines.Add(line);
        }
    }

    /// <summary>One category's sink.</summary>
    /// <param name="owner">The capture the lines go to.</param>
    /// <param name="category">The category the host asked for.</param>
    private sealed class Sink(CapturingJobLogs owner, string category) : ILogger
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            owner.Capture(new CapturedJobLog(
                eventId.Id,
                logLevel,
                category,
                formatter(state, exception),
                exception?.ToString() ?? string.Empty));
        }
    }
}

/// <summary>One captured log line.</summary>
/// <param name="EventId">The <c>[LoggerMessage]</c> id, or 0 for a framework line.</param>
/// <param name="Level">The level it was written at.</param>
/// <param name="Category">The logger category.</param>
/// <param name="Message">The rendered message.</param>
/// <param name="Exception">The rendered exception, or an empty string.</param>
internal sealed record CapturedJobLog(int EventId, LogLevel Level, string Category, string Message, string Exception);

/// <summary>
/// Watches whether a database transaction is open while an outbound Google call runs (06.2.3-T2 §Performance, SEC-8,
/// QA D3): the run's unit of work reports its begin/end here, and a decorator over
/// <see cref="IYouTubeClient"/> records the depth it saw when <c>videos.insert</c> was called, so "no transaction — and
/// so no publication lock — is held across the create" becomes an assertion instead of a claim.
/// </summary>
internal sealed class TransactionProbe
{
    private int _open;
    private int _violations;

    /// <summary>How many transactions are open right now.</summary>
    internal int Open => Volatile.Read(ref _open);

    /// <summary>How many session creates ran with a transaction open: always zero in a correct run.</summary>
    internal int Violations => Volatile.Read(ref _violations);

    /// <summary>Records a transaction opening.</summary>
    internal void Enter() => Interlocked.Increment(ref _open);

    /// <summary>Records a transaction ending.</summary>
    internal void Exit() => Interlocked.Decrement(ref _open);

    /// <summary>Records what the depth was when a session create was issued.</summary>
    internal void RecordSessionCreate()
    {
        if (Open > 0)
        {
            Interlocked.Increment(ref _violations);
        }
    }
}

/// <summary>
/// Forwards <see cref="IUnitOfWork"/> to the real implementation and reports every begin and end to a
/// <see cref="TransactionProbe"/>.
/// </summary>
internal sealed class ProbingUnitOfWork(IUnitOfWork inner, TransactionProbe probe) : IUnitOfWork
{
    private bool _open;

    /// <inheritdoc />
    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        await inner.BeginAsync(cancellationToken).ConfigureAwait(false);

        _open = true;
        probe.Enter();
    }

    /// <inheritdoc />
    public async Task<ErrorOr<Success>> CommitAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await inner.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Close();
        }
    }

    /// <inheritdoc />
    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        try
        {
            await inner.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Close();
        }
    }

    private void Close()
    {
        if (!_open)
        {
            return;
        }

        _open = false;
        probe.Exit();
    }
}

/// <summary>
/// Forwards <see cref="IYouTubeClient"/> to the real client and tells a <see cref="TransactionProbe"/> when a session
/// create is issued, so the test can assert that no transaction was open at that moment.
/// </summary>
internal sealed class ProbingYouTubeClient(IYouTubeClient inner, TransactionProbe probe) : IYouTubeClient
{
    /// <inheritdoc />
    public string BuildAuthorizationUrl(
        OAuthClient client,
        OAuthAuthorizationRequest request,
        string state,
        string? codeChallenge) => inner.BuildAuthorizationUrl(client, request, state, codeChallenge);

    /// <inheritdoc />
    public Task<ErrorOr<OAuthTokens>> ExchangeCodeAsync(
        OAuthClient client,
        string code,
        string? codeVerifier,
        string redirectUri,
        CancellationToken ct) => inner.ExchangeCodeAsync(client, code, codeVerifier, redirectUri, ct);

    /// <inheritdoc />
    public Task<ErrorOr<OAuthTokens>> RefreshAsync(OAuthClient client, string refreshToken, CancellationToken ct) =>
        inner.RefreshAsync(client, refreshToken, ct);

    /// <inheritdoc />
    public Task<ErrorOr<RevokeOutcome>> RevokeAsync(string token, CancellationToken ct) =>
        inner.RevokeAsync(token, ct);

    /// <inheritdoc />
    public Task<ErrorOr<GoogleChannel>> GetMyChannelAsync(string accessToken, CancellationToken ct) =>
        inner.GetMyChannelAsync(accessToken, ct);

    /// <inheritdoc />
    public Task<ErrorOr<Uri>> StartUploadAsync(
        string accessToken,
        VideoInsertRequest request,
        long length,
        CancellationToken ct)
    {
        probe.RecordSessionCreate();

        return inner.StartUploadAsync(accessToken, request, length, ct);
    }

    /// <inheritdoc />
    public Task<ErrorOr<UploadResult>> ResumeUploadAsync(
        string accessToken,
        Uri sessionUri,
        Stream content,
        long length,
        IProgress<long>? progress,
        CancellationToken ct) => inner.ResumeUploadAsync(accessToken, sessionUri, content, length, progress, ct);

    /// <inheritdoc />
    public Task<ErrorOr<UploadProbe>> ProbeUploadAsync(
        string accessToken,
        Uri sessionUri,
        long length,
        CancellationToken ct) => inner.ProbeUploadAsync(accessToken, sessionUri, length, ct);

    /// <inheritdoc />
    public Task<ErrorOr<IReadOnlyList<YouTubeVideoStatus>>> GetVideosAsync(
        string accessToken,
        IReadOnlyList<string> ids,
        CancellationToken ct) => inner.GetVideosAsync(accessToken, ids, ct);
}
