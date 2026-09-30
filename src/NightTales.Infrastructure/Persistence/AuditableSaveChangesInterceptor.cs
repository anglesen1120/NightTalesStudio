using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Persistence;

/// <summary>
/// Stamps the audit timestamps of every <see cref="ICreatedAt"/> / <see cref="IAuditable"/> row from
/// <see cref="IClock"/> (09 README D8, 09 W3 CR-38): on insert <c>created_at</c> only while it is still
/// <c>default</c> (a factory that already received <c>now</c> keeps its value) plus <c>updated_at</c>, and
/// on update <c>updated_at</c> only. It is registered on <see cref="AppDbContext"/> *before*
/// <c>OutboxSaveChangesInterceptor</c> (09.2.4-T1), so the outbox rows are written after the stamps.
/// </summary>
/// <remarks>
/// Set-based <c>ExecuteUpdateAsync</c> bypasses interceptors, so such a path on an
/// <see cref="IAuditable"/> table sets <c>UpdatedAt</c> explicitly (D8); <c>ExecuteDeleteAsync</c> needs
/// no stamp.
/// </remarks>
public sealed class AuditableSaveChangesInterceptor(IClock clock) : SaveChangesInterceptor
{
    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Stamp(eventData.Context);

        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = clock.UtcNow;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity is ICreatedAt)
                    {
                        StampIfDefault(entry, nameof(ICreatedAt.CreatedAt), now);
                    }

                    if (entry.Entity is IAuditable)
                    {
                        StampUpdatedAt(entry, now);
                    }

                    break;

                case EntityState.Modified:
                    if (entry.Entity is IAuditable)
                    {
                        StampUpdatedAt(entry, now);
                    }

                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="now"/> only while the property still holds its CLR default, so a factory
    /// that already received <c>now</c> is never overwritten (09 W3 CR-38).
    /// </summary>
    private static void StampIfDefault(EntityEntry entry, string propertyName, DateTimeOffset now)
    {
        var property = MappedProperty(entry, propertyName);
        if (property is null)
        {
            return;
        }

        if (property.CurrentValue is not DateTimeOffset value || value == default)
        {
            property.CurrentValue = now;
        }
    }

    private static void StampUpdatedAt(EntityEntry entry, DateTimeOffset now)
    {
        var property = MappedProperty(entry, nameof(IAuditable.UpdatedAt));
        if (property is not null)
        {
            property.CurrentValue = now;
        }
    }

    private static PropertyEntry? MappedProperty(EntityEntry entry, string propertyName) =>
        entry.Metadata.FindProperty(propertyName) is null ? null : entry.Property(propertyName);
}
