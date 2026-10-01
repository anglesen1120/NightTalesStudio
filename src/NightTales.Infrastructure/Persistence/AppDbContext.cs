using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
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
using NightTales.Infrastructure.Persistence.Entities;
using NightTales.Infrastructure.Persistence.Idempotency;
using NightTales.Infrastructure.Persistence.Outbox;

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

    /// <summary>The accepted legal-document versions of 09.3.1-T1 (13 FR-13-2).</summary>
    public DbSet<UserConsent> UserConsents => Set<UserConsent>();

    /// <summary>
    /// Infrastructure-only: the refresh-token families of 09.3.2-T1. Inserted through the model by the
    /// login flow's first row; every later mutation is a set-based statement in
    /// <c>EfRefreshTokenService</c>, like the outbox and idempotency tables next to it.
    /// </summary>
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

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
