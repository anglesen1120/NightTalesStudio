using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NightTales.Domain.Assets;
using NightTales.Domain.Catalog;
using NightTales.Domain.Common;
using NightTales.Domain.Communication;
using NightTales.Domain.Compliance;
using NightTales.Domain.Compliance.Deletion;
using NightTales.Domain.Episodes;
using NightTales.Domain.Platform;
using NightTales.Domain.Rendering;
using NightTales.Domain.Rendering.Music;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Usage;

namespace NightTales.Application.Abstractions.Persistence;

/// <summary>
/// The tracked write model (09 README D8). Command handlers mutate aggregates through it; only
/// <c>UnitOfWorkBehavior</c> (09 README D5) and the seeders call <see cref="SaveChangesAsync"/>.
/// Every owning feature adds its own <c>DbSet&lt;T&gt;</c> properties to this interface.
/// </summary>
public interface IAppDbContext
{
    DatabaseFacade Database { get; }

    DbSet<Series> Series { get; }

    DbSet<Episode> Episodes { get; }

    DbSet<StoryVersion> StoryVersions { get; }

    /// <summary>
    /// The paid generations of every kind (02 D2, 02.2.2-T1): inserted by the generate commands and advanced by
    /// <c>StoryGenerationProgress</c> and the sweeper. <c>IUserOwned</c>, so the D10 tenant filter applies.
    /// </summary>
    DbSet<GenerationRun> GenerationRuns { get; }

    DbSet<SeriesAutopilotAuthorization> SeriesAutopilotAuthorizations { get; }

    DbSet<AutopilotRun> AutopilotRuns { get; }

    DbSet<ModerationEvent> ModerationEvents { get; }

    /// <summary>The sources of an episode (02.5.1-T1, <c>story_sources</c>); <c>IUserOwned</c>, so the D10 tenant filter applies.</summary>
    DbSet<StorySource> StorySources { get; }

    /// <summary>The fetcher's work queue (02.5.1-T1, <c>fetch_tasks</c>); <c>IUserOwned</c>, so the D10 tenant filter applies.</summary>
    DbSet<FetchTask> FetchTasks { get; }

    /// <summary>The legal deletion duties of 13.3.1-T1 (<c>data_deletion_requests</c>); <c>IUserOwned</c>, so the D10 tenant filter applies.</summary>
    DbSet<DeletionRequest> DeletionRequests { get; }

    DbSet<UsageRecord> UsageRecords { get; }

    /// <summary>The immutable rates and limits of 02.2.4-T1; inserted only by <c>PriceVersionSeeder</c>.</summary>
    DbSet<PriceVersion> PriceVersions { get; }

    DbSet<Asset> Assets { get; }

    DbSet<Voice> Voices { get; }

    DbSet<StyleTemplate> StyleTemplates { get; }

    DbSet<FeatureFlag> FeatureFlags { get; }

    /// <summary>The queued e-mails of 09.2.6-T3: added by <c>IEmailSender</c>, mutated by <c>SendEmailJob</c>.</summary>
    DbSet<EmailMessage> EmailMessages { get; }

    /// <summary>
    /// The render-node registry of 11.3.3-T1 (table <c>worker_nodes</c>): upserted by
    /// <c>WorkerNodeSeeder</c>, heartbeat/state-mutated by <c>NodeHealthPoller</c> and read by the
    /// <c>nts-worker nodes</c> verbs. System data, so no tenant filter applies (09 README D10).
    /// </summary>
    DbSet<WorkerNode> WorkerNodes { get; }

    /// <summary>
    /// The global music catalog of 05.3.2-T3 (table <c>music_tracks</c>): upserted by <c>MusicTrackSeeder</c>.
    /// Platform data, so no tenant filter applies (09 README D10).
    /// </summary>
    DbSet<MusicTrack> MusicTracks { get; }

    /// <summary>
    /// Takes the row lock of the current user's episode (<c>SELECT … FOR UPDATE</c>, 02.2.2 plan §4 U6) as the
    /// first statement of a command's transaction, so the status read, the daily count and the run insert of two
    /// concurrent generate calls serialise on the episode. The lock is held until the surrounding transaction
    /// ends. Like the tenant filter, it matches only rows owned by the scope's user: another user's id, an
    /// unknown id and an anonymous scope lock nothing, so one tenant cannot block another; the tenant-filtered
    /// load that follows is what answers 404.
    /// </summary>
    /// <param name="episodeId">The episode to lock.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    Task LockEpisodeForUpdateAsync(Guid episodeId, CancellationToken cancellationToken);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
