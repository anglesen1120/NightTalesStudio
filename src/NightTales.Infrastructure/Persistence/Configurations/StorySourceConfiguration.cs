using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>story_sources</c> table (00 §6, 02.5.1 plan §3.2, CR-4): a pasted URL or a user note of one episode.
/// </summary>
/// <remarks>
/// The foreign key to the episode is the composite same-owner key <c>(episode_id, user_id)</c> (09 W3 CR-46, see
/// <see cref="SeriesConfiguration"/>) and cascades, so a deleted episode takes its sources with it (D19), and the
/// alternate key <c>(id, user_id)</c> is what <c>fetch_tasks</c> targets. The partial unique index carries the
/// duplicate rule (<c>409 source_duplicate</c>); the rolling 24 h cap counts <c>source_creations</c>, not these rows. Every property is
/// <c>ValueGeneratedNever</c>: the domain supplies each value.
/// </remarks>
internal sealed class StorySourceConfiguration : IEntityTypeConfiguration<StorySource>
{
    public void Configure(EntityTypeBuilder<StorySource> builder)
    {
        builder.ToTable("story_sources");
        builder.HasKey(source => source.Id);

        builder.Property(source => source.Kind).HasEnumCheck();
        builder.Property(source => source.Status).HasEnumCheck();
        builder.Property(source => source.Url).HasMaxLength(2048);
        builder.Property(source => source.NormalizedUrl).HasMaxLength(2048);
        builder.Property(source => source.RegistrableDomain).HasMaxLength(253);
        builder.Property(source => source.NoteText);
        builder.Property(source => source.ErrorCode);
        builder.Property(source => source.HttpStatus);
        builder.Property(source => source.ContentType);
        builder.Property(source => source.ContentSha256).HasColumnType("char(64)");
        builder.Property(source => source.ExtractedTextKey);
        builder.Property(source => source.TextChars);
        builder.Property(source => source.FinalUrl);
        builder.Property(source => source.Title);
        builder.Property(source => source.CreatedAt).HasColumnType("timestamptz");
        builder.Property(source => source.FetchedAt).HasColumnType("timestamptz");

        builder.HasAlternateKey(source => new { source.Id, source.UserId });
        builder.HasOne<Episode>().WithMany()
            .HasForeignKey(source => new { source.EpisodeId, source.UserId })
            .HasPrincipalKey(episode => new { episode.Id, episode.UserId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(source => source.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(source => new { source.EpisodeId, source.NormalizedUrl })
            .HasDatabaseName("ux_story_sources_episode_url")
            .IsUnique()
            .HasFilter("\"kind\" = 'url'");

        builder.HasIndex(source => new { source.EpisodeId, source.Status }).HasDatabaseName("ix_story_sources_episode_status");
        builder.HasIndex(source => new { source.EpisodeId, source.CreatedAt }).HasDatabaseName("ix_story_sources_episode_created");

        builder.ToTable(table =>
        {
            table.HasCheckConstraint(
                "ck_story_sources_url",
                "\"kind\" <> 'url' OR (\"url\" IS NOT NULL AND \"normalized_url\" IS NOT NULL "
                + "AND \"registrable_domain\" IS NOT NULL AND \"note_text\" IS NULL)");
            table.HasCheckConstraint(
                "ck_story_sources_note",
                "\"kind\" <> 'user_note' OR (\"url\" IS NULL AND \"note_text\" IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_story_sources_note_len",
                "\"note_text\" IS NULL OR char_length(\"note_text\") <= 3000");
            table.HasCheckConstraint(
                "ck_story_sources_url_len",
                "\"normalized_url\" IS NULL OR char_length(\"normalized_url\") <= 2048");
            table.HasCheckConstraint(
                "ck_story_sources_sha256",
                "\"content_sha256\" IS NULL OR \"content_sha256\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint(
                "ck_story_sources_text_chars",
                "\"text_chars\" IS NULL OR (\"text_chars\" >= 0 AND \"text_chars\" <= 300000)");
        });

        foreach (var property in builder.Metadata.GetProperties())
        {
            property.ValueGenerated = ValueGenerated.Never;
        }
    }
}
