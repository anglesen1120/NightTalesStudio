using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Persistence.Conventions;

/// <summary>
/// Maps the audit timestamps of every <see cref="ICreatedAt"/> / <see cref="IAuditable"/> entity
/// (09 README D8, 09 W3 CR-38): <c>created_at</c> for every <see cref="ICreatedAt"/> type and
/// <c>updated_at</c> only for <see cref="IAuditable"/> ones, both <c>timestamptz NOT NULL</c>. Because
/// the mapping lives here, no <c>IEntityTypeConfiguration</c> and no factory repeats it; the values come
/// from <see cref="AuditableSaveChangesInterceptor"/>.
/// </summary>
/// <remarks>
/// An entity that implements an interface without carrying the matching <c>DateTimeOffset</c> property is
/// a bug that would silently lose the column, so the convention throws instead of skipping it.
/// </remarks>
public sealed class AuditTimestampConvention : IModelFinalizingConvention
{
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            if (typeof(ICreatedAt).IsAssignableFrom(entityType.ClrType))
            {
                Map(entityType, nameof(ICreatedAt.CreatedAt));
            }

            if (typeof(IAuditable).IsAssignableFrom(entityType.ClrType))
            {
                Map(entityType, nameof(IAuditable.UpdatedAt));
            }
        }
    }

    private static void Map(IConventionEntityType entityType, string propertyName)
    {
        var property = entityType.FindProperty(propertyName);
        if (property is null || property.ClrType != typeof(DateTimeOffset))
        {
            throw new InvalidOperationException(
                $"'{entityType.ClrType.Name}' implements the audit interface but declares no " +
                $"DateTimeOffset '{propertyName}' property: the audit columns must be mapped, never lost.");
        }

        property.SetIsNullable(false, fromDataAnnotation: false);
    }
}
