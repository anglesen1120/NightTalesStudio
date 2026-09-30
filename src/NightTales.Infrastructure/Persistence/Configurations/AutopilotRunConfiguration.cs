using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>autopilot_runs</c> table (09.2.2 plan §3). 02.8.3-T1 adds its state-machine columns, the
/// unique <c>(series_id, slot_at)</c> index and the behaviour.
/// </summary>
internal sealed class AutopilotRunConfiguration : IEntityTypeConfiguration<AutopilotRun>
{
    public void Configure(EntityTypeBuilder<AutopilotRun> builder)
    {
        builder.ToTable("autopilot_runs");
        builder.HasKey(run => run.Id);

        builder.Property(run => run.StartedAt).IsRequired();
        builder.Property(run => run.Outcome).HasEnumCheck();
        // gate_results: the gates.v1 document 02.8.2 owns the shape of.
        builder.Property(run => run.GateResults).HasColumnType("jsonb");

        // NTS-922-SEC-001 (09 W3 CR-46): both same-owner FKs include user_id (see SeriesConfiguration).
        builder.HasOne<Series>().WithMany()
            .HasForeignKey(run => new { run.SeriesId, run.UserId })
            .HasPrincipalKey(series => new { series.Id, series.UserId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Episode>().WithMany()
            .HasForeignKey(run => new { run.EpisodeId, run.UserId })
            .HasPrincipalKey(episode => new { episode.Id, episode.UserId })
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(run => run.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(run => new { run.SeriesId, run.StartedAt }).IsDescending(false, true);
    }
}
