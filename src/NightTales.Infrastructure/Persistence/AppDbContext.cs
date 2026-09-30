using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Assets;
using NightTales.Domain.Catalog;
using NightTales.Domain.Compliance;
using NightTales.Domain.Episodes;
using NightTales.Domain.Platform;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Persistence.Conventions;
using NightTales.Infrastructure.Persistence.Idempotency;
using NightTales.Infrastructure.Persistence.Outbox;

namespace NightTales.Infrastructure.Persistence;

/// <summary>
/// The tracked write model (09 README D8). It is the only context that owns migrations and the only
/// one who saves changes; the tenant filter of 09.2.3-T1 reads <see cref="CurrentUserId"/>.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser)
    : IdentityDbContext<AppUser, AppRole, Guid>(options), IAppDbContext
{
    internal Guid? CurrentUserId { get; } = currentUser.Id;

    public DbSet<Series> Series => Set<Series>();

    public DbSet<Episode> Episodes => Set<Episode>();

    public DbSet<StoryVersion> StoryVersions => Set<StoryVersion>();

    public DbSet<SeriesAutopilotAuthorization> SeriesAutopilotAuthorizations => Set<SeriesAutopilotAuthorization>();

    public DbSet<AutopilotRun> AutopilotRuns => Set<AutopilotRun>();

    public DbSet<ModerationEvent> ModerationEvents => Set<ModerationEvent>();

    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();

    public DbSet<Asset> Assets => Set<Asset>();

    public DbSet<Voice> Voices => Set<Voice>();

    public DbSet<StyleTemplate> StyleTemplates => Set<StyleTemplate>();

    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();

    /// <summary>Infrastructure-only: the outbox rows the interceptor of 09.2.4-T1 writes.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>Infrastructure-only: the idempotency rows the behaviour of 09.1.3-T2 writes.</summary>
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.ApplyNightTalesConventions();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyNightTalesConventions();
    }
}
