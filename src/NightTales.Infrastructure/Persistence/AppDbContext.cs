using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Assets;
using NightTales.Domain.Audit;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Catalog;
using NightTales.Domain.Common;
using NightTales.Domain.Communication;
using NightTales.Domain.Compliance;
using NightTales.Domain.Compliance.Deletion;
using NightTales.Domain.Episodes;
using NightTales.Domain.Platform;
using NightTales.Domain.Publishing;
using NightTales.Domain.Rendering;
using NightTales.Domain.Rendering.Music;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Persistence.Conventions;
using NightTales.Infrastructure.Persistence.Entities;
using NightTales.Infrastructure.Persistence.Idempotency;
using NightTales.Infrastructure.Persistence.Outbox;
using NightTales.Infrastructure.Realtime;

namespace NightTales.Infrastructure.Persistence;

/// <summary>
/// The tracked write model (09 README D8). It is the only context that owns migrations and the only
/// one who saves changes; the tenant filter of 09.2.3-T1 reads <see cref="CurrentUserId"/>.
/// </summary>
public sealed class AppDbContext : IdentityDbContext<AppUser, AppRole, Guid>, IAppDbContext, ITenantFilterContext, IDataProtectionKeyContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser)
        : base(options)
    {
        // 09 README D10: the scope's user is captured here and the scope's actor provider is told that
        // a context now exists, so a late ISystemContext.For fails instead of reading nothing.
        (currentUser as ITenantScopeObserver)?.MarkObserved();
        CurrentUserId = currentUser.Id;
    }

    /// <summary>
    /// The owner every tenant-filtered query of this scope is restricted to (09 README D10);
    /// <c>null</c> for an anonymous scope, which therefore sees no user-owned row.
    /// </summary>
    public Guid? CurrentUserId { get; }

    public DbSet<Series> Series => Set<Series>();

    public DbSet<Episode> Episodes => Set<Episode>();

    public DbSet<StoryVersion> StoryVersions => Set<StoryVersion>();

    /// <summary>
    /// The paid generations of every kind (02 D2, 02.2.2-T1): inserted by the generate commands and advanced by
    /// <c>StoryGenerationProgress</c> and the sweeper. It is <c>IUserOwned</c>, so the D10 tenant filter applies.
    /// </summary>
    public DbSet<GenerationRun> GenerationRuns => Set<GenerationRun>();

    public DbSet<SeriesAutopilotAuthorization> SeriesAutopilotAuthorizations => Set<SeriesAutopilotAuthorization>();

    public DbSet<AutopilotRun> AutopilotRuns => Set<AutopilotRun>();

    public DbSet<ModerationEvent> ModerationEvents => Set<ModerationEvent>();

    /// <summary>The sources of an episode (02.5.1-T1, table <c>story_sources</c>); <c>IUserOwned</c>, so the D10 tenant filter applies.</summary>
    public DbSet<StorySource> StorySources => Set<StorySource>();

    /// <summary>The fetcher's work queue (02.5.1-T1, table <c>fetch_tasks</c>, 02 D10); <c>IUserOwned</c>, so the D10 tenant filter applies.</summary>
    public DbSet<FetchTask> FetchTasks => Set<FetchTask>();

    /// <summary>The append-only url-source creation ledger the 24 h cap counts (02.5.1-T1, table <c>source_creations</c>, CR-31); <c>IUserOwned</c>, so the D10 tenant filter applies.</summary>
    public DbSet<SourceCreation> SourceCreations => Set<SourceCreation>();

    /// <summary>The versioned fact sheets of one episode (02.5.3-T2, table <c>fact_sheets</c>); <c>IUserOwned</c>, so the D10 tenant filter applies.</summary>
    public DbSet<FactSheet> FactSheets => Set<FactSheet>();

    /// <summary>The source candidates a find-sources run proposed for one episode (02.7.2-T2, table <c>source_candidates</c>); <c>IUserOwned</c>, so the D10 tenant filter applies.</summary>
    public DbSet<SourceCandidate> SourceCandidates => Set<SourceCandidate>();

    /// <summary>The ideas an <c>ideas</c> run proposed for a series (02.8.1-T1, table <c>episode_ideas</c>); <c>IUserOwned</c>, so the D10 tenant filter applies.</summary>
    public DbSet<EpisodeIdea> EpisodeIdeas => Set<EpisodeIdea>();

    /// <summary>
    /// The one bible row of a series (02.6.3-T1, table <c>series_bibles</c>): created with its series by
    /// <c>CreateSeriesHandler</c> in the same transaction, replaced in place by
    /// <c>PUT /api/v1/series/{id}/bible</c>. <c>IUserOwned</c>, so the D10 tenant filter applies.
    /// </summary>
    public DbSet<SeriesBible> SeriesBibles => Set<SeriesBible>();

    /// <summary>The legal deletion duties of 13.3.1-T1 (table <c>data_deletion_requests</c>); <c>IUserOwned</c>, so the D10 tenant filter applies.</summary>
    public DbSet<DeletionRequest> DeletionRequests => Set<DeletionRequest>();

    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();

    /// <summary>The immutable rates and limits of 02.2.4-T1 (table <c>price_versions</c>); not tenant-owned.</summary>
    public DbSet<PriceVersion> PriceVersions => Set<PriceVersion>();

    public DbSet<Asset> Assets => Set<Asset>();

    public DbSet<Voice> Voices => Set<Voice>();

    public DbSet<StyleTemplate> StyleTemplates => Set<StyleTemplate>();

    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();

    /// <summary>
    /// The queued e-mails of 09.2.6-T3: written by the feature that asks for a message, read and mutated by
    /// <c>SendEmailJob</c> and read by <c>EmailDeliveryStatusQuery</c>. It is <c>IUserOwned</c>, so the D10
    /// tenant filter applies to every read.
    /// </summary>
    public DbSet<EmailMessage> EmailMessages => Set<EmailMessage>();

    /// <summary>
    /// The render-node registry of 11.3.3-T1 (table <c>worker_nodes</c>, 00 overview §6 L311): upserted by
    /// <c>WorkerNodeSeeder</c> from <c>RenderNodes:Nodes</c>, heartbeat- and state-mutated by
    /// <c>NodeHealthPoller</c>, and read by the <c>nts-worker nodes</c> verbs and (through <c>IReadDb</c>) by
    /// 11.3.3-T2's selector.
    /// </summary>
    /// <remarks>
    /// It carries no <c>IUserOwned</c>, so 09 README D10's tenant filter never applies and no
    /// <c>IgnoreQueryFilters</c> allow-list entry is needed — the registry is operator data, not user data.
    /// </remarks>
    public DbSet<WorkerNode> WorkerNodes => Set<WorkerNode>();

    /// <summary>
    /// The global music catalog of 05.3.2-T3 (table <c>music_tracks</c>, 05 README D8/P5): upserted by
    /// <c>MusicTrackSeeder</c> and read by <c>MusicSelector</c>'s caller. It carries no <c>IUserOwned</c>,
    /// so 09 README D10's tenant filter never applies and no <c>IgnoreQueryFilters</c> entry is needed.
    /// </summary>
    public DbSet<MusicTrack> MusicTracks => Set<MusicTrack>();

    /// <summary>
    /// The render attempts of an episode of 09.6.1-T1 (table <c>render_jobs</c>, BD5/WF13, 00 §6): inserted by
    /// <c>RenderEpisodeCommandHandler</c> and progressed by 09.6.3's submit/poll/ingest jobs on the same row.
    /// <c>IUserOwned</c>, so the D10 tenant filter applies.
    /// </summary>
    public DbSet<RenderJob> RenderJobs => Set<RenderJob>();

    /// <summary>
    /// The YouTube upload quota counter of 06.3.1-T1 (<c>platform_quota_usage</c>): written only by
    /// <c>YouTubeQuotaLedger</c> through set-based SQL. A platform counter, so it is not <c>IUserOwned</c>.
    /// </summary>
    public DbSet<PlatformQuotaUsage> PlatformQuotaUsage => Set<PlatformQuotaUsage>();

    /// <summary>Infrastructure-only: the outbox rows the interceptor of 09.2.4-T1 writes.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>Infrastructure-only: the idempotency rows the behaviour of 09.1.3-T2 writes.</summary>
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();

    /// <summary>The accepted legal-document versions of 09.3.1-T1 (13 FR-13-2).</summary>
    public DbSet<UserConsent> UserConsents => Set<UserConsent>();

    /// <summary>
    /// Infrastructure-only: the refresh-token families of 09.3.2-T1. Inserted through the model by the
    /// login flow's first row; every later mutation is a set-based statement in
    /// <c>EfRefreshTokenService</c>, like the outbox and idempotency tables next to it.
    /// </summary>
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    /// <summary>
    /// Infrastructure-only: the single-use action tokens of 09.3.3-T1 (BD3/WF11). Every statement that
    /// touches them is set-based raw SQL in <c>EfUserActionTokenService</c> — the invalidating insert and
    /// the replay-proof consume — like the refresh-token families next to it.
    /// </summary>
    public DbSet<UserActionToken> UserActionTokens => Set<UserActionToken>();

    /// <summary>
    /// Infrastructure-only: the MFA login challenges of 09.3.2-T3. 09.3.2-T1's login flow inserts one
    /// through <c>EfMfaChallengeService.BeginAsync</c> when the account always signs in with an
    /// authenticator; every later statement (the supersede, the locked read, the attempt counter) lives in
    /// that same store, like the refresh families next to it.
    /// </summary>
    public DbSet<AuthChallenge> AuthChallenges => Set<AuthChallenge>();

    /// <summary>
    /// Infrastructure-only: the single-use hub tickets of 09.8.1-T2. 09.8.1-T1's claim is a
    /// set-based <c>UPDATE</c> in <c>NpgsqlHubTicketValidator</c> and the issue path inserts through
    /// <c>NpgsqlHubTicketStore</c>, so nothing outside Infrastructure touches this set.
    /// </summary>
    public DbSet<HubTicket> HubTickets => Set<HubTicket>();

    /// <summary>
    /// The audit trail of 09.3.6-T1: appended by <c>EfAuditWriter</c> inside the caller's transaction,
    /// never updated. It is not on <see cref="IAppDbContext"/> — only 09.3.6-T2's whitelisted reader and
    /// 09.7.3-T1's retention sweep read it, through their own stores.
    /// </summary>
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <summary>
    /// The persisted Data Protection key ring of 09.9.4-T1 (09 README WF15): written and read by the
    /// framework's key manager through <see cref="IDataProtectionKeyContext"/>, never by a handler.
    /// </summary>
    /// <remarks>
    /// The <c>xml</c> column holds the ring's key XML already encrypted with the <c>DP_KEK</c> certificate,
    /// so this table is not a way around the KEK. No job ever prunes it: removing a row makes every value
    /// protected with that key unrecoverable (09.9.4 plan §11).
    /// </remarks>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <inheritdoc />
    public Task LockEpisodeForUpdateAsync(Guid episodeId, CancellationToken cancellationToken) =>
        CurrentUserId is { } userId
            ? Database.ExecuteSqlInterpolatedAsync(
                $"SELECT id FROM episodes WHERE id = {episodeId} AND user_id = {userId} FOR NO KEY UPDATE",
                cancellationToken)
            : Task.CompletedTask;

    /// <inheritdoc />
    public Task LockRenderJobForUpdateAsync(Guid renderJobId, CancellationToken cancellationToken) =>
        CurrentUserId is { } userId
            ? Database.ExecuteSqlInterpolatedAsync(
                $"SELECT id FROM render_jobs WHERE id = {renderJobId} AND user_id = {userId} FOR NO KEY UPDATE",
                cancellationToken)
            : Task.CompletedTask;

    /// <inheritdoc />
    public Task LockSeriesForShareAsync(Guid seriesId, CancellationToken cancellationToken) =>
        CurrentUserId is { } userId
            ? Database.ExecuteSqlInterpolatedAsync(
                $"SELECT id FROM series WHERE id = {seriesId} AND user_id = {userId} FOR SHARE",
                cancellationToken)
            : Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>
    /// The row is addressed by <c>series_id</c> — the column that is unique per series — rather than by the row's
    /// own <c>id</c>, because a caller knows the series, not the bible row: two saves of one series therefore
    /// serialise on the same row. The tenant clause is the one the episode lock uses, so another user's series, an
    /// unknown id and an anonymous scope lock nothing.
    /// </remarks>
    public Task LockSeriesBibleForUpdateAsync(Guid seriesId, CancellationToken cancellationToken) =>
        CurrentUserId is { } userId
            ? Database.ExecuteSqlInterpolatedAsync(
                $"SELECT id FROM series_bibles WHERE series_id = {seriesId} AND user_id = {userId} FOR NO KEY UPDATE",
                cancellationToken)
            : Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>
    /// The key is the user id text with advisory seed <c>3</c>: <c>EfRefreshTokenService.LockFamilyAsync</c> uses seed 0,
    /// <c>SecuritySql.LockUserAsync</c> 1 and <c>SecuritySql.LockActionTokenScopeAsync</c> 2, so none collides with it.
    /// </remarks>
    public Task LockUserSourceCapAsync(Guid userId, CancellationToken cancellationToken) =>
        Database.CurrentTransaction is null
            ? throw new InvalidOperationException(
                "The url-source cap lock runs inside the command's transaction (09 README D5); outside one it would not survive its statement.")
            : Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({userId.ToString()}, 3))",
                cancellationToken);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.ApplyNightTalesConventions();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyNightTalesConventions(this);
    }
}
