using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>generation_runs</c> table (02 D2, 02.2.2 plan §3.1): one row per paid generation of any kind.
/// </summary>
/// <remarks>
/// <para>
/// The closed value sets are the D8 enum pair (snake_case text plus <c>ck_generation_runs_kind</c>,
/// <c>_trigger</c>, <c>_status</c>, <c>_stage</c>); <c>previous_episode_status</c> and <c>pause_reason</c> carry
/// the plan's own constraint names (<c>_prev</c>, <c>_pause</c>) and value sets, so they use the converter
/// directly. <c>ck_generation_runs_scope</c> is TL T17: a run has no episode exactly when its kind is
/// series-level.
/// </para>
/// <para>
/// Every same-owner foreign key is composite <c>(parent_id, user_id)</c> (09 W3 CR-46, see
/// <see cref="SeriesConfiguration"/>), so a run can never point at another user's episode or version. The
/// nullable <c>SET NULL</c> ones (<c>result_story_version_id</c>, and 02.5.3-T2's <c>input_fact_sheet_id</c> and
/// <c>result_fact_sheet_id</c>) null only their parent column — the migration spells that with raw SQL, as
/// <c>InitialCore</c> does. The two fact-sheet constraints are the deferred half of 02 CR-29: the columns have
/// existed since <c>02_GenerationRuns</c>, and 02.5.3-T2 is what gives them a parent.
/// </para>
/// <para>
/// PF-1 (02.3.4-T2 hand-over) is <c>ix_generation_runs_episode_created</c>: the episode-detail query reads the
/// latest run of an episode ordered <c>created_at DESC, id DESC</c>, so the index carries that exact order.
/// </para>
/// <para>
/// The partial unique indexes are the second backstop of "one active run per (episode, kind)" behind the episode
/// row lock. The sweeper index orders by <c>COALESCE(started_at, queued_at)</c>, an expression EF cannot model, so
/// the migration creates it with SQL. Every property is <c>ValueGeneratedNever</c>: the domain always supplies the
/// value, which also keeps a <c>false</c> for <c>counts_toward_limit</c> from being read as "unset" next to its
/// <c>DEFAULT true</c>.
/// </para>
/// </remarks>
internal sealed class GenerationRunConfiguration : IEntityTypeConfiguration<GenerationRun>
{
    private const string ActiveStatuses = "\"status\" IN ('queued', 'running')";

    public void Configure(EntityTypeBuilder<GenerationRun> builder)
    {
        builder.ToTable("generation_runs");
        builder.HasKey(run => run.Id);

        builder.Property(run => run.Kind).HasEnumCheck();
        builder.Property(run => run.Trigger).HasEnumCheck();
        builder.Property(run => run.Status).HasEnumCheck();
        builder.Property(run => run.Stage).HasEnumCheck();
        builder.Property(run => run.PreviousEpisodeStatus)
            .HasConversion((ValueConverter)new EnumToSnakeCaseStringConverter<EpisodeStatus>())
            .HasColumnType("text");
        builder.Property(run => run.PauseReason)
            .HasConversion((ValueConverter)new EnumToSnakeCaseStringConverter<GenerationPauseReason>())
            .HasColumnType("text");

        builder.Property(run => run.IdempotencyKey);
        builder.Property(run => run.CountsTowardLimit).HasDefaultValue(true);
        builder.Property(run => run.WorstCaseUsd).HasPrecision(12, 6);
        builder.Property(run => run.CostUsd).HasPrecision(12, 6);
        builder.Property(run => run.ErrorCode);
        builder.Property(run => run.ErrorDetail);
        builder.Property(run => run.PromptVersion);
        builder.Property(run => run.Models).HasColumnType("jsonb");
        builder.Property(run => run.Attempt).HasDefaultValue(0);
        builder.Property(run => run.QueuedAt).HasColumnType("timestamptz");
        builder.Property(run => run.StartedAt).HasColumnType("timestamptz");
        builder.Property(run => run.FinishedAt).HasColumnType("timestamptz");

        // NTS-922-SEC-001 (09 W3 CR-46): the alternate key (id, user_id) the same-owner FKs of
        // story_versions target, and the same-owner FKs of this table (see SeriesConfiguration).
        builder.HasAlternateKey(run => new { run.Id, run.UserId });
        builder.HasOne<Series>().WithMany()
            .HasForeignKey(run => new { run.SeriesId, run.UserId })
            .HasPrincipalKey(series => new { series.Id, series.UserId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Episode>().WithMany()
            .HasForeignKey(run => new { run.EpisodeId, run.UserId })
            .HasPrincipalKey(episode => new { episode.Id, episode.UserId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<StoryVersion>().WithMany()
            .HasForeignKey(run => new { run.ResultStoryVersionId, run.UserId })
            .HasPrincipalKey(version => new { version.Id, version.UserId })
            .OnDelete(DeleteBehavior.SetNull);
        // 02 CR-29, added by 02.5.3-T2: the fact-sheet columns become same-owner FKs to fact_sheets(id, user_id).
        builder.HasOne<FactSheet>().WithMany()
            .HasForeignKey(run => new { run.InputFactSheetId, run.UserId })
            .HasPrincipalKey(sheet => new { sheet.Id, sheet.UserId })
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<FactSheet>().WithMany()
            .HasForeignKey(run => new { run.ResultFactSheetId, run.UserId })
            .HasPrincipalKey(sheet => new { sheet.Id, sheet.UserId })
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(run => run.UserId).OnDelete(DeleteBehavior.Restrict);

        // At most one active run per episode and kind (409 story_generation_in_progress, …) ...
        builder.HasIndex(run => new { run.EpisodeId, run.Kind })
            .HasDatabaseName("ux_generation_runs_active_episode")
            .IsUnique()
            .HasFilter("\"episode_id\" IS NOT NULL AND " + ActiveStatuses);

        // ... and one active series-level run per series and kind.
        builder.HasIndex(run => new { run.SeriesId, run.Kind })
            .HasDatabaseName("ux_generation_runs_active_series")
            .IsUnique()
            .HasFilter("\"episode_id\" IS NULL AND " + ActiveStatuses);

        // D2 and the rolling daily-limit query (02.4.2-T1).
        builder.HasIndex(run => new { run.EpisodeId, run.Kind, run.QueuedAt })
            .HasDatabaseName("ix_generation_runs_episode_kind_queued")
            .IsDescending(false, false, true);

        // PF-1 (02.3.4-T2 hand-over): "the latest run of this episode", read as
        // ORDER BY created_at DESC, id DESC LIMIT 1 (episode detail, run status).
        builder.HasIndex(run => new { run.EpisodeId, run.CreatedAt, run.Id })
            .HasDatabaseName("ix_generation_runs_episode_created")
            .IsDescending(false, true, true);

        // IBudgetGuard's open bounds: only the queued and running rows, covering worst_case_usd.
        builder.HasIndex(run => run.Status)
            .HasDatabaseName("ix_generation_runs_open")
            .IncludeProperties(run => run.WorstCaseUsd)
            .HasFilter(ActiveStatuses);

        // ix_generation_runs_user_id is EF's own name for this index; ix_generation_runs_sweep is created by
        // the migration (expression index).
        builder.HasIndex(run => run.UserId);

        builder.ToTable(table =>
        {
            table.HasCheckConstraint(
                "ck_generation_runs_prev",
                "\"previous_episode_status\" IS NULL OR \"previous_episode_status\" IN ('draft', 'story_review')");

            // T17: series-level kinds are ideas/bible_proposal; story/fact_sheet/find_sources are episode-scoped.
            table.HasCheckConstraint(
                "ck_generation_runs_scope",
                "(\"episode_id\" IS NULL) = (\"kind\" IN ('ideas', 'bible_proposal'))");

            // T19: the pause reason is its own column, never overloaded into error_detail.
            table.HasCheckConstraint(
                "ck_generation_runs_pause",
                "\"pause_reason\" IS NULL OR \"pause_reason\" IN ('spend_cap_reached', 'provider_funds_exhausted')");

            table.HasCheckConstraint(
                "ck_generation_runs_money",
                "\"worst_case_usd\" >= 0 AND (\"cost_usd\" IS NULL OR \"cost_usd\" >= 0)");
            table.HasCheckConstraint(
                "ck_generation_runs_times",
                "\"finished_at\" IS NULL OR \"started_at\" IS NULL OR \"finished_at\" >= \"started_at\"");
            table.HasCheckConstraint(
                "ck_generation_runs_failed",
                "\"status\" <> 'failed' OR \"error_code\" IS NOT NULL");
        });

        // The domain always supplies every value (UUIDv7 id, the counting flag, the attempt counter).
        foreach (var property in builder.Metadata.GetProperties())
        {
            property.ValueGenerated = ValueGenerated.Never;
        }
    }
}
