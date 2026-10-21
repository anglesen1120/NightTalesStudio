using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Platform;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>feature_flags</c> table (09.2.2 plan §3): keyed by the flag key, with the <c>rules</c> document
/// defaulting to <c>{}</c>. Platform data, so it is not <c>IUserOwned</c>.
/// </summary>
internal sealed class FeatureFlagConfiguration : IEntityTypeConfiguration<FeatureFlag>
{
    public void Configure(EntityTypeBuilder<FeatureFlag> builder)
    {
        builder.ToTable("feature_flags");
        builder.HasKey(flag => flag.Key);

        builder.Property(flag => flag.Key).IsRequired();
        builder.Property(flag => flag.Enabled).IsRequired();
        builder.Property(flag => flag.Rules).HasColumnType("jsonb").HasDefaultValue(FeatureFlag.EmptyRules);
    }
}
