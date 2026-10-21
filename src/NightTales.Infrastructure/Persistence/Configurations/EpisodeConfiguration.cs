using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Assets;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>episodes</c> table (09.2.2 plan §3). The circular FK with <c>story_versions</c> is created by
/// the same migration: <c>NO ACTION</c> on the episode side lets a delete still cascade its versions in
/// one statement because the referencing episode row disappears with it.
/// </summary>
internal sealed class EpisodeConfiguration : IEntityTypeConfiguration<Episode>
{
    public void Configure(EntityTypeBuilder<Episode> builder)
    {
        builder.ToTable("episodes");
        builder.HasKey(episode => episode.Id);

        builder.Property(episode => episode.Title).HasMaxLength(120);
        builder.Property(episode => episode.Brief).HasMaxLength(1_000).IsRequired();
        builder.Property(episode => episode.LengthTier).HasEnumCheck();
        builder.Property(episode => episode.Status).HasEnumCheck();
        builder.Property(episode => episode.ApprovedAt).HasColumnName("approved_at").HasColumnType("timestamptz");
        builder.Property(episode => episode.ReadyAt).HasColumnName("ready_at").HasColumnType("timestamptz");
        builder.Property(episode => episode.VideoRejectReason).HasColumnName("video_reject_reason").HasMaxLength(500);

        // NTS-922-SEC-001 (09 W3 CR-46): the alternate key (id, user_id) every same-owner FK of this
        // table targets, so a child row can never point at another user's parent (see SeriesConfiguration).
        builder.HasAlternateKey(episode => new { episode.Id, episode.UserId });

        builder.HasOne<Series>().WithMany()
            .HasForeignKey(episode => new { episode.SeriesId, episode.UserId })
            .HasPrincipalKey(series => new { series.Id, series.UserId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoryVersion>().WithMany()
            .HasForeignKey(episode => new { episode.CurrentStoryVersionId, episode.UserId })
            .HasPrincipalKey(version => new { version.Id, version.UserId })
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<StoryVersion>().WithMany()
            .HasForeignKey(episode => new { episode.ApprovedStoryVersionId, episode.UserId })
            .HasPrincipalKey(version => new { version.Id, version.UserId })
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Asset>().WithMany()
            .HasForeignKey(episode => new { episode.FinalVideoAssetId, episode.UserId })
            .HasPrincipalKey(asset => new { asset.Id, asset.UserId })
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(episode => episode.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(episode => new { episode.UserId, episode.CreatedAt, episode.Id }).IsDescending(false, true, true);
        builder.HasIndex(episode => new { episode.UserId, episode.Status, episode.CreatedAt, episode.Id })
            .IsDescending(false, false, true, true);
        builder.HasIndex(episode => new { episode.SeriesId, episode.CreatedAt, episode.Id }).IsDescending(false, true, true);
    }
}
