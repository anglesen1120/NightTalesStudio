using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Npgsql;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// The <c>_02_AutopilotRunsStateAndSlot</c> migration (02.8.3-T1, plan §3.2–§3.5): the eleven columns the run state
/// machine adds to the existing <c>autopilot_runs</c> shell, the four indexes of plan §3.3 and the backfill of the
/// rows written before them. <c>Up</c> is expand-only — <c>ADD COLUMN</c>s, the backfill <c>UPDATE</c>, the two
/// optional references, the <c>CHECK</c>s and the indexes; <c>Down</c> drops exactly what it added.
/// </summary>
/// <remarks>
/// <para>
/// <b>The backfill Up must implement</b> (this class is the executable form of it, and the task's acceptance checks
/// read these rules):
/// </para>
/// <list type="bullet">
/// <item><description><c>slot_at = started_at</c> and <c>start_at = started_at</c>: the shell had no slot, and its
/// <c>started_at</c> is the only instant it recorded.</description></item>
/// <item><description><c>state</c> from the stored <c>outcome</c>, member for member — <c>published</c> →
/// <c>published</c>, <c>published_private_locked</c> → <c>published_private_locked</c>, <c>waiting_owner</c> →
/// <c>waiting_owner</c>, <c>gate_failed</c> → <c>gate_failed</c>, <c>cancelled_by_owner</c> →
/// <c>cancelled_by_owner</c>, <c>awaiting_authorization</c> → <c>awaiting_authorization</c>, <c>paused</c> →
/// <c>paused</c>, <c>failed</c> → <c>failed</c> — and a <c>NULL</c> outcome → <c>failed</c>: a row with no outcome
/// was never a live production (the planner and the machine did not exist before this migration), and
/// <c>failed</c> is terminal and not <c>WritesOutcome</c>/producing, so no orchestrator tick can pick it up as a run
/// it must advance. Mapping it to <c>planned</c> would make an unowned historical row start producing.</description></item>
/// <item><description>A <c>NULL</c>-outcome row also gets <c>outcome = 'failed'</c>, so the status agrees with the
/// state it is given: the aggregate writes an outcome whenever a run stops (plan §3.5 rule 3), and the gate-failure
/// streak counts only rows that stopped (<c>outcome IS NOT NULL</c>), so leaving it null would produce a terminal
/// row nobody counts.</description></item>
/// <item><description><c>attempt = 0</c> and <c>render_attempts = 0</c> (the columns' own <c>DEFAULT 0</c>), because
/// no generation or render of a pre-migration row was counted.</description></item>
/// <item><description><c>last_transition_at = updated_at</c>; <c>finished_at = updated_at</c> only for the five
/// terminal states (<c>published</c>, <c>published_private_locked</c>, <c>gate_failed</c>,
/// <c>cancelled_by_owner</c>, <c>failed</c>) and <c>NULL</c> for the blocked ones (<c>waiting_owner</c>,
/// <c>paused</c>, <c>awaiting_authorization</c>), per plan §3.5 rule 1.</description></item>
/// <item><description><c>waiting_for</c> stays <c>NULL</c>: the shell never recorded why the owner was needed, and a
/// backfilled <c>waiting_owner</c> row is history, not a live ask.</description></item>
/// <item><description><c>pause_reason</c> is <b>not</b> rewritten: it already holds the plan §3.6 wire value (or
/// <c>NULL</c>), and the new <c>CHECK</c> accepts every one of them. A row whose text is not one of the enum's wire
/// values is nulled instead, because the <c>CHECK</c> is validated against existing rows when it is added.</description></item>
/// <item><description><c>publication_id</c> and <c>authorization_id</c> stay <c>NULL</c>: the shell never recorded
/// either.</description></item>
/// </list>
/// <para>
/// The columns are added nullable, backfilled, and only then set <c>NOT NULL</c> (a <c>SET NOT NULL</c> by hand, as
/// raw SQL, so <c>MigrationGuard</c> does not read it as narrowing an inherited column). The unique
/// <c>(series_id, slot_at)</c> key is created after the backfill; two pre-migration rows of one series that shared a
/// <c>started_at</c> would make it fail loudly, which is the intended answer — the table has no production data at
/// this point (plan §3.3).
/// </para>
/// <para>
/// This class no longer owns the last-migration check (<c>migrations[^1]</c>): 06.2.3-T2's
/// <see cref="AddPlatformIncidentsMigrationTests"/> took it over for the new tip, and this class now only proves its own
/// migration is compiled in, exactly as 06.2.1-T1's <see cref="AddPublicationsMigrationTests"/> did when this migration
/// landed.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class AddAutopilotRunsStateAndSlotMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string MigrationName = "02_AutopilotRunsStateAndSlot";

    private const string Table = "autopilot_runs";

    /// <summary>The <c>autopilot_runs</c> row <see cref="SeedPrevious"/> writes into schema N-1 (the shell's shape).</summary>
    private static readonly Guid SeededRun = Guid.Parse("01920000-0000-7000-8000-00000000001f");

    /// <summary>
    /// The eleven columns this migration adds, in <c>name|type|length|nullable|default</c> form — exactly plan §3.3.
    /// <c>attempt</c> and <c>render_attempts</c> carry the plan's <c>DEFAULT 0</c>; the instants carry none, because
    /// the aggregate computes every one of them.
    /// </summary>
    private static readonly string[] ExpectedNewColumnSpecs =
    [
        "attempt|integer||NO|0",
        "authorization_id|uuid||YES|<none>",
        "finished_at|timestamp with time zone||YES|<none>",
        "last_transition_at|timestamp with time zone||NO|<none>",
        "publication_id|uuid||YES|<none>",
        "render_attempts|integer||NO|0",
        "slot_at|timestamp with time zone||NO|<none>",
        "start_at|timestamp with time zone||NO|<none>",
        "state|text||NO|<none>",
        "step|text||YES|<none>",
        "waiting_for|text||YES|<none>",
    ];

    /// <summary>
    /// The three enum <c>CHECK</c>s as <c>pg_get_constraintdef</c> renders them: the value lists come from
    /// <see cref="AutopilotRunState"/>, <see cref="WaitingFor"/> and <see cref="AutopilotPauseReason"/>, and
    /// <c>pause_reason</c> carries the plan §3.6 row 6b exception (<c>autopilot_channel_unsupported</c>).
    /// </summary>
    private static readonly string[] ExpectedCheckDefinitions =
    [
        "ck_autopilot_runs_pause_reason|CHECK ((pause_reason = ANY (ARRAY["
        + "'autopilot_channel_unsupported'::text, 'channel_not_active'::text, 'content_reported'::text, "
        + "'gate_failed_streak'::text, 'oauth_client_incident'::text, 'owner_paused'::text, "
        + "'pre_audit_private_limit'::text, 'provider_funds_exhausted'::text, 'repeated_run_failures'::text, "
        + "'run_cost_cap_reached'::text, 'slot_missed'::text, 'spend_cap_reached'::text, 'upload_failed'::text, "
        + "'veto_notice_undelivered'::text, 'video_rejected'::text, 'youtube_quota_exhausted'::text])))",
        "ck_autopilot_runs_state|CHECK ((state = ANY (ARRAY["
        + "'awaiting_authorization'::text, 'cancelled_by_owner'::text, 'failed'::text, 'gate_failed'::text, "
        + "'idea_picked'::text, 'paused'::text, 'planned'::text, 'publication_created'::text, "
        + "'published'::text, 'published_private_locked'::text, 'rendering'::text, 'researching'::text, "
        + "'scheduled'::text, 'story_gate'::text, 'story_generating'::text, 'veto_window'::text, "
        + "'video_gate'::text, 'waiting_owner'::text])))",
        "ck_autopilot_runs_waiting_for|CHECK ((waiting_for = ANY (ARRAY["
        + "'fact_sheet'::text, 'sources'::text, 'story'::text, 'video'::text])))",
    ];

    /// <summary>The four indexes plan §3.3 adds, with their exact definitions (the <c>DESC</c> marker included).</summary>
    private static readonly string[] ExpectedIndexDefinitions =
    [
        "ix_autopilot_runs_authorization_id_user_id|CREATE INDEX "
        + "ix_autopilot_runs_authorization_id_user_id ON public.autopilot_runs USING btree (authorization_id, user_id)",
        "ix_autopilot_runs_publication_id|CREATE INDEX ix_autopilot_runs_publication_id "
        + "ON public.autopilot_runs USING btree (publication_id)",
        "ix_autopilot_runs_series_id_slot_at|CREATE INDEX ix_autopilot_runs_series_id_slot_at "
        + "ON public.autopilot_runs USING btree (series_id, slot_at DESC)",
        "ix_autopilot_runs_series_id_state|CREATE INDEX ix_autopilot_runs_series_id_state "
        + "ON public.autopilot_runs USING btree (series_id, state)",
        "ix_autopilot_runs_state_start_at|CREATE INDEX ix_autopilot_runs_state_start_at "
        + "ON public.autopilot_runs USING btree (state, start_at)",
        "ux_autopilot_runs_series_id_slot_at|CREATE UNIQUE INDEX ux_autopilot_runs_series_id_slot_at "
        + "ON public.autopilot_runs USING btree (series_id, slot_at)",
    ];

    /// <summary>The shell's own descending index, which this migration must keep (plan §3.3).</summary>
    private const string KeptStartedAtIndex =
        "ix_autopilot_runs_series_id_started_at|CREATE INDEX ix_autopilot_runs_series_id_started_at "
        + "ON public.autopilot_runs USING btree (series_id, started_at DESC)";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_IsPresent_AndModelHasNoPendingChange()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();

        // 06.2.3-T2 appended a migration after this one, so this class no longer owns the last-migration check (the
        // newest migration's own test does); it still proves its own migration is compiled in.
        migrations.ShouldContain(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "an entity change without a migration must fail here (run `dotnet ef migrations add`)");

        // The runtime model is read-optimized (EF Core 10 drops the index sort order there), so the contract is read
        // from the design-time model — the one `dotnet ef migrations` diffs against.
        var entity = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(AutopilotRun))!;
        entity.GetTableName().ShouldBe(Table);

        // `xmin` is the compare-and-set the orchestrator needs (`AggregateRootConvention`), so the mapping to the
        // system column is part of the contract; the catalog side of the same fact is asserted below.
        var version = entity.FindProperty(nameof(AggregateRoot.Version))!;
        version.GetColumnName().ShouldBe("xmin");
        version.IsConcurrencyToken.ShouldBeTrue();

        // The state is the machine's own position: required, and never a free-form string.
        var state = entity.FindProperty(nameof(AutopilotRun.State))!;
        state.IsNullable.ShouldBeFalse();
        state.GetColumnName().ShouldBe("state");

        // Plan §3.3's four indexes, named exactly as the migration creates them: the unique slot key the planner
        // relies on, its descending twin for the newest-run lookup, and the two tick scans.
        var indexes = entity.GetIndexes().ToList();
        indexes.Single(index => index.GetDatabaseName() == "ux_autopilot_runs_series_id_slot_at")
            .IsUnique.ShouldBeTrue("one run per slot is the planner's arbiter");
        indexes.Single(index => index.GetDatabaseName() == "ix_autopilot_runs_series_id_slot_at")
            .IsDescending.ShouldBe([false, true], "the newest slot per series reads the index backwards");
        indexes.ShouldContain(index => index.GetDatabaseName() == "ix_autopilot_runs_state_start_at");
        indexes.ShouldContain(index => index.GetDatabaseName() == "ix_autopilot_runs_series_id_state");
    }

    [Fact]
    public void Applies_WithTheElevenNewColumnsTheirNullabilityAndDefaults()
    {
        SqlProbe.QueryStrings(
                Database,
                "SELECT column_name || '|' || data_type || '|' || coalesce(character_maximum_length::text, '') "
                + "|| '|' || is_nullable || '|' || coalesce(column_default, '<none>') "
                + "FROM information_schema.columns WHERE table_schema = 'public' "
                + $"AND table_name = '{Table}' "
                + "AND column_name IN ('slot_at', 'start_at', 'state', 'step', 'publication_id', 'authorization_id', "
                + "'attempt', 'render_attempts', 'waiting_for', 'last_transition_at', 'finished_at') "
                + "ORDER BY column_name")
            .ShouldBe(ExpectedNewColumnSpecs);
    }

    [Fact]
    public void Applies_WithTheXminRowVersionColumn()
    {
        // The orchestrator's compare-and-set reads this system column; without it a concurrent tick would overwrite
        // a run another tick just advanced instead of saving nothing.
        SqlProbe.QueryStrings(
                Database,
                "SELECT attname || '|' || atttypid::regtype::text FROM pg_attribute "
                + $"WHERE attrelid = '{Table}'::regclass AND attname = 'xmin'")
            .ShouldBe(["xmin|xid"]);
    }

    [Fact]
    public void Applies_WithTheFourIndexesAndKeepsTheStartedAtIndex()
    {
        SqlProbe.QueryStrings(
                Database,
                $"SELECT indexname || '|' || indexdef FROM pg_indexes WHERE schemaname = 'public' "
                + $"AND tablename = '{Table}' AND indexname IN ('ux_autopilot_runs_series_id_slot_at', "
                + "'ix_autopilot_runs_series_id_slot_at', 'ix_autopilot_runs_state_start_at', "
                + "'ix_autopilot_runs_series_id_state', 'ix_autopilot_runs_publication_id', "
                + "'ix_autopilot_runs_authorization_id_user_id') ORDER BY indexname")
            .ShouldBe(ExpectedIndexDefinitions);

        SqlProbe.QueryStrings(
                Database,
                $"SELECT indexname || '|' || indexdef FROM pg_indexes WHERE schemaname = 'public' "
                + $"AND tablename = '{Table}' AND indexname = 'ix_autopilot_runs_series_id_started_at'")
            .ShouldBe([KeptStartedAtIndex]);
    }

    [Fact]
    public async Task Schema_RejectsAnUnknownStateWaitingForAndPauseReason()
    {
        var parents = await ParentsAsync();

        // Each case gets its own slot: the unique (series_id, slot_at) key must not be the constraint that fires.
        var slot = parents.SlotAt;

        // `state`, `waiting_for` and `pause_reason` are closed sets, not free text: a typo in a later handler would
        // otherwise leave a row the orchestrator can never advance (or one whose alert copy key does not exist).
        var badState = Should.Throw<PostgresException>(() => SqlProbe.Execute(
            Database,
            InsertSql(
                parents,
                Guid.CreateVersion7(),
                state: "bogus",
                slotAt: slot.AddMinutes(1))));
        badState.SqlState.ShouldBe("23514");
        badState.ConstraintName.ShouldBe("ck_autopilot_runs_state");

        var badWaiting = Should.Throw<PostgresException>(() => SqlProbe.Execute(
            Database,
            InsertSql(
                parents,
                Guid.CreateVersion7(),
                state: "waiting_owner",
                waitingFor: "bogus",
                slotAt: slot.AddMinutes(2))));
        badWaiting.SqlState.ShouldBe("23514");
        badWaiting.ConstraintName.ShouldBe("ck_autopilot_runs_waiting_for");

        // The plan §3.6 row 6b exception: the stored value is `autopilot_channel_unsupported`, not the bare
        // snake_case spelling of the member's name.
        var bareMemberName = Should.Throw<PostgresException>(() => SqlProbe.Execute(
            Database,
            InsertSql(
                parents,
                Guid.CreateVersion7(),
                state: "paused",
                pauseReason: "channel_unsupported",
                slotAt: slot.AddMinutes(3))));
        bareMemberName.SqlState.ShouldBe("23514");
        bareMemberName.ConstraintName.ShouldBe("ck_autopilot_runs_pause_reason");

        // Every value the three sets allow passes: the positive half of the same contract, with the exception's
        // real wire value.
        SqlProbe.Execute(Database, InsertSql(parents, Guid.CreateVersion7(), state: "planned", slotAt: slot));
        SqlProbe.Execute(Database, InsertSql(
            parents,
            Guid.CreateVersion7(),
            state: "waiting_owner",
            waitingFor: "fact_sheet",
            slotAt: slot.AddMinutes(4)));
        SqlProbe.Execute(Database, InsertSql(
            parents,
            Guid.CreateVersion7(),
            state: "paused",
            pauseReason: "autopilot_channel_unsupported",
            slotAt: slot.AddMinutes(5)));
        SqlProbe.Execute(Database, InsertSql(
            parents,
            Guid.CreateVersion7(),
            state: "failed",
            pauseReason: "slot_missed",
            slotAt: slot.AddMinutes(6)));

        SqlProbe.Count(Database, $"SELECT count(*) FROM {Table} WHERE user_id = '{parents.UserId}'").ShouldBe(4);
    }

    [Fact]
    public async Task Schema_RejectsTwoRunsOfTheSameSeriesInTheSameSlot()
    {
        var parents = await ParentsAsync();
        var slot = parents.SlotAt;

        // The planner's arbiter (plan §3.2): one run per slot, no matter how often a tick is retried.
        SqlProbe.Execute(Database, InsertSql(parents, Guid.CreateVersion7(), slotAt: slot));

        var refused = Should.Throw<PostgresException>(() => SqlProbe.Execute(
            Database, InsertSql(parents, Guid.CreateVersion7(), slotAt: slot)));
        refused.SqlState.ShouldBe("23505");
        refused.ConstraintName.ShouldBe("ux_autopilot_runs_series_id_slot_at");

        // The key is per series and per slot: the next slot of the same series and the same slot of another series
        // both insert.
        SqlProbe.Execute(Database, InsertSql(parents, Guid.CreateVersion7(), slotAt: slot.AddDays(1)));

        var other = await ParentsAsync();
        SqlProbe.Execute(Database, InsertSql(other, Guid.CreateVersion7(), slotAt: slot));

        SqlProbe.Count(Database, $"SELECT count(*) FROM {Table} WHERE user_id = '{parents.UserId}'").ShouldBe(2);
        SqlProbe.Count(Database, $"SELECT count(*) FROM {Table} WHERE user_id = '{other.UserId}'").ShouldBe(1);
    }

    [Fact]
    public void Applies_WithTheThreeChecksAndTheirValueLists()
    {
        SqlProbe.QueryStrings(
                Database,
                "SELECT conname || '|' || pg_get_constraintdef(oid) FROM pg_constraint "
                + $"WHERE conrelid = '{Table}'::regclass AND contype = 'c' "
                + "AND conname IN ('ck_autopilot_runs_state', 'ck_autopilot_runs_waiting_for', "
                + "'ck_autopilot_runs_pause_reason') ORDER BY conname")
            .ShouldBe(ExpectedCheckDefinitions);
    }

    [Fact]
    public void Applies_WithTheTwoOptionalReferencesAndTheirDeleteActions()
    {
        // The authorization is the same-owner composite key (NTS-922-SEC-001) and nulls only its own column; the
        // publication is the plain reference plan §3.3 names, because 06's table has no (id, user_id) alternate key.
        SqlProbe.QueryStrings(
                Database,
                "SELECT conname || '|' || pg_get_constraintdef(oid) || '|' || confdeltype::text "
                + $"FROM pg_constraint WHERE conrelid = '{Table}'::regclass AND contype = 'f' "
                + "AND conname IN ('fk_autopilot_runs_series_autopilot_authorizations_authorization', "
                + "'fk_autopilot_runs_publications_publication_id') ORDER BY conname")
            .ShouldBe(
            [
                "fk_autopilot_runs_publications_publication_id|FOREIGN KEY (publication_id) "
                + "REFERENCES publications(id) ON DELETE SET NULL|n",
                "fk_autopilot_runs_series_autopilot_authorizations_authorization|FOREIGN KEY "
                + "(authorization_id, user_id) REFERENCES series_autopilot_authorizations(id, user_id) "
                + "ON DELETE SET NULL (authorization_id)|n",
            ]);
    }

    [Fact]
    public async Task Migration_HasOnlyAddAndBackfillOperations()
    {
        await using var context = Database.CreateAppDbContext();

        // The backfill is an `UPDATE`, which the guard does not read as destructive; the new columns are added
        // nullable and only then made NOT NULL by raw SQL, so nothing it inherited is narrowed.
        MigrationGuard.FindViolations([CompiledMigration(context, MigrationName)]).ShouldBeEmpty();
    }

    /// <summary>
    /// The schema one release behind with the seed harness's rows (<see cref="SeedPrevious"/>), including one
    /// <c>autopilot_runs</c> row in the old shape: every inherited column comes out untouched, and the eleven new
    /// columns are backfilled from <c>started_at</c>/<c>updated_at</c>/<c>outcome</c> exactly as this class documents.
    /// </summary>
    [Fact]
    public async Task Applies_OnThePreviousSchemaWithSeededData()
    {
        await using var context = Database.CreateAppDbContext();
        var (migrator, migrations, own) = MigrationOf(context);
        var fixture = Database.Fixture;
        var name = Database.Name;

        try
        {
            // The class database is shared by its tests, and an earlier one leaves autopilot_runs rows behind —
            // some with a NULL `outcome`, which the backfill under test deliberately rewrites to `failed`
            // (`Applies_BackfillsABlockedRunWithoutFinishedAt_AndANullOutcomeAsFailed` pins exactly that). The
            // fingerprint below claims "the migration touches none of the inherited rows", so its precondition is a
            // table holding exactly the old-shape row this dataset plants, not a sibling test's leftovers.
            await Database.ExecuteAsync("DELETE FROM autopilot_runs", Ct);

            await migrator.MigrateAsync(migrations[own - 1], Ct);

            var inherited = await SeedPrevious.CaptureTablesAsync(fixture, name, Ct);
            var seeded = await SeedPrevious.ExecuteAsync(fixture, name, Ct);
            seeded.ShouldContain(Table, "the migration must be exercised against an old-shape run, not an empty schema");

            (await NewColumnsAsync()).ShouldBeEmpty("schema N-1 has none of the eleven columns");

            var before = Snapshot(await SeedPrevious.SnapshotAsync(fixture, name, inherited, Ct));

            await migrator.MigrateAsync(migrations[own], Ct);

            // The inherited columns are untouched: the fingerprint hashes exactly the projection schema N-1 had, so
            // a rewrite of `started_at`, `outcome`, `pause_reason` or any other old column would change it.
            var after = Snapshot(await SeedPrevious.SnapshotAsync(fixture, name, inherited, Ct));
            foreach (var table in inherited.Select(shape => shape.Table))
            {
                after[table].ShouldBe(before[table], $"the migration must not touch the rows of {table}");
            }

            // The old row is backfilled: slot and start are its `started_at`, the state is its `gate_failed` outcome,
            // the counters are zero, the transition instant is its `updated_at`, it is terminal (so `finished_at` is
            // `updated_at`), nothing asked for the owner and neither reference existed.
            SqlProbe.QueryStrings(
                    Database,
                    "SELECT (slot_at = started_at)::text || '|' || (start_at = started_at)::text || '|' || state "
                    + "|| '|' || attempt::text || '|' || render_attempts::text || '|' "
                    + "|| (last_transition_at = updated_at)::text || '|' "
                    + "|| (finished_at = updated_at)::text || '|' || coalesce(waiting_for, 'null') || '|' "
                    + "|| coalesce(publication_id::text, 'null') || '|' || coalesce(authorization_id::text, 'null') "
                    + "|| '|' || coalesce(pause_reason, 'null') "
                    + $"FROM {Table} WHERE id = '{SeededRun}'")
                .ShouldBe(
                [
                    "true|true|gate_failed|0|0|true|true|null|null|null|gate_failed_streak",
                ]);
        }
        finally
        {
            // The class database is always left at the latest migration.
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
        }
    }

    /// <summary>
    /// One blocked pre-migration run (an <c>awaiting_authorization</c> row, the state whose outcome is written but
    /// which is not terminal) is backfilled without a <c>finished_at</c>, and a <c>NULL</c>-outcome row becomes
    /// <c>failed</c> with a matching outcome and <c>finished_at</c> — the one state that cannot be picked up as a live
    /// production by the tick, and a terminal row the streak reader can count.
    /// </summary>
    [Fact]
    public async Task Applies_BackfillsABlockedRunWithoutFinishedAt_AndANullOutcomeAsFailed()
    {
        await using var context = Database.CreateAppDbContext();
        var (migrator, migrations, own) = MigrationOf(context);
        var parents = await ParentsAsync();

        try
        {
            await migrator.MigrateAsync(migrations[own - 1], Ct);

            // The shell shape: no state, no slot, no counters.
            SqlProbe.Execute(
                Database,
                "INSERT INTO autopilot_runs (id, user_id, series_id, started_at, outcome, created_at, updated_at) "
                + $"VALUES ('{Guid.CreateVersion7()}', '{parents.UserId}', '{parents.SeriesId}', "
                + "now(), 'awaiting_authorization', now(), now())");
            SqlProbe.Execute(
                Database,
                "INSERT INTO autopilot_runs (id, user_id, series_id, started_at, outcome, created_at, updated_at) "
                + $"VALUES ('{Guid.CreateVersion7()}', '{parents.UserId}', '{parents.SeriesId}', "
                + "now() + interval '1 minute', NULL, now(), now())");

            await migrator.MigrateAsync(migrations[own], Ct);

            SqlProbe.QueryStrings(
                    Database,
                    $"SELECT state || '|' || coalesce(outcome, 'null') || '|' "
                    + "|| coalesce((finished_at = updated_at)::text, 'null') "
                    + $"FROM {Table} WHERE user_id = '{parents.UserId}' ORDER BY started_at")
                .ShouldBe(
                [
                    "awaiting_authorization|awaiting_authorization|null",
                    "failed|failed|true",
                ]);
        }
        finally
        {
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
        }
    }

    [Fact]
    public async Task Down_DropsTheElevenColumnsAndTheirIndexesOnly_AndReApplies()
    {
        var parents = await ParentsAsync();
        var id = Guid.CreateVersion7();
        SqlProbe.Execute(Database, InsertSql(parents, id, state: "gate_failed"));

        await using var context = Database.CreateAppDbContext();
        var (migrator, migrations, own) = MigrationOf(context);

        try
        {
            await migrator.MigrateAsync(migrations[own - 1], Ct);

            (await NewColumnsAsync()).ShouldBeEmpty("Down drops the columns this migration added");
            SqlProbe.Count(
                    Database,
                    $"SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND tablename = '{Table}' "
                    + "AND indexname IN ('ux_autopilot_runs_series_id_slot_at', 'ix_autopilot_runs_series_id_slot_at', "
                    + "'ix_autopilot_runs_state_start_at', 'ix_autopilot_runs_series_id_state')")
                .ShouldBe(0, "the drop takes the new indexes with the columns");

            // The row itself survives the Down: only its new columns go.
            SqlProbe.Count(Database, $"SELECT count(*) FROM {Table} WHERE id = '{id}'").ShouldBe(1);
            SqlProbe.Count(Database, $"SELECT count(*) FROM {Table} WHERE user_id = '{parents.UserId}'").ShouldBe(1);
        }
        finally
        {
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
        }

        // Re-applied (NFR-09-6 expand-only), the columns and indexes are back; the old row is backfilled again from
        // its `started_at`, and its `NULL` outcome lands in `failed` — never a state a tick would pick up.
        (await NewColumnsAsync()).Count.ShouldBe(11);
        SqlProbe.QueryStrings(
                Database,
                $"SELECT state || '|' || (slot_at = started_at)::text FROM {Table} WHERE id = '{id}'")
            .ShouldBe(["failed|true"]);

        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    /// <summary>The columns of the table this migration adds, if the schema under test has them.</summary>
    private async Task<IReadOnlyList<string>> NewColumnsAsync() =>
        await Database.QueryStringsAsync(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' "
            + $"AND table_name = '{Table}' AND column_name IN ('slot_at', 'start_at', 'state', 'step', "
            + "'publication_id', 'authorization_id', 'attempt', 'render_attempts', 'waiting_for', "
            + "'last_transition_at', 'finished_at') ORDER BY column_name",
            Ct);

    /// <summary>One user with a series: the parents a run row needs, written through the TestKit builders.</summary>
    private async Task<Parents> ParentsAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        actor.Id = user.Id;
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);

        return new Parents(user.Id, series.Id, new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// The one-row insert the constraint cases use: the columns the table requires now, with only the state, the
    /// owner ask, the pause reason and the slot under the caller's control, so nothing else can fail first.
    /// </summary>
    private static string InsertSql(
        Parents parents,
        Guid id,
        string state = "planned",
        string? waitingFor = null,
        string? pauseReason = null,
        DateTimeOffset? slotAt = null)
    {
        var slot = slotAt ?? parents.SlotAt;
        var waiting = waitingFor is null ? "NULL" : $"'{waitingFor}'";
        var reason = pauseReason is null ? "NULL" : $"'{pauseReason}'";

        return $"INSERT INTO {Table} (id, user_id, series_id, slot_at, start_at, started_at, state, waiting_for, "
            + "pause_reason, last_transition_at, created_at, updated_at) VALUES ("
            + $"'{id}', '{parents.UserId}', '{parents.SeriesId}', '{slot:O}', '{slot.AddDays(-2):O}', now(), "
            + $"'{state}', {waiting}, {reason}, now(), now(), now())";
    }

    private static (IMigrator Migrator, List<string> Migrations, int Own) MigrationOf(AppDbContext context)
    {
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        var own = migrations.FindIndex(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        own.ShouldBeGreaterThan(0, "the migration must exist and have a previous one to revert to");

        return (context.Database.GetService<IMigrator>(), migrations, own);
    }

    private static Migration CompiledMigration(AppDbContext context, string idFragment)
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        var type = assembly.Migrations
            .Single(pair => pair.Key.Contains(idFragment, StringComparison.Ordinal))
            .Value;

        return (Migration)Activator.CreateInstance(type, nonPublic: true)!;
    }

    /// <summary>Turns <c>table=count:md5</c> snapshot lines into a table → fingerprint lookup.</summary>
    private static Dictionary<string, string> Snapshot(IReadOnlyList<string> lines) => lines.ToDictionary(
        line => line[..line.IndexOf('=', StringComparison.Ordinal)],
        line => line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..],
        StringComparer.Ordinal);

    private sealed record Parents(Guid UserId, Guid SeriesId, DateTimeOffset SlotAt);
}
