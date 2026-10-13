using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Research;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Npgsql;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// The <c>_02_AddFactSheets</c> migration (02.5.3-T2): the <c>fact_sheets</c> table with its numeric
/// status CHECK and the partial unique index behind "at most one approved sheet per episode", the three
/// deferred 02 CR-29 fact-sheet foreign keys plus the sheet's own run key — all four composite
/// <c>(column, user_id)</c> with the column-list <c>ON DELETE SET NULL</c> that keeps <c>user_id</c> — and
/// PF-1's covering index for the newest run of an episode. This class owns the last-migration check
/// (<c>migrations[^1]</c>) and finds its own migration by name everywhere else.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class AddFactSheetsMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string MigrationName = "02_AddFactSheets";

    private static readonly string[] ExpectedColumns =
    [
        "approved_at", "approved_by", "content_sha256", "created_at", "created_by", "episode_id", "facts",
        "generation_run_id", "id", "input_truncated", "max_fact_id", "prompt_version", "source_aliases",
        "status", "updated_at", "user_id", "version",
    ];

    private static readonly string[] ExpectedChecks = ["ck_fact_sheets_sha256", "ck_fact_sheets_status"];

    private static readonly string[] ExpectedIndexes =
    [
        "ak_fact_sheets_id_user_id",
        "ix_fact_sheets_created_by",
        "ix_fact_sheets_episode_id_user_id",
        "ix_fact_sheets_generation_run_id",
        "ix_fact_sheets_generation_run_id_user_id",
        "ix_fact_sheets_user_id",
        "pk_fact_sheets",
        "ux_fact_sheets_episode_approved",
        "ux_fact_sheets_episode_version",
    ];

    /// <summary>
    /// The four same-owner keys this migration adds, in constraint-name order, as
    /// <c>name|definition</c>. The definition is the column-list <c>SET NULL</c>, which is what keeps
    /// <c>user_id</c> alive when the parent row goes away.
    /// </summary>
    private static readonly string[] ExpectedFactSheetKeys =
    [
        "fk_fact_sheets_generation_runs_generation_run_id_user_id|FOREIGN KEY (generation_run_id, user_id) "
        + "REFERENCES generation_runs(id, user_id) ON DELETE SET NULL (generation_run_id)",
        "fk_generation_runs_fact_sheets_input_fact_sheet_id_user_id|FOREIGN KEY (input_fact_sheet_id, user_id) "
        + "REFERENCES fact_sheets(id, user_id) ON DELETE SET NULL (input_fact_sheet_id)",
        "fk_generation_runs_fact_sheets_result_fact_sheet_id_user_id|FOREIGN KEY (result_fact_sheet_id, user_id) "
        + "REFERENCES fact_sheets(id, user_id) ON DELETE SET NULL (result_fact_sheet_id)",
        "fk_story_versions_fact_sheets_fact_sheet_id_user_id|FOREIGN KEY (fact_sheet_id, user_id) "
        + "REFERENCES fact_sheets(id, user_id) ON DELETE SET NULL (fact_sheet_id)",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_IsTheLastOne_AndModelHasNoPendingChange()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        migrations[^1].ShouldEndWith("_" + MigrationName);

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "an entity change without a migration must fail here (run `dotnet ef migrations add`)");
        context.Model.FindEntityType(typeof(FactSheet))!.GetTableName().ShouldBe("fact_sheets");
    }

    [Fact]
    public async Task Applies_WithTheColumnsTypesAndChecks()
    {
        SqlProbe.QueryStrings(
                Database,
                "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' "
                + "AND table_name = 'fact_sheets' ORDER BY column_name")
            .ShouldBe(ExpectedColumns);

        // The columns whose type, length or nullability the plan §3 fixes (the CR-4 user_id column is
        // NOT NULL because the same-owner keys target (id, user_id)).
        SqlProbe.QueryStrings(
                Database,
                "SELECT column_name || '|' || data_type || '|' || coalesce(character_maximum_length::text, '') "
                + "|| '|' || is_nullable FROM information_schema.columns WHERE table_schema = 'public' "
                + "AND table_name = 'fact_sheets' ORDER BY column_name")
            .ShouldBe(
            [
                "approved_at|timestamp with time zone||YES",
                "approved_by|uuid||YES",
                "content_sha256|character|64|NO",
                "created_at|timestamp with time zone||NO",
                "created_by|uuid||NO",
                "episode_id|uuid||NO",
                "facts|jsonb||NO",
                "generation_run_id|uuid||YES",
                "id|uuid||NO",
                "input_truncated|boolean||NO",
                "max_fact_id|integer||NO",
                "prompt_version|character varying|512|NO",
                "source_aliases|jsonb||NO",
                "status|smallint||NO",
                "updated_at|timestamp with time zone||NO",
                "user_id|uuid||NO",
                "version|integer||NO",
            ]);

        SqlProbe.QueryStrings(
                Database,
                "SELECT conname FROM pg_constraint WHERE conrelid = 'fact_sheets'::regclass AND contype = 'c' "
                + "ORDER BY conname")
            .ShouldBe(ExpectedChecks);
    }

    [Fact]
    public void Applies_WithTheFrozenIndexSet()
    {
        SqlProbe.QueryStrings(
                Database,
                "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'fact_sheets'")
            .ShouldBe(ExpectedIndexes, ignoreOrder: true);

        var approved = SqlProbe.QueryStrings(
                Database,
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
                + "AND indexname = 'ux_fact_sheets_episode_approved'")
            .ShouldHaveSingleItem();
        approved.ShouldContain("UNIQUE");
        approved.ShouldContain("(episode_id)");
        approved.ShouldContain("status");
        approved.ShouldContain("= 1");

        SqlProbe.QueryStrings(
                Database,
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
                + "AND indexname = 'ux_fact_sheets_episode_version'")
            .ShouldHaveSingleItem()
            .ShouldContain("(episode_id, version)");
    }

    [Fact]
    public async Task Checks_RejectAStatusOutsideTheClosedSet_AndANonHexHash()
    {
        var parents = await ParentsAsync();

        var statusFailure = Should.Throw<PostgresException>(
            () => SqlProbe.Execute(Database, InsertSheetSql(parents, Guid.CreateVersion7(), version: 1, status: 7)));
        statusFailure.SqlState.ShouldBe("23514");
        statusFailure.ConstraintName.ShouldBe("ck_fact_sheets_status");

        var hashFailure = Should.Throw<PostgresException>(
            () => SqlProbe.Execute(
                Database,
                InsertSheetSql(parents, Guid.CreateVersion7(), version: 1, status: 0, sha256: new string('A', 64))));
        hashFailure.SqlState.ShouldBe("23514");
        hashFailure.ConstraintName.ShouldBe("ck_fact_sheets_sha256");
    }

    [Fact]
    public async Task PartialUniqueIndex_AllowsOneApprovedSheetPerEpisode_AndOneRowPerVersion()
    {
        var parents = await ParentsAsync();
        var first = InsertSheet(parents, version: 1);
        InsertSheet(parents, version: 2);

        // Many drafts of one episode coexist; approving the second while the first is approved is refused.
        SqlProbe.Execute(Database, $"UPDATE fact_sheets SET status = 1 WHERE id = '{first}'");
        var second = InsertSheet(parents, version: 3);
        SqlProbe.SqlState(Database, $"UPDATE fact_sheets SET status = 1 WHERE id = '{second}'")
            .ShouldBe("23505", "at most one approved row per episode (plan §3 invariant 4, T54)");

        SqlProbe.SqlState(Database, InsertSheetSql(parents, Guid.CreateVersion7(), version: 1, status: 0))
            .ShouldBe("23505", "one row per (episode, version): a lost max+1 race raises");
    }

    [Fact]
    public void FactSheetKeys_AreCompositeWithTheColumnListSetNull()
    {
        SqlProbe.QueryStrings(
                Database,
                """
                SELECT c.conname || '|' || pg_get_constraintdef(c.oid)
                FROM pg_constraint c
                JOIN pg_class cl ON cl.oid = c.conrelid
                JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = c.conkey[1]
                WHERE c.contype = 'f'
                      AND c.connamespace = 'public'::regnamespace
                      AND cl.relname || '.' || a.attname IN (
                          'fact_sheets.generation_run_id',
                          'generation_runs.input_fact_sheet_id',
                          'generation_runs.result_fact_sheet_id',
                          'story_versions.fact_sheet_id')
                ORDER BY 1
                """)
            .ShouldBe(ExpectedFactSheetKeys);
    }

    [Fact]
    public async Task DeletingAFactSheet_ClearsOnlyTheIdColumns_AndKeepsUserId()
    {
        var parents = await ParentsAsync();
        var sheet = InsertSheet(parents);
        var run = InsertRun(parents, inputSheet: sheet, resultSheet: sheet);
        var version = InsertVersion(parents, 1, sheet);

        SqlProbe.Execute(Database, $"DELETE FROM fact_sheets WHERE id = '{sheet}'");

        // The column-list SET NULL nulls the sheet id and leaves user_id (a plain SET NULL would have
        // failed on the NOT NULL column).
        SqlProbe.ScalarText(
                Database,
                $"SELECT (input_fact_sheet_id IS NULL)::text || '|' || (result_fact_sheet_id IS NULL)::text "
                + $"|| '|' || user_id::text FROM generation_runs WHERE id = '{run}'")
            .ShouldBe($"true|true|{parents.UserId}");
        SqlProbe.ScalarText(
                Database,
                $"SELECT (fact_sheet_id IS NULL)::text || '|' || user_id::text FROM story_versions WHERE id = '{version}'")
            .ShouldBe($"true|{parents.UserId}");
    }

    [Fact]
    public async Task DeletingARun_ClearsOnlyTheSheetsRunColumn_AndKeepsTheRow()
    {
        var parents = await ParentsAsync();
        var run = InsertRun(parents);
        var sheet = InsertSheet(parents, runId: run);

        SqlProbe.Execute(Database, $"DELETE FROM generation_runs WHERE id = '{run}'");

        SqlProbe.ScalarText(
                Database,
                $"SELECT (generation_run_id IS NULL)::text || '|' || user_id::text FROM fact_sheets WHERE id = '{sheet}'")
            .ShouldBe($"true|{parents.UserId}");
    }

    [Fact]
    public async Task Pf1Index_ServesTheNewestRunOfOneEpisode()
    {
        var parents = await ParentsAsync();
        for (var age = 0; age < 10; age++)
        {
            InsertRun(parents, created: $"now() - interval '{age} minutes'");
        }

        // The lookup of the episode detail (PF-1, 02.3.4-T2): the newest run of one episode. The table is
        // small here, so the plan is forced off the sequential scan, as the retention-sweep test does.
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync(Ct);
        // ANALYZE first: without statistics the planner's choice among the episode indexes is not stable.
        await using (var off = new NpgsqlCommand("ANALYZE generation_runs; SET enable_seqscan = off; SET enable_bitmapscan = off", connection))
        {
            await off.ExecuteNonQueryAsync(Ct);
        }

        var plan = new List<string>();
        await using (var explain = new NpgsqlCommand(
            "EXPLAIN SELECT id FROM generation_runs "
            + $"WHERE episode_id = '{parents.EpisodeId}'::uuid "
            + "ORDER BY created_at DESC, id DESC LIMIT 1",
            connection))
        await using (var reader = await explain.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                plan.Add(reader.GetString(0));
            }
        }

        var text = string.Join('\n', plan);
        text.ShouldContain("ix_generation_runs_episode_created");
        text.ShouldNotContain("Sort");
    }

    [Fact]
    public async Task Down_DropsTheTableAndTheFourKeys_AndKeepsTheThreeColumns()
    {
        var parents = await ParentsAsync();
        var sheet = InsertSheet(parents);
        InsertRun(parents, inputSheet: sheet, resultSheet: sheet);
        InsertVersion(parents, 1, sheet);
        var versionsBefore = SqlProbe.Count(Database, "SELECT count(*) FROM story_versions");

        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        var own = migrations.FindIndex(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        own.ShouldBeGreaterThan(0, "the migration must exist and have a previous one to revert to");
        var migrator = context.Database.GetService<IMigrator>();

        try
        {
            await migrator.MigrateAsync(migrations[own - 1], Ct);

            SqlProbe.ScalarText(Database, "SELECT coalesce(to_regclass('public.fact_sheets')::text, 'gone')")
                .ShouldBe("gone");
            // The three columns and their indexes arrived with 02_GenerationRuns; Down removes the keys and
            // the indexes, never the columns.
            SqlProbe.Count(
                    Database,
                    "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' "
                    + "AND table_name = 'generation_runs' AND column_name IN ('input_fact_sheet_id', 'result_fact_sheet_id')")
                .ShouldBe(2);
            SqlProbe.Count(
                    Database,
                    "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' "
                    + "AND table_name = 'story_versions' AND column_name = 'fact_sheet_id'")
                .ShouldBe(1);
            SqlProbe.Count(
                    Database,
                    "SELECT count(*) FROM pg_constraint WHERE conname IN ("
                    + "'fk_fact_sheets_generation_runs_generation_run_id_user_id', "
                    + "'fk_generation_runs_fact_sheets_input_fact_sheet_id_user_id', "
                    + "'fk_generation_runs_fact_sheets_result_fact_sheet_id_user_id', "
                    + "'fk_story_versions_fact_sheets_fact_sheet_id_user_id')")
                .ShouldBe(0);
            SqlProbe.Count(
                    Database,
                    "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' "
                    + "AND indexname = 'ix_generation_runs_episode_created'")
                .ShouldBe(0);
            SqlProbe.Count(Database, "SELECT count(*) FROM story_versions")
                .ShouldBe(versionsBefore, "Down keeps every version row");
        }
        finally
        {
            // While fact_sheets is gone the three references dangle (the drop removed the keys, not the
            // values), so they are cleared before the latest migration re-adds the constraints; the class
            // database is always left at the latest migration.
            SqlProbe.Execute(Database, "UPDATE story_versions SET fact_sheet_id = NULL");
            SqlProbe.Execute(
                Database,
                "UPDATE generation_runs SET input_fact_sheet_id = NULL, result_fact_sheet_id = NULL");
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
        }

        SqlProbe.Count(Database, "SELECT count(*) FROM fact_sheets").ShouldBe(0, "the recreated table is empty");
        SqlProbe.Count(
                Database,
                "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' "
                + "AND indexname = 'ix_generation_runs_episode_created'")
            .ShouldBe(1);
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    /// <summary>One user with a series and an episode, written through the TestKit builders.</summary>
    private async Task<Parents> ParentsAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);

        return new Parents(user.Id, series.Id, episode.Id);
    }

    private Guid InsertSheet(
        Parents parents,
        int version = 1,
        int status = 0,
        Guid? runId = null,
        int maxFactId = 0)
    {
        var id = Guid.CreateVersion7();
        SqlProbe.Execute(Database, InsertSheetSql(parents, id, version, status, runId, maxFactId: maxFactId));

        return id;
    }

    private static string InsertSheetSql(
        Parents parents,
        Guid id,
        int version,
        int status,
        Guid? runId = null,
        string? sha256 = null,
        int maxFactId = 0) =>
        "INSERT INTO fact_sheets (id, user_id, episode_id, version, status, facts, source_aliases, input_truncated, "
        + "max_fact_id, content_sha256, generation_run_id, prompt_version, created_by, created_at, updated_at) VALUES ("
        + $"'{id}', '{parents.UserId}', '{parents.EpisodeId}', {version}, {status}, '[]'::jsonb, '{{}}'::jsonb, false, "
        + $"{maxFactId}, '{sha256 ?? new string('a', 64)}', "
        + (runId is null ? "NULL" : $"'{runId}'")
        + $", 'facts.v1', '{parents.UserId}', now(), now())";

    private Guid InsertRun(
        Parents parents,
        Guid? inputSheet = null,
        Guid? resultSheet = null,
        string created = "now()")
    {
        var id = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            "INSERT INTO generation_runs (id, user_id, series_id, episode_id, kind, \"trigger\", status, stage, "
            + "input_fact_sheet_id, result_fact_sheet_id, worst_case_usd, queued_at, created_at, updated_at) VALUES ("
            + $"'{id}', '{parents.UserId}', '{parents.SeriesId}', '{parents.EpisodeId}', 'story', 'user', "
            + "'succeeded', 'done', "
            + (inputSheet is null ? "NULL" : $"'{inputSheet}'")
            + ", "
            + (resultSheet is null ? "NULL" : $"'{resultSheet}'")
            + $", 0, now(), {created}, now())");

        return id;
    }

    private Guid InsertVersion(Parents parents, int version, Guid? factSheetId)
    {
        var id = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            "INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, "
            + "word_count, content, fact_sheet_id, created_at) VALUES ("
            + $"'{id}', '{parents.UserId}', '{parents.EpisodeId}', {version}, 'generated', 't', 'b', '{{}}', 1, "
            + "'{}'::jsonb, "
            + (factSheetId is null ? "NULL" : $"'{factSheetId}'")
            + ", now())");

        return id;
    }

    private sealed record Parents(Guid UserId, Guid SeriesId, Guid EpisodeId);
}
