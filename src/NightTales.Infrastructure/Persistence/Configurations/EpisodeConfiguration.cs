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

        builder.HasOne<Series>().WithMany().HasForeignKey(episode => episode.SeriesId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoryVersion>().WithMany()
            .HasForeignKey(episode => episode.CurrentStoryVersionId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<StoryVersion>().WithMany()
            .HasForeignKey(episode => episode.ApprovedStoryVersionId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Asset>().WithMany().HasForeignKey(episode => episode.FinalVideoAssetId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(episode => episode.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(episode => new { episode.UserId, episode.CreatedAt, episode.Id }).IsDescending(false, true, true);
        builder.HasIndex(episode => new { episode.UserId, episode.Status, episode.CreatedAt, episode.Id })
            .IsDescending(false, false, true, true);
        builder.HasIndex(episode => new { episode.SeriesId, episode.CreatedAt, episode.Id }).IsDescending(false, true, true);
    }
}
