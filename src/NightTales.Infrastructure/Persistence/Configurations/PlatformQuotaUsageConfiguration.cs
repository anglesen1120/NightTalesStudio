using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Publishing;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>platform_quota_usage</c> table (06.3.1-T1): one counter row per platform and Pacific day. The composite
/// primary key <c>(platform, quota_day)</c> is the only index, because every statement filters on the whole key. The
/// three CHECKs make a negative count, a non-positive limit and an unknown platform impossible at the storage layer.
/// The audit columns come from <c>IAuditable</c> through <c>AuditTimestampConvention</c>.
/// </summary>
internal sealed class PlatformQuotaUsageConfiguration : IEntityTypeConfiguration<PlatformQuotaUsage>
{
    public void Configure(EntityTypeBuilder<PlatformQuotaUsage> builder)
    {
        builder.ToTable("platform_quota_usage", table =>
        {
            table.HasCheckConstraint("ck_platform_quota_usage_platform", "platform IN ('youtube')");
            table.HasCheckConstraint("ck_platform_quota_usage_units_used", "units_used >= 0");
            table.HasCheckConstraint("ck_platform_quota_usage_units_limit", "units_limit > 0");
        });

        builder.HasKey(usage => new { usage.Platform, usage.QuotaDay });
        builder.Property(usage => usage.Platform).ValueGeneratedNever();
        builder.Property(usage => usage.QuotaDay).ValueGeneratedNever();

        // The seed insert names the counter explicitly; the default keeps a bare insert at zero units.
        builder.Property(usage => usage.UnitsUsed).HasDefaultValue(0);
        builder.Property(usage => usage.UnitsLimit).IsRequired();
    }
}
