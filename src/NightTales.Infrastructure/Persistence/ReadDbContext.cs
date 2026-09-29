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
public sealed class ReadDbContext : DbContext, IReadDb
{
    public ReadDbContext(DbContextOptions<ReadDbContext> options, ICurrentUser currentUser)
        : base(options)
    {
        CurrentUserId = currentUser.Id;
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    internal Guid? CurrentUserId { get; }

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
        modelBuilder.ApplyNightTalesConventions();
    }

    private static InvalidOperationException ReadOnlyViolation() => new("read-only context");
}
