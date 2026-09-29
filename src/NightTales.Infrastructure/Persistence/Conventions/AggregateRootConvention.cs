using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Persistence.Conventions;

/// <summary>
/// Maps <see cref="AggregateRoot.Version"/> to the PostgreSQL <c>xmin</c> system column
/// (09 README D8, N8). It runs while the property is added — before the Npgsql provider's
/// model-finalizing convention, which turns a <c>uint</c> row-version property into <c>xmin</c>.
/// </summary>
public sealed class AggregateRootConvention : IPropertyAddedConvention
{
    public void ProcessPropertyAdded(
        IConventionPropertyBuilder propertyBuilder,
        IConventionContext<IConventionPropertyBuilder> context)
    {
        ArgumentNullException.ThrowIfNull(propertyBuilder);

        var property = propertyBuilder.Metadata;
        if (property.Name != nameof(AggregateRoot.Version) || property.ClrType != typeof(uint))
        {
            return;
        }

        if (!typeof(AggregateRoot).IsAssignableFrom(property.DeclaringType.ClrType))
        {
            return;
        }

        propertyBuilder.ValueGenerated(ValueGenerated.OnAddOrUpdate, fromDataAnnotation: false);
        propertyBuilder.IsConcurrencyToken(true, fromDataAnnotation: false);
    }
}
