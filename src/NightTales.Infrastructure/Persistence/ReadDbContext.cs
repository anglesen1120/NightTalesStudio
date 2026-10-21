using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence;

/// <summary>
/// The no-tracking read model (09 README D8). It maps the same entity types as
/// <see cref="AppDbContext"/> — including the Identity types, so queries can join <c>users</c> — but
/// owns no migrations and every <c>SaveChanges</c> overload throws.
/// </summary>
public sealed class ReadDbContext : DbContext, IReadDb, ITenantFilterContext
{
    public ReadDbContext(DbContextOptions<ReadDbContext> options, ICurrentUser currentUser)
        : base(options)
    {
        // 09 README D10: the read model carries the same tenant filter as the write model, so the
        // scope's actor provider is told a context exists here too.
        (currentUser as ITenantScopeObserver)?.MarkObserved();
        CurrentUserId = currentUser.Id;
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    /// <summary>
    /// The owner every tenant-filtered query of this scope is restricted to (09 README D10);
    /// <c>null</c> for an anonymous scope, which therefore sees no user-owned row.
    /// </summary>
    public Guid? CurrentUserId { get; }

    public IQueryable<TEntity> Query<TEntity>()
        where TEntity : class
        => Set<TEntity>();

    public override int SaveChanges() => throw ReadOnlyViolation();

    public override int SaveChanges(bool acceptAllChangesOnSuccess) => throw ReadOnlyViolation();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => throw ReadOnlyViolation();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        => throw ReadOnlyViolation();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.ApplyNightTalesConventions();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyNightTalesConventions(this);
    }

    private static InvalidOperationException ReadOnlyViolation() => new("read-only context");
}
