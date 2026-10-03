using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>fetch_tasks</c> table (02 D10, 02.5.1 plan §3.2): what the fetcher leases. The schema is frozen here for
/// 02.5.2-T2's lease SQL: the <c>(status, not_before)</c> lease-scan index, the partial unique index of one live task
/// per source, and the two partial indexes on leased rows (per-domain cap, sweeper).
/// </summary>
/// <remarks>
/// Both foreign keys are composite same-owner keys and cascade: the episode's deletion (D19) and a removed source
/// delete their tasks, including a leased one (the fetcher's late result then answers 404). <c>generation_run_id</c>
/// is a plain column until 02.7 owns the <c>find_sources</c> run that fills it.
/// </remarks>
internal sealed class FetchTaskConfiguration : IEntityTypeConfiguration<FetchTask>
{
    private const string LeasedStatus = "\"status\" = 'leased'";

    public void Configure(EntityTypeBuilder<FetchTask> builder)
    {
        builder.ToTable("fetch_tasks");
        builder.HasKey(task => task.Id);

        builder.Property(task => task.Kind).HasEnumCheck();
        builder.Property(task => task.Status).HasEnumCheck();
        builder.Property(task => task.Url);
        builder.Property(task => task.RegistrableDomain).HasMaxLength(253);
        builder.Property(task => task.Attempts).HasDefaultValue(0);
        builder.Property(task => task.NotBefore).HasColumnType("timestamptz").HasDefaultValueSql("now()");
        builder.Property(task => task.LeasedAt).HasColumnType("timestamptz");
        builder.Property(task => task.LeaseUntil).HasColumnType("timestamptz");
        builder.Property(task => task.LeasedBy);
        builder.Property(task => task.ErrorCode);
        builder.Property(task => task.CreatedAt).HasColumnType("timestamptz");
        builder.Property(task => task.FinishedAt).HasColumnType("timestamptz");

        builder.HasOne<Episode>().WithMany()
            .HasForeignKey(task => new { task.EpisodeId, task.UserId })
            .HasPrincipalKey(episode => new { episode.Id, episode.UserId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<StorySource>().WithMany()
            .HasForeignKey(task => new { task.SourceId, task.UserId })
            .HasPrincipalKey(source => new { source.Id, source.UserId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(task => task.UserId).OnDelete(DeleteBehavior.Restrict);

        // The lease scan (D10).
        builder.HasIndex(task => new { task.Status, task.NotBefore }).HasDatabaseName("ix_fetch_tasks_status_not_before");

        // One live task per source; a retry inserts a new row only once the failed one left queued/leased.
        builder.HasIndex(task => task.SourceId)
            .HasDatabaseName("ux_fetch_tasks_live_source")
            .IsUnique()
            .HasFilter("\"source_id\" IS NOT NULL AND \"status\" IN ('queued', 'leased')");

        // One lease per registrable domain.
        builder.HasIndex(task => new { task.Status, task.RegistrableDomain })
            .HasDatabaseName("ix_fetch_tasks_leased_domain")
            .HasFilter(LeasedStatus);

        // The sweeper of expired leases.
        builder.HasIndex(task => new { task.Status, task.LeaseUntil })
            .HasDatabaseName("ix_fetch_tasks_leased_until")
            .HasFilter(LeasedStatus);

        builder.ToTable(table =>
        {
            table.HasCheckConstraint(
                "ck_fetch_tasks_fetch_source",
                "\"kind\" <> 'fetch_source' OR (\"source_id\" IS NOT NULL AND \"url\" IS NOT NULL "
                + "AND \"registrable_domain\" IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_fetch_tasks_leased",
                "\"status\" <> 'leased' OR (\"lease_until\" IS NOT NULL AND \"leased_by\" IS NOT NULL)");
            table.HasCheckConstraint("ck_fetch_tasks_attempts", "\"attempts\" >= 0");
        });

        foreach (var property in builder.Metadata.GetProperties())
        {
            property.ValueGenerated = ValueGenerated.Never;
        }
    }
}
