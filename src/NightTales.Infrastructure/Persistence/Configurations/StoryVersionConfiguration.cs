using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>story_versions</c> table (09.2.2 plan §3). Immutable, so it carries only <c>created_at</c>;
/// 02.2.2-T1 adds its own columns, its unique <c>(episode_id, version)</c> index and the behaviour.
/// </summary>
internal sealed class StoryVersionConfiguration : IEntityTypeConfiguration<StoryVersion>
{
    public void Configure(EntityTypeBuilder<StoryVersion> builder)
    {
        builder.ToTable("story_versions");
        builder.HasKey(version => version.Id);

        builder.Property(version => version.Version).IsRequired();
        builder.Property(version => version.Source).HasEnumCheck();
        builder.Property(version => version.Title).IsRequired();
        builder.Property(version => version.Body).IsRequired();
        // The list is read-only on the entity, so EF reads and writes the private _keywords field.
        builder.Property(version => version.Keywords)
            .HasColumnType("text[]")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Property(version => version.WordCount).IsRequired();
        // content: the story.v1 document 02.2.1-T1 owns the shape of; audit_report: audit.v1 (02 D22).
        builder.Property(version => version.Content).HasColumnType("jsonb").IsRequired();
        builder.Property(version => version.AuditReport).HasColumnType("jsonb");
        builder.Property(version => version.SimilarityScore).HasPrecision(5, 4);

        // NTS-922-SEC-001 (09 W3 CR-46): the same-owner FK to the episode (see SeriesConfiguration).
        builder.HasAlternateKey(version => new { version.Id, version.UserId });
        builder.HasOne<Episode>().WithMany()
            .HasForeignKey(version => new { version.EpisodeId, version.UserId })
            .HasPrincipalKey(episode => new { episode.Id, episode.UserId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(version => version.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(version => version.EpisodeId);
    }
}
