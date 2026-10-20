using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 02.8.3-T1 (plan §3.2–§3.5): the eleven run-state columns on the <c>autopilot_runs</c> shell, the backfill of
    /// the rows written before them, their <c>CHECK</c>s, the four plan §3.3 indexes and the two optional references.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Expand-only order.</b> Every column is added nullable, one <c>UPDATE</c> backfills the existing rows, and
    /// only then are the six <c>NOT NULL</c> columns set <c>NOT NULL</c> — as raw SQL, so
    /// <c>MigrationGuard</c> does not read it as an <c>AlterColumn</c> narrowing an inherited column. The constraints
    /// and indexes follow the backfill: the unique <c>(series_id, slot_at)</c> key is created after the rows have a
    /// slot, and the enum <c>CHECK</c>s are validated against rows the backfill already made legal.
    /// </para>
    /// <para>
    /// <b>The backfill</b> (the executable form is <c>AddAutopilotRunsStateAndSlotMigrationTests</c>):
    /// <c>slot_at = start_at = started_at</c> (the shell's only instant), <c>state</c> member-for-member from
    /// <c>outcome</c>, <c>NULL</c> <c>outcome</c> → <c>state = outcome = 'failed'</c> (<c>failed</c> is terminal and not
    /// producing, so no tick can pick the row up as a live run it must advance), <c>attempt = render_attempts = 0</c>,
    /// <c>last_transition_at = updated_at</c>, <c>finished_at = updated_at</c> only for the five terminal states and
    /// <c>NULL</c> for the blocked ones, <c>waiting_for</c>/<c>publication_id</c>/<c>authorization_id</c> left
    /// <c>NULL</c>, and a <c>pause_reason</c> that is not one of the enum's wire values nulled — the
    /// <c>ck_autopilot_runs_pause_reason</c> added below is validated against these rows.
    /// </para>
    /// <para>
    /// <b>The authorization reference</b> is the same-owner composite of NTS-922-SEC-001 and nulls only its own
    /// column: EF emits a plain <c>SET NULL</c>, which would try to null the <c>NOT NULL</c> <c>user_id</c>, so the
    /// constraint is replaced with the PostgreSQL 15+ column-list spelling (see <c>InitialCore</c>). The model
    /// snapshot still describes the relationship as <c>SetNull</c>; only the delete action's column list lives here.
    /// Regression: <c>TenantForeignKeyTests</c>.
    /// </para>
    /// </remarks>
    public partial class _02_AutopilotRunsStateAndSlot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Added nullable on purpose: the backfill below fills them and the raw `SET NOT NULL` (after it) makes
            // the six required ones required. `attempt`/`render_attempts` keep the plan's `DEFAULT 0`, which also
            // seeds the existing rows; the instants and the state carry no default, because the aggregate computes
            // every one of them and a stale default would hide a missing value.
            migrationBuilder.AddColumn<int>(
                name: "attempt",
                table: "autopilot_runs",
                type: "integer",
                nullable: true,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "authorization_id",
                table: "autopilot_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "finished_at",
                table: "autopilot_runs",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_transition_at",
                table: "autopilot_runs",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "publication_id",
                table: "autopilot_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "render_attempts",
                table: "autopilot_runs",
                type: "integer",
                nullable: true,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "slot_at",
                table: "autopilot_runs",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "start_at",
                table: "autopilot_runs",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "state",
                table: "autopilot_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "step",
                table: "autopilot_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "waiting_for",
                table: "autopilot_runs",
                type: "text",
                nullable: true);

            // The one backfill of the shell's rows. `outcome` is the shell's own closed set (it has its CHECK), so it
            // maps member-for-member onto `state`; a NULL outcome never was a live production and becomes `failed`
            // in both columns. PostgreSQL evaluates every right-hand side against the old row, so `outcome` is still
            // the stored value in `state` and in the `finished_at` test. A NULL `pause_reason` stays NULL (`NULL IN
            // (…)` is never true) and a value outside the enum's wire set is nulled, so the CHECK added below is
            // valid against the existing rows.
            migrationBuilder.Sql(
                """
                UPDATE autopilot_runs
                SET slot_at = started_at,
                    start_at = started_at,
                    state = coalesce(outcome, 'failed'),
                    outcome = coalesce(outcome, 'failed'),
                    attempt = 0,
                    render_attempts = 0,
                    last_transition_at = updated_at,
                    finished_at = CASE
                        WHEN coalesce(outcome, 'failed') IN ('published', 'published_private_locked', 'gate_failed', 'cancelled_by_owner', 'failed')
                            THEN updated_at
                        ELSE NULL
                    END,
                    pause_reason = CASE
                        WHEN pause_reason IN ('autopilot_channel_unsupported', 'channel_not_active', 'content_reported', 'gate_failed_streak', 'oauth_client_incident', 'owner_paused', 'pre_audit_private_limit', 'provider_funds_exhausted', 'repeated_run_failures', 'run_cost_cap_reached', 'slot_missed', 'spend_cap_reached', 'upload_failed', 'veto_notice_undelivered', 'video_rejected', 'youtube_quota_exhausted')
                            THEN pause_reason
                        ELSE NULL
                    END
                WHERE slot_at IS NULL;
                """);

            // Raw on purpose (MigrationGuard reads an `AlterColumn` as narrowing an inherited column; a `SET NOT
            // NULL` this migration wrote itself is not one). `attempt` and `render_attempts` keep their `DEFAULT 0`.
            migrationBuilder.Sql(
                """
                ALTER TABLE autopilot_runs ALTER COLUMN slot_at SET NOT NULL;
                ALTER TABLE autopilot_runs ALTER COLUMN start_at SET NOT NULL;
                ALTER TABLE autopilot_runs ALTER COLUMN state SET NOT NULL;
                ALTER TABLE autopilot_runs ALTER COLUMN attempt SET NOT NULL;
                ALTER TABLE autopilot_runs ALTER COLUMN render_attempts SET NOT NULL;
                ALTER TABLE autopilot_runs ALTER COLUMN last_transition_at SET NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_autopilot_runs_authorization_id_user_id",
                table: "autopilot_runs",
                columns: new[] { "authorization_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_autopilot_runs_publication_id",
                table: "autopilot_runs",
                column: "publication_id");

            migrationBuilder.CreateIndex(
                name: "ix_autopilot_runs_series_id_slot_at",
                table: "autopilot_runs",
                columns: new[] { "series_id", "slot_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_autopilot_runs_series_id_state",
                table: "autopilot_runs",
                columns: new[] { "series_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_autopilot_runs_state_start_at",
                table: "autopilot_runs",
                columns: new[] { "state", "start_at" });

            // The planner's arbiter: created after the backfill, so two pre-migration rows of one series that shared
            // a `started_at` fail here loudly instead of silently keeping a duplicate slot (plan §3.3).
            migrationBuilder.CreateIndex(
                name: "ux_autopilot_runs_series_id_slot_at",
                table: "autopilot_runs",
                columns: new[] { "series_id", "slot_at" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_autopilot_runs_pause_reason",
                table: "autopilot_runs",
                sql: "\"pause_reason\" IN ('autopilot_channel_unsupported', 'channel_not_active', 'content_reported', 'gate_failed_streak', 'oauth_client_incident', 'owner_paused', 'pre_audit_private_limit', 'provider_funds_exhausted', 'repeated_run_failures', 'run_cost_cap_reached', 'slot_missed', 'spend_cap_reached', 'upload_failed', 'veto_notice_undelivered', 'video_rejected', 'youtube_quota_exhausted')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_autopilot_runs_state",
                table: "autopilot_runs",
                sql: "\"state\" IN ('awaiting_authorization', 'cancelled_by_owner', 'failed', 'gate_failed', 'idea_picked', 'paused', 'planned', 'publication_created', 'published', 'published_private_locked', 'rendering', 'researching', 'scheduled', 'story_gate', 'story_generating', 'veto_window', 'video_gate', 'waiting_owner')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_autopilot_runs_waiting_for",
                table: "autopilot_runs",
                sql: "\"waiting_for\" IN ('fact_sheet', 'sources', 'story', 'video')");

            migrationBuilder.AddForeignKey(
                name: "fk_autopilot_runs_publications_publication_id",
                table: "autopilot_runs",
                column: "publication_id",
                principalTable: "publications",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_autopilot_runs_series_autopilot_authorizations_authorization",
                table: "autopilot_runs",
                columns: new[] { "authorization_id", "user_id" },
                principalTable: "series_autopilot_authorizations",
                principalColumns: new[] { "id", "user_id" },
                onDelete: ReferentialAction.SetNull);

            // The same-owner reference nulls only its own column (PostgreSQL 15+ column list), which EF cannot emit:
            // a plain `SET NULL` would null the NOT NULL `user_id` and fail on the first delete of an authorization.
            migrationBuilder.Sql(
                """
                ALTER TABLE autopilot_runs
                    DROP CONSTRAINT fk_autopilot_runs_series_autopilot_authorizations_authorization;

                ALTER TABLE autopilot_runs
                    ADD CONSTRAINT fk_autopilot_runs_series_autopilot_authorizations_authorization
                        FOREIGN KEY (authorization_id, user_id) REFERENCES series_autopilot_authorizations (id, user_id)
                        ON DELETE SET NULL (authorization_id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Both references by name: the authorization one is redefined with raw SQL in `Up`, and
            // `DROP CONSTRAINT` is the same statement for either spelling.
            migrationBuilder.DropForeignKey(
                name: "fk_autopilot_runs_publications_publication_id",
                table: "autopilot_runs");

            migrationBuilder.DropForeignKey(
                name: "fk_autopilot_runs_series_autopilot_authorizations_authorization",
                table: "autopilot_runs");

            migrationBuilder.DropIndex(
                name: "ix_autopilot_runs_authorization_id_user_id",
                table: "autopilot_runs");

            migrationBuilder.DropIndex(
                name: "ix_autopilot_runs_publication_id",
                table: "autopilot_runs");

            migrationBuilder.DropIndex(
                name: "ix_autopilot_runs_series_id_slot_at",
                table: "autopilot_runs");

            migrationBuilder.DropIndex(
                name: "ix_autopilot_runs_series_id_state",
                table: "autopilot_runs");

            migrationBuilder.DropIndex(
                name: "ix_autopilot_runs_state_start_at",
                table: "autopilot_runs");

            migrationBuilder.DropIndex(
                name: "ux_autopilot_runs_series_id_slot_at",
                table: "autopilot_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_autopilot_runs_pause_reason",
                table: "autopilot_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_autopilot_runs_state",
                table: "autopilot_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_autopilot_runs_waiting_for",
                table: "autopilot_runs");

            // Dropping the columns drops the `SET NOT NULL` this migration wrote with them; nothing else of the
            // shell (its own columns, indexes and keys) is touched.
            migrationBuilder.DropColumn(
                name: "attempt",
                table: "autopilot_runs");

            migrationBuilder.DropColumn(
                name: "authorization_id",
                table: "autopilot_runs");

            migrationBuilder.DropColumn(
                name: "finished_at",
                table: "autopilot_runs");

            migrationBuilder.DropColumn(
                name: "last_transition_at",
                table: "autopilot_runs");

            migrationBuilder.DropColumn(
                name: "publication_id",
                table: "autopilot_runs");

            migrationBuilder.DropColumn(
                name: "render_attempts",
                table: "autopilot_runs");

            migrationBuilder.DropColumn(
                name: "slot_at",
                table: "autopilot_runs");

            migrationBuilder.DropColumn(
                name: "start_at",
                table: "autopilot_runs");

            migrationBuilder.DropColumn(
                name: "state",
                table: "autopilot_runs");

            migrationBuilder.DropColumn(
                name: "step",
                table: "autopilot_runs");

            migrationBuilder.DropColumn(
                name: "waiting_for",
                table: "autopilot_runs");
        }
    }
}
