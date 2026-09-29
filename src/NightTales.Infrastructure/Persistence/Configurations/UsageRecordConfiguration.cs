using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>usage_records</c> table (09.2.2 plan §3, 00 overview §6:296). Append-only, so it carries only
/// <c>created_at</c>; the numbers survive an episode deletion (<c>episode_id</c> becomes null).
/// </summary>
internal sealed class UsageRecordConfiguration : IEntityTypeConfiguration<UsageRecord>
{
    public void Configure(EntityTypeBuilder<UsageRecord> builder)
    {
        builder.ToTable("usage_records");
        builder.HasKey(record => record.Id);

        builder.Property(record => record.Provider).IsRequired();
        builder.Property(record => record.Operation).HasEnumCheck();
        builder.Property(record => record.InputTokens).IsRequired().HasDefaultValue(0);
        builder.Property(record => record.CachedInputTokens).IsRequired().HasDefaultValue(0);
        builder.Property(record => record.OutputTokens).IsRequired().HasDefaultValue(0);
        builder.Property(record => record.ImageCount).HasDefaultValue(0);
        builder.Property(record => record.GpuSeconds).HasPrecision(10, 2).HasDefaultValue(0m);
        builder.Property(record => record.Estimated).IsRequired();
        builder.Property(record => record.CostUsd).HasPrecision(12, 6);

        builder.HasOne<Episode>().WithMany().HasForeignKey(record => record.EpisodeId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Series>().WithMany().HasForeignKey(record => record.SeriesId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(record => record.UserId).OnDelete(DeleteBehavior.Restrict);

        // 00 overview §6:296: the global cost window (12.5.2-T1) and the 08 KPI query.
        builder.HasIndex(record => new { record.CreatedAt, record.Operation });

        // Plan §3: the reservation (12.2.1-T1 settle/refund) and provider-run (02.2.2-T1) lookups, and the
        // per-user history window. The user_id index is composite, so it also covers the users FK and EF
        // drops its single-column by-convention index.
        builder.HasIndex(record => record.ReservationId);
        builder.HasIndex(record => record.GenerationRunId);
        builder.HasIndex(record => new { record.UserId, record.CreatedAt }).IsDescending(false, true);
    }
}
