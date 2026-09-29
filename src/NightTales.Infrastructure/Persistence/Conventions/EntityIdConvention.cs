using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Persistence.Conventions;

/// <summary>
/// Maps <see cref="Entity.Id"/> with <c>ValueGeneratedNever()</c> (09 README D3, D8): UUIDv7 ids
/// come from the domain factories, never from the database. It runs last, after the key-discovery
/// and value-generation conventions that give a <c>Guid</c> key <c>ValueGeneratedOnAdd</c>.
/// </summary>
public sealed class EntityIdConvention : IModelFinalizingConvention
{
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            if (!typeof(Entity).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            var id = entityType.FindProperty(nameof(Entity.Id));
            if (id is null || id.ClrType != typeof(Guid))
            {
                continue;
            }

            id.SetValueGenerated(ValueGenerated.Never, fromDataAnnotation: false);
        }
    }
}
