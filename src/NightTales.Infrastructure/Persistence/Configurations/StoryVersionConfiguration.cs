using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>story_versions</c> table (09.2.2 plan §3) with the CR-4 columns of 02.2.2-T1: <c>parent_version_id</c>,
/// <c>generation_run_id</c>, <c>fact_sheet_id</c>, <c>schema_version</c>, and the unique <c>(episode_id, version)</c>
/// index the version counter relies on. Immutable, so it carries only <c>created_at</c>.
/// </summary>
/// <remarks>
/// Append-only story content (02.2.2 plan §3.2, FR-02-3): every property is <c>ValueGeneratedNever</c>.
/// Only <c>audit_report</c> may be refreshed during approval (02.3.2); all other properties map
/// <c>AfterSaveBehavior.Throw</c>. <c>fact_sheet_id</c> became a same-owner FK to <c>fact_sheets</c> in
/// 02.5.3-T2 (the deferred half of 02 CR-29). The three same-owner FKs are composites like the rest (CR-46);
/// their <c>SET NULL</c> nulls only the parent column, which the migration writes with raw SQL.
/// </remarks>
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
        builder.Property(version => version.SchemaVersion)
            .IsRequired()
            .HasDefaultValue(StoryVersion.CurrentSchemaVersion);

        // NTS-922-SEC-001 (09 W3 CR-46): the same-owner FK to the episode (see SeriesConfiguration).
        builder.HasAlternateKey(version => new { version.Id, version.UserId });
        builder.HasOne<Episode>().WithMany()
            .HasForeignKey(version => new { version.EpisodeId, version.UserId })
            .HasPrincipalKey(episode => new { episode.Id, episode.UserId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<StoryVersion>().WithMany()
            .HasForeignKey(version => new { version.ParentVersionId, version.UserId })
            .HasPrincipalKey(version => new { version.Id, version.UserId })
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<GenerationRun>().WithMany()
            .HasForeignKey(version => new { version.GenerationRunId, version.UserId })
            .HasPrincipalKey(run => new { run.Id, run.UserId })
            .OnDelete(DeleteBehavior.SetNull);
        // 02 CR-29, added by 02.5.3-T2: fact_sheet_id becomes a same-owner FK to fact_sheets(id, user_id).
        builder.HasOne<FactSheet>().WithMany()
            .HasForeignKey(version => new { version.FactSheetId, version.UserId })
            .HasPrincipalKey(sheet => new { sheet.Id, sheet.UserId })
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(version => version.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(version => version.EpisodeId);
        builder.HasIndex(version => new { version.EpisodeId, version.Version })
            .HasDatabaseName("ux_story_versions_episode_version")
            .IsUnique();
        builder.HasIndex(version => version.GenerationRunId).HasDatabaseName("ix_story_versions_generation_run_id");

        // Approval refreshes only the audit report; every saved story-content or identity field stays immutable.
        foreach (var property in builder.Metadata.GetProperties())
        {
            property.ValueGenerated = ValueGenerated.Never;
            property.SetAfterSaveBehavior(property.Name == nameof(StoryVersion.AuditReport)
                ? PropertySaveBehavior.Save
                : PropertySaveBehavior.Throw);
        }
    }
}
