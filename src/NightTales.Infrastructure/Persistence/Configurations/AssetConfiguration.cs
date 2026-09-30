using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Assets;
using NightTales.Domain.Episodes;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>assets</c> table (09.2.2 plan §3). It owns the column types only: <c>ix_assets_retention_due</c>
/// is 09.7.3-T1's index-only migration, so this table carries just <c>(episode_id)</c> (09 W3 CR-40).
/// </summary>
internal sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        builder.ToTable("assets");
        builder.HasKey(asset => asset.Id);

        builder.Property(asset => asset.Kind).HasEnumCheck();
        builder.Property(asset => asset.StorageKey).HasMaxLength(512).IsRequired();
        builder.Property(asset => asset.ContentType).IsRequired();
        builder.Property(asset => asset.SizeBytes).IsRequired();
        builder.Property(asset => asset.DurationSec).HasPrecision(9, 2);
        builder.Property(asset => asset.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();

        builder.HasOne<Episode>().WithMany().HasForeignKey(asset => asset.EpisodeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(asset => asset.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(asset => asset.StorageKey).IsUnique();
        builder.HasIndex(asset => asset.EpisodeId);
    }
}
