using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace NightTales.Infrastructure.Persistence.Conventions;

/// <summary>
/// Turns every property annotated by <see cref="EnumCheckExtensions.HasEnumCheck{TEnum}"/> into a
/// <c>ck_&lt;table&gt;_&lt;column&gt;</c> constraint listing the snake_case enum values
/// (09 README D8, C8). Runs at model finalization, when table and column names are final.
/// </summary>
public sealed class EnumCheckConvention : IModelFinalizingConvention
{
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            var table = entityType.GetTableName();
            if (table is null)
            {
                continue;
            }

            var storeObject = StoreObjectIdentifier.Table(table, entityType.GetSchema());

            foreach (var property in entityType.GetDeclaredProperties())
            {
                if (property.FindAnnotation(EnumCheckAnnotationNames.EnumCheck)?.Value is not Type enumType)
                {
                    continue;
                }

                var column = property.GetColumnName(storeObject);
                if (column is null)
                {
                    continue;
                }

                var allowed = string.Join(", ", EnumSnakeCase.ConvertAll(enumType).Select(value => $"'{value}'"));
                entityType.AddCheckConstraint(
                    $"ck_{table}_{column}",
                    $"{column} IN ({allowed})",
                    fromDataAnnotation: false);
            }
        }
    }
}
