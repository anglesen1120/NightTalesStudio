using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.UnitTests.TestKit.Probes;

/// <summary>
/// The only <see cref="IEntityTypeConfiguration{TEntity}"/> of the probe model. It is applied by
/// <see cref="ProbeModelCustomizer"/>, never by <c>ApplyConfigurationsFromAssembly</c> in
/// <c>src</c>.
/// </summary>
internal sealed class ProbeAggregateConfiguration : IEntityTypeConfiguration<ProbeAggregate>
{
    public void Configure(EntityTypeBuilder<ProbeAggregate> builder)
    {
        builder.ToTable("probe_aggregates");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Status).HasEnumCheck();
        builder.ComplexProperty(x => x.Settings, property => property.ToJson());
    }
}
