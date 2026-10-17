using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>source_candidates</c> table (02.7.2 plan §3, CR-4): the citation links a find-sources run proposed
/// for one episode, each waiting to be added as a <see cref="StorySource"/> or dismissed.
/// </summary>
/// <remarks>
/// <para>
/// The same-owner foreign keys of 09 W3 CR-46 are composite <c>(column, user_id)</c> onto the parent's
/// <c>(id, user_id)</c> alternate key, so the database itself refuses a candidate of user B pointing at user A's
/// episode, run or source. The episode key cascades (D19: a deleted episode takes its candidates with it); the
/// run key and the source key cascade as well, because both columns are NOT NULL or must stay consistent with
/// the plan's invariant — the table is a record of one run's proposal and of the source an added candidate
/// became, and neither can outlive its parent. <c>ON DELETE SET NULL</c> is impossible for the run (NOT NULL) and
/// would break <c>ck_source_candidates_added</c> for the source.
/// </para>
/// <para>
/// "A URL is proposed once per episode, even across runs" is the unique
/// <c>ux_source_candidates_episode_url</c> index on <c>(episode_id, normalized_url)</c>; the list query reads
/// <c>ix_source_candidates_episode_status_rank</c>. <c>status</c> is snake_case text with the D8 CHECK
/// (proposed/added/dismissed), and <c>ck_source_candidates_added</c> is the plan invariant
/// <c>status = 'added' ⇔ source_id IS NOT NULL</c>. Every property is <c>ValueGeneratedNever</c>: the domain
/// factory supplies each value, and the audit interceptor stamps the two timestamps.
/// </para>
/// </remarks>
internal sealed class SourceCandidateConfiguration : IEntityTypeConfiguration<SourceCandidate>
{
    /// <summary>The <c>ck_source_candidates_rank</c> value range: a run's citation order is 1…10 (plan §3).</summary>
    internal const string RankCheck = "\"rank\" BETWEEN 1 AND 10";

    /// <summary>The plan §3 invariant: an added candidate always names its source, and only an added one does.</summary>
    internal const string AddedCheck = "(\"status\" = 'added') = (\"source_id\" IS NOT NULL)";

    public void Configure(EntityTypeBuilder<SourceCandidate> builder)
    {
        builder.ToTable("source_candidates");
        builder.HasKey(candidate => candidate.Id);

        // D8: snake_case text (proposed/added/dismissed) plus the generated ck_source_candidates_status.
        builder.Property(candidate => candidate.Status).HasEnumCheck();
        builder.Property(candidate => candidate.Rank).HasColumnType("smallint");
        builder.Property(candidate => candidate.Url).HasMaxLength(SourceCandidate.UrlMaxChars);
        builder.Property(candidate => candidate.NormalizedUrl).HasMaxLength(SourceCandidate.UrlMaxChars);
        builder.Property(candidate => candidate.RegistrableDomain).HasMaxLength(SourceCandidate.DomainMaxChars);
        builder.Property(candidate => candidate.Title).HasMaxLength(SourceCandidate.TitleMaxChars);
        // site_name is the allow-list entry's display name; the reviewed file is the bound, as for a source note.
        builder.Property(candidate => candidate.SiteName);
        builder.Property(candidate => candidate.CreatedAt).HasColumnType("timestamptz");
        builder.Property(candidate => candidate.UpdatedAt).HasColumnType("timestamptz");

        // 09 W3 CR-46: the same-owner keys, each onto the parent's (id, user_id) alternate key.
        builder.HasOne<Episode>().WithMany()
            .HasForeignKey(candidate => new { candidate.EpisodeId, candidate.UserId })
            .HasPrincipalKey(episode => new { episode.Id, episode.UserId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<GenerationRun>().WithMany()
            .HasForeignKey(candidate => new { candidate.GenerationRunId, candidate.UserId })
            .HasPrincipalKey(run => new { run.Id, run.UserId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<StorySource>().WithMany()
            .HasForeignKey(candidate => new { candidate.SourceId, candidate.UserId })
            .HasPrincipalKey(source => new { source.Id, source.UserId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(candidate => candidate.UserId).OnDelete(DeleteBehavior.Restrict);

        // "A URL is proposed once per episode, even across runs" (plan §3): the later run skips the duplicate.
        builder.HasIndex(candidate => new { candidate.EpisodeId, candidate.NormalizedUrl })
            .HasDatabaseName("ux_source_candidates_episode_url")
            .IsUnique();

        // The list query: one episode's candidates, optionally by status, in citation order (plan §5).
        builder.HasIndex(candidate => new { candidate.EpisodeId, candidate.Status, candidate.Rank })
            .HasDatabaseName("ix_source_candidates_episode_status_rank");

        builder.ToTable(table =>
        {
            table.HasCheckConstraint("ck_source_candidates_rank", RankCheck);
            table.HasCheckConstraint("ck_source_candidates_added", AddedCheck);
        });

        // The domain factory supplies every value (UUIDv7 id, proposed status, no source).
        foreach (var property in builder.Metadata.GetProperties())
        {
            property.ValueGenerated = ValueGenerated.Never;
        }
    }
}
