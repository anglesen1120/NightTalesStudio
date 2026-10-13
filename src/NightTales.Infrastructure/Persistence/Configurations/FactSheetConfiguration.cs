using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>fact_sheets</c> table (02.5.3 plan §3, 02 README D21): the versioned, deterministic fact drafts of
/// one episode, one row per version.
/// </summary>
/// <remarks>
/// <para>
/// The same-owner foreign key to the episode is the composite <c>(episode_id, user_id)</c> (09 W3 CR-46, see
/// <see cref="SeriesConfiguration"/>) and cascades, so a deleted episode takes its drafts with it (D19). The
/// alternate key <c>(id, user_id)</c> is what the three deferred fact-sheet foreign keys of 02 CR-29 target
/// (<c>generation_runs.input_fact_sheet_id</c>, <c>generation_runs.result_fact_sheet_id</c> and
/// <c>story_versions.fact_sheet_id</c>); they are declared on those two configurations, each with
/// <c>ON DELETE SET NULL</c> over the sheet column only.
/// </para>
/// <para>
/// <c>status</c> is the plan's <c>smallint</c> and not the D8 snake_case text pair: the closed set is
/// <see cref="FactSheetStatus"/> (<c>0/1/2</c>) plus <c>ck_fact_sheets_status</c>, and the partial unique
/// <c>ux_fact_sheets_episode_approved</c> — "at most one approved row per episode" (plan §3 invariant 4, T54)
/// — is <c>WHERE status = 1</c>, which is what makes the approve-and-supersede transaction safe.
/// </para>
/// <para>
/// <c>facts</c> and <c>source_aliases</c> are the canonical JSON documents of
/// <see cref="FactSheetJson"/>: their conversions are registered pre-convention
/// (<see cref="Conventions.ModelConfigurationBuilderExtensions"/>) so EF discovers both as converted scalars
/// instead of navigations, and the stored bytes are exactly the bytes <c>content_sha256</c> hashes. This
/// configuration only fixes their column type.
/// </para>
/// <para>
/// The id is <c>Guid.CreateVersion7()</c> and every other value is supplied by
/// <see cref="FactSheet.CreateDraft"/>, so every property is <c>ValueGeneratedNever</c>. There is no
/// <c>xmin</c> concurrency token: the plan fixes the ETag at <c>fs-&lt;id&gt;</c> (the row id, D9), and
/// <see cref="FactSheet"/> is an <see cref="NightTales.Domain.Common.Entity"/> like <c>StorySource</c> and
/// <c>GenerationRun</c>, so no <c>AggregateRootConvention</c> token applies.
/// </para>
/// </remarks>
internal sealed class FactSheetConfiguration : IEntityTypeConfiguration<FactSheet>
{
    /// <summary>The partial unique index predicate: exactly the approved <see cref="FactSheetStatus"/>.</summary>
    internal const string ApprovedStatusFilter = "\"status\" = 1";

    /// <summary>The <c>ck_fact_sheets_status</c> value set: draft, approved, superseded.</summary>
    internal const string StatusCheck = "\"status\" IN (0, 1, 2)";

    public void Configure(EntityTypeBuilder<FactSheet> builder)
    {
        builder.ToTable("fact_sheets");
        builder.HasKey(sheet => sheet.Id);

        builder.Property(sheet => sheet.Version).IsRequired();
        // smallint + CHECK rather than the D8 text pair: plan §3 fixes the storage of `status`.
        builder.Property(sheet => sheet.Status).HasConversion<short>().HasColumnType("smallint").IsRequired();
        builder.Property(sheet => sheet.InputTruncated).IsRequired();
        builder.Property(sheet => sheet.MaxFactId).IsRequired();
        builder.Property(sheet => sheet.ContentSha256).HasColumnType("char(64)").IsRequired();
        builder.Property(sheet => sheet.PromptVersion).HasMaxLength(512).IsRequired();
        builder.Property(sheet => sheet.CreatedAt).HasColumnType("timestamptz");
        builder.Property(sheet => sheet.UpdatedAt).HasColumnType("timestamptz");
        builder.Property(sheet => sheet.ApprovedAt).HasColumnType("timestamptz");

        // The canonical documents (see the class remarks): the conversion is pre-convention, only the
        // column type is fixed here.
        builder.Property(sheet => sheet.Facts).HasColumnType("jsonb").IsRequired();
        builder.Property(sheet => sheet.SourceAliases).HasColumnType("jsonb").IsRequired();

        // NTS-922-SEC-001 (09 W3 CR-46): the alternate key (id, user_id) the three CR-29 fact-sheet FKs
        // target, and the same-owner FK of this table (see SeriesConfiguration).
        builder.HasAlternateKey(sheet => new { sheet.Id, sheet.UserId });
        builder.HasOne<Episode>().WithMany()
            .HasForeignKey(sheet => new { sheet.EpisodeId, sheet.UserId })
            .HasPrincipalKey(episode => new { episode.Id, episode.UserId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<GenerationRun>().WithMany()
            .HasForeignKey(sheet => new { sheet.GenerationRunId, sheet.UserId })
            .HasPrincipalKey(run => new { run.Id, run.UserId })
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(sheet => sheet.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(sheet => sheet.UserId).OnDelete(DeleteBehavior.Restrict);

        // One row per (episode, version): the version counter the create path reads and the D9 ETag the
        // update path matches.
        builder.HasIndex(sheet => new { sheet.EpisodeId, sheet.Version })
            .HasDatabaseName("ux_fact_sheets_episode_version")
            .IsUnique();

        // At most one approved row per episode (plan §3 invariant 4, T54). The filter is the numeric status
        // because the column is a smallint, not the D8 text pair.
        builder.HasIndex(sheet => sheet.EpisodeId)
            .HasDatabaseName("ux_fact_sheets_episode_approved")
            .IsUnique()
            .HasFilter(ApprovedStatusFilter);

        // The provenance lookups (`fact_sheet_conflict` 412, the D21 writer read): EF names the composite
        // (generation_run_id, user_id) index itself; this single-column one is the story_versions precedent.
        builder.HasIndex(sheet => sheet.GenerationRunId).HasDatabaseName("ix_fact_sheets_generation_run_id");

        builder.ToTable(table =>
        {
            table.HasCheckConstraint("ck_fact_sheets_status", StatusCheck);
            table.HasCheckConstraint(
                "ck_fact_sheets_sha256",
                "\"content_sha256\" ~ '^[0-9a-f]{64}$'");
        });

        // The domain factory supplies every value (UUIDv7 id, the version, the hash, the run id).
        foreach (var property in builder.Metadata.GetProperties())
        {
            property.ValueGenerated = ValueGenerated.Never;
        }
    }
}
