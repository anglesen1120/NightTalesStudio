using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>autopilot_runs</c> table (09.2.2 plan §3, 02.8.3 plan §3.3): one row per slot of a series' cadence, the
/// live position of its state machine and the ids its steps attach. 02.8.3-T1 adds the slot, the state, the
/// counters and the two optional references on top of the shell 09.2.2 created.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enums.</b> <c>state</c>, <c>outcome</c> and <c>waiting_for</c> are the D8 enum pair: snake_case text plus a
/// <c>CHECK</c> emitted by <see cref="EnumCheckExtensions.HasEnumCheck{TEnum}(PropertyBuilder{TEnum})"/>, so the
/// constant, the column and the constraint cannot drift. <c>pause_reason</c> is the same shape with one documented
/// exception in its wire value (<see cref="AutopilotPauseReason.ChannelUnsupported"/>), so its conversion is
/// <see cref="AutopilotPauseReasonConverter"/> and its <c>CHECK</c> is hand-written from
/// <see cref="AutopilotPauseReasonWire.All"/> — both read the same table. <c>step</c> is plain text because the
/// ladder of <see cref="AutopilotRunStep"/> is a Domain constant list, not an enum the storage has to police.
/// </para>
/// <para>
/// <b>Indexes.</b> <c>UNIQUE (series_id, slot_at)</c> is the planner's "exactly one run per slot" arbiter (plan
/// §3.2); <c>(series_id, slot_at DESC)</c> serves the run log and the gate-failure streak (plan §3.3);
/// <c>(state, start_at)</c> is the tick's "what is due" scan; <c>(series_id, state)</c> is the concurrency guard
/// that asks whether the series already has a producing run. The shell's <c>(series_id, started_at DESC)</c> stays:
/// it orders runs by when they were planned, which is not the slot order.
/// </para>
/// <para>
/// <b>Foreign keys.</b> The series and episode keys are the same-owner composite keys of NTS-922-SEC-001
/// (09 W3 CR-46), like every other child table. <c>authorization_id</c> is a same-owner composite too, onto the
/// alternate key <c>series_autopilot_authorizations(id, user_id)</c>, and it nulls only the authorization column on
/// delete — the migration writes that column list with raw SQL because EF cannot emit it (see
/// <c>NullableSameOwnerDefinitions</c> in the migration tests). <c>publication_id</c> is the plain single-column
/// reference plan §3.3 names: a publication has no <c>(id, user_id)</c> alternate key to point at (06 README D20
/// fixes its key set), and the planner only ever attaches the publication its own run created. Both references are
/// nullable and <c>SET NULL</c>: a run keeps its history when the row it pointed at goes away.
/// </para>
/// <para>
/// <c>Version</c> is the <c>xmin</c> row version (09 README D8, N8), mapped by <c>AggregateRootConvention</c> — it
/// is the compare-and-set the orchestrator relies on, so a lost race surfaces as
/// <c>DbUpdateConcurrencyException</c> instead of a second step. The tenant filter is <c>TenantFilterConvention</c>'s
/// because the aggregate is <see cref="NightTales.Domain.Common.IUserOwned"/>, the audit timestamps are
/// <c>AuditTimestampConvention</c>'s and the id's <c>ValueGeneratedNever</c> is <c>EntityIdConvention</c>'s.
/// </para>
/// </remarks>
internal sealed class AutopilotRunConfiguration : IEntityTypeConfiguration<AutopilotRun>
{
    /// <summary>The planner's "one run per slot" arbiter (plan §3.2, §3.3).</summary>
    internal const string SlotIndexName = "ux_autopilot_runs_series_id_slot_at";

    /// <summary>The run log's and the streak reader's order: newest slot first (plan §3.3).</summary>
    internal const string SeriesSlotDescIndexName = "ix_autopilot_runs_series_id_slot_at";

    /// <summary>The tick's due scan: the runs of one state by their start instant (plan §3.3).</summary>
    internal const string StateStartIndexName = "ix_autopilot_runs_state_start_at";

    /// <summary>The concurrency guard: a series' runs of one state (plan §3.3).</summary>
    internal const string SeriesStateIndexName = "ix_autopilot_runs_series_id_state";

    /// <summary>
    /// The <c>ck_autopilot_runs_pause_reason</c> definition, from the one table of stored wire values
    /// (<see cref="AutopilotPauseReasonWire.All"/>); a <c>NULL</c> reason satisfies it because <c>NULL IN (…)</c>
    /// is never false.
    /// </summary>
    internal const string PauseReasonCheckPrefix = "\"pause_reason\" IN (";

    public void Configure(EntityTypeBuilder<AutopilotRun> builder)
    {
        builder.ToTable(
            "autopilot_runs",
            table => table.HasCheckConstraint(
                "ck_autopilot_runs_pause_reason",
                PauseReasonCheckPrefix
                + string.Join(", ", AutopilotPauseReasonWire.All.Select(value => $"'{value}'"))
                + ")"));

        builder.HasKey(run => run.Id);

        // The slot (plan §3.2) and the computed earliest safe start (plan §3.4): both NOT NULL, and their values are
        // the domain's because the planner computes them.
        builder.Property(run => run.SlotAt).IsRequired();
        builder.Property(run => run.StartAt).IsRequired();

        builder.Property(run => run.StartedAt).IsRequired();

        builder.Property(run => run.State).HasEnumCheck();
        builder.Property(run => run.Step);
        builder.Property(run => run.Outcome).HasEnumCheck();

        builder.Property(run => run.PauseReason)
            .HasConversion((ValueConverter)new AutopilotPauseReasonConverter())
            .HasColumnType("text");

        builder.Property(run => run.WaitingFor).HasEnumCheck();

        // Plan §3.3: the two counters are NOT NULL DEFAULT 0, so a row written before the counter existed reads 0
        // (the same shape `generation_runs.attempt` has).
        builder.Property(run => run.Attempt).HasDefaultValue(0);
        builder.Property(run => run.RenderAttempts).HasDefaultValue(0);

        // gate_results: the gates.v1 document 02.8.2 owns the shape of.
        builder.Property(run => run.GateResults).HasColumnType("jsonb");

        // Plan §3.3: the observability column and the stale-run sweeper's input (NOT NULL), and the terminal marker
        // (NULL except in a terminal state, plan §3.5 rule 1).
        builder.Property(run => run.LastTransitionAt).IsRequired();
        builder.Property(run => run.FinishedAt);

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

        // The authorization the run relied on (plan §3.7): the same-owner composite, so a run can never name another
        // tenant's authorization. The constraint is named explicitly because the convention's composed name would be
        // truncated by the identifier limit; the migration hand-writes `ON DELETE SET NULL (authorization_id)` for the
        // same reason it does for the episode key — a plain SET NULL would try to null the NOT NULL `user_id`.
        builder.HasOne<SeriesAutopilotAuthorization>().WithMany()
            .HasForeignKey(run => new { run.AuthorizationId, run.UserId })
            .HasPrincipalKey(authorization => new { authorization.Id, authorization.UserId })
            .HasConstraintName("fk_autopilot_runs_series_autopilot_authorizations_authorization")
            .OnDelete(DeleteBehavior.SetNull);

        // The publication this run created (plan §3.3): a plain reference, because 06's table has no (id, user_id)
        // alternate key; the planner attaches only the publication its own run created.
        builder.HasOne<Publication>().WithMany()
            .HasForeignKey(run => run.PublicationId)
            .OnDelete(DeleteBehavior.SetNull);

        // The existing descending index of the shell: planned newest first.
        builder.HasIndex(run => new { run.SeriesId, run.StartedAt }).IsDescending(false, true);

        // Plan §3.3's index set. The unique index is the planner's arbiter; the second one is the run log's and the
        // streak reader's order (`slot_at DESC`), which a unique ascending key cannot serve without a second index.
        // Both keys cover the same columns, so the name is what keeps them apart: `HasIndex(properties, name)` is the
        // only overload that defines a second index over a property set (`HasDatabaseName` alone would rename the
        // first), and `HasDatabaseName` is still required because the model name is not the store name.
        builder.HasIndex(run => new { run.SeriesId, run.SlotAt }, SlotIndexName)
            .HasDatabaseName(SlotIndexName)
            .IsUnique();
        builder.HasIndex(run => new { run.SeriesId, run.SlotAt }, SeriesSlotDescIndexName)
            .HasDatabaseName(SeriesSlotDescIndexName)
            .IsDescending(false, true);
        builder.HasIndex(run => new { run.State, run.StartAt })
            .HasDatabaseName(StateStartIndexName);
        builder.HasIndex(run => new { run.SeriesId, run.State })
            .HasDatabaseName(SeriesStateIndexName);
    }
}
