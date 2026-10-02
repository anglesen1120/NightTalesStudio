using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NightTales.Domain.Assets;
using NightTales.Domain.Catalog;
using NightTales.Domain.Common;
using NightTales.Domain.Communication;
using NightTales.Domain.Compliance;
using NightTales.Domain.Episodes;
using NightTales.Domain.Platform;
using NightTales.Domain.Rendering;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories;
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

    DbSet<SeriesAutopilotAuthorization> SeriesAutopilotAuthorizations { get; }

    DbSet<AutopilotRun> AutopilotRuns { get; }

    DbSet<ModerationEvent> ModerationEvents { get; }

    DbSet<UsageRecord> UsageRecords { get; }

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

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
