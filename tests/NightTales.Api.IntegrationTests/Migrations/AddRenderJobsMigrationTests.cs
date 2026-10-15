using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Rendering;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Npgsql;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// The <c>_09_RenderJobs</c> migration (09.6.1-T1, BD5): the <c>render_jobs</c> table with its index set — the
/// poller's due-job scan, the tenant lookups and the two same-owner key indexes — the partial unique index behind
/// "one active render per episode", the closed status CHECK, and the composite keys to <c>episodes</c> and
/// <c>story_versions</c> beside the owner key. The <c>_06_PlatformQuotaUsage</c> migration owns the
/// last-migration check now; this class finds its own migration by name, like every other migration test.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class AddRenderJobsMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string MigrationName = "09_RenderJobs";

    private static readonly string[] ExpectedColumns =
    [
        "adapter_task_id", "attempt", "cancel_requested_at", "episode_id", "error_code", "error_message",
        "finished_at", "id", "images_needed", "next_poll_at", "params", "percent", "quality_report",
        "reservation_id", "stage", "started_at", "status", "story_version_id", "submitted_at", "user_id",
        "worker_node_id",
    ];

    /// <summary>
    /// The columns whose type, length or nullability 00 §6 and 09 W2 CR-1 fix, in
    /// <c>name|type|length|nullable</c> form. There is no <c>created_at</c>: the newest job of an episode is the
    /// highest UUIDv7 <c>id</c> (WT49), and the row version is the <c>xmin</c> system column.
    /// </summary>
    private static readonly string[] ExpectedColumnSpecs =
    [
        "adapter_task_id|text||YES",
        "attempt|integer||NO",
        "cancel_requested_at|timestamp with time zone||YES",
        "episode_id|uuid||NO",
        "error_code|character varying|64|YES",
        "error_message|character varying|2000|YES",
        "finished_at|timestamp with time zone||YES",
        "id|uuid||NO",
        "images_needed|integer||YES",
        "next_poll_at|timestamp with time zone||YES",
        "params|jsonb||NO",
        "percent|smallint||NO",
        "quality_report|jsonb||YES",
        "reservation_id|uuid||NO",
        "stage|character varying|32|NO",
        "started_at|timestamp with time zone||YES",
        "status|text||NO",
        "story_version_id|uuid||NO",
        "submitted_at|timestamp with time zone||YES",
        "user_id|uuid||NO",
        "worker_node_id|uuid||YES",
    ];

    private static readonly string[] ExpectedIndexes =
    [
        "ix_render_jobs_episode_id_user_id",
        "ix_render_jobs_status_next_poll_at",
        "ix_render_jobs_story_version_id_user_id",
        "ix_render_jobs_user_id_episode_id",
        "pk_render_jobs",
        "ux_render_jobs_active_episode",
    ];

    /// <summary>
    /// The three keys of the table as PostgreSQL renders them: the same-owner composite keys
    /// (NTS-922-SEC-001) — to <c>episodes(id, user_id)</c>, so the episode's own hard delete takes the job with it,
    /// and to <c>story_versions(id, user_id)</c> — and the owner, whose delete is restricted.
    /// </summary>
    private static readonly string[] ExpectedKeys =
    [
        "fk_render_jobs_asp_net_users_user_id|FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE RESTRICT",
        "fk_render_jobs_episodes_episode_id_user_id|FOREIGN KEY (episode_id, user_id) "
        + "REFERENCES episodes(id, user_id) ON DELETE CASCADE",
        "fk_render_jobs_story_versions_story_version_id_user_id|FOREIGN KEY (story_version_id, user_id) "
        + "REFERENCES story_versions(id, user_id) ON DELETE CASCADE",
    ];

    /// <summary>The closed status set of <c>ck_render_jobs_status</c>, the snake_case text of
    /// <see cref="RenderJobStatus"/>.</summary>
    private static readonly string[] AllStatuses =
        ["cancelled", "failed", "ingesting", "queued", "running", "submitted", "succeeded"];

    /// <summary>BD5's four active statuses: the filter of the partial unique index.</summary>
    private static readonly string[] ActiveStatuses = ["queued", "submitted", "running", "ingesting"];

    /// <summary>The two statuses the poller claims: the filter of the due-job index.</summary>
    private static readonly string[] PollableStatuses = ["submitted", "running"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_IsPresent_AndModelHasNoPendingChange()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        migrations.Any(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal)).ShouldBeTrue();

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "an entity change without a migration must fail here (run `dotnet ef migrations add`)");
        context.Model.FindEntityType(typeof(RenderJob))!.GetTableName().ShouldBe("render_jobs");
    }

    [Fact]
    public async Task Applies_WithTheColumnsTypesAndChecks()
    {
        SqlProbe.QueryStrings(
                Database,
                "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' "
                + "AND table_name = 'render_jobs' ORDER BY column_name")
            .ShouldBe(ExpectedColumns);

        SqlProbe.QueryStrings(
                Database,
                "SELECT column_name || '|' || data_type || '|' || coalesce(character_maximum_length::text, '') "
                + "|| '|' || is_nullable FROM information_schema.columns WHERE table_schema = 'public' "
                + "AND table_name = 'render_jobs' ORDER BY column_name")
            .ShouldBe(ExpectedColumnSpecs);

        // The status is the domain's closed set, not free text: the CHECK is the only thing between a typo in a
        // later handler and a row the poller can never claim.
        var check = SqlProbe.QueryStrings(
                Database,
                "SELECT conname || '|' || pg_get_constraintdef(c.oid) FROM pg_constraint c "
                + "WHERE c.conrelid = 'render_jobs'::regclass AND c.contype = 'c'")
            .ShouldHaveSingleItem();
        check.ShouldStartWith("ck_render_jobs_status|");
        foreach (var status in AllStatuses)
        {
            check.ShouldContain($"'{status}'");
        }

        var parents = await ParentsAsync();
        var unknown = Should.Throw<PostgresException>(
            () => SqlProbe.Execute(Database, InsertJobSql(parents, Guid.CreateVersion7(), "started")));
        unknown.SqlState.ShouldBe("23514");
        unknown.ConstraintName.ShouldBe("ck_render_jobs_status");
    }

    [Fact]
    public void Applies_WithTheFrozenIndexSet()
    {
        SqlProbe.QueryStrings(
                Database,
                "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'render_jobs'")
            .ShouldBe(ExpectedIndexes, ignoreOrder: true);

        // "One active render per episode" is the database rule (BD5), not a handler convention. The filter is
        // compared by the statuses it carries, never against the literal the model wrote: EF keeps
        // `"status" IN (...)`, while pg_get_indexdef returns PostgreSQL's normalised `status = ANY (ARRAY[...])`.
        var active = SqlProbe.QueryStrings(
                Database,
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
                + "AND indexname = 'ux_render_jobs_active_episode'")
            .ShouldHaveSingleItem();
        active.ShouldContain("UNIQUE");
        active.ShouldContain("(episode_id)");
        foreach (var status in ActiveStatuses)
        {
            active.ShouldContain($"'{status}'");
        }

        var poller = SqlProbe.QueryStrings(
                Database,
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
                + "AND indexname = 'ix_render_jobs_status_next_poll_at'")
            .ShouldHaveSingleItem();
        poller.ShouldNotContain("UNIQUE");
        poller.ShouldContain("(status, next_poll_at)");
        foreach (var status in PollableStatuses)
        {
            poller.ShouldContain($"'{status}'");
        }

        poller.ShouldNotContain("'queued'");
        poller.ShouldNotContain("'ingesting'");

        var tenant = SqlProbe.QueryStrings(
                Database,
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
                + "AND indexname = 'ix_render_jobs_user_id_episode_id'")
            .ShouldHaveSingleItem();
        tenant.ShouldNotContain("UNIQUE");
        tenant.ShouldNotContain("WHERE");
        tenant.ShouldContain("(user_id, episode_id)");
    }

    [Fact]
    public void Applies_WithTheSameOwnerKeys()
    {
        SqlProbe.QueryStrings(
                Database,
                """
                SELECT c.conname || '|' || pg_get_constraintdef(c.oid)
                FROM pg_constraint c
                WHERE c.conrelid = 'render_jobs'::regclass AND c.contype = 'f'
                ORDER BY 1
                """)
            .ShouldBe(ExpectedKeys);
    }

    [Fact]
    public async Task ActiveEpisodeIndex_AllowsOneActiveJobPerEpisode_AndTerminalRowsAccumulate()
    {
        var parents = await ParentsAsync();
        var queued = InsertJob(parents, "queued");

        var refused = Should.Throw<PostgresException>(
            () => SqlProbe.Execute(Database, InsertJobSql(parents, Guid.CreateVersion7(), "submitted")));
        refused.SqlState.ShouldBe("23505");
        refused.ConstraintName.ShouldBe("ux_render_jobs_active_episode");

        // A retry is a new row once the previous attempt is terminal (BD5): the failed row stays as that
        // attempt's history while the episode is free again.
        SqlProbe.Execute(Database, $"UPDATE render_jobs SET status = 'failed', stage = 'failed' WHERE id = '{queued}'");
        InsertJob(parents, "queued");
        InsertJob(parents, "succeeded", "done");

        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE episode_id = '{parents.EpisodeId}'")
            .ShouldBe(3, "two terminal rows and one active one coexist");
    }

    [Fact]
    public async Task DeletingTheEpisode_CascadesTheJobRow()
    {
        var parents = await ParentsAsync();
        var job = InsertJob(parents, "queued");

        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE id = '{job}'").ShouldBe(1);

        SqlProbe.Execute(Database, $"DELETE FROM episodes WHERE id = '{parents.EpisodeId}'");

        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE id = '{job}'")
            .ShouldBe(0, "the episode's hard delete takes its jobs with it through the same-owner composite key");
    }

    /// <summary>
    /// The migration applies over the schema one release behind, with the data the seed harness writes into it
    /// (<see cref="SeedPrevious"/>): the pre-existing rows come out untouched and <c>render_jobs</c> joins them
    /// empty. The seed skips a table the previous schema does not have, so this run writes no render job of its own.
    /// </summary>
    [Fact]
    public async Task Applies_OnThePreviousSchemaWithSeededData()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        var own = migrations.FindIndex(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        own.ShouldBeGreaterThan(0, "the migration must exist and have a previous one to revert to");
        var migrator = context.Database.GetService<IMigrator>();
        var fixture = Database.Fixture;
        var name = Database.Name;

        try
        {
            await migrator.MigrateAsync(migrations[own - 1], Ct);
            SqlProbe.ScalarText(Database, "SELECT coalesce(to_regclass('public.render_jobs')::text, 'gone')")
                .ShouldBe("gone");

            var inherited = await SeedPrevious.CaptureTablesAsync(fixture, name, Ct);
            var seeded = await SeedPrevious.ExecuteAsync(fixture, name, Ct);
            seeded.ShouldNotContain("render_jobs", "the previous schema has no table to seed a job into");
            seeded.ShouldContain("episodes", "the migration must be exercised against data, not an empty schema");
            var before = Snapshot(await SeedPrevious.SnapshotAsync(fixture, name, inherited, Ct));

            await migrator.MigrateAsync(migrations[own], Ct);

            SqlProbe.Count(Database, "SELECT count(*) FROM render_jobs").ShouldBe(0);

            var after = Snapshot(await SeedPrevious.SnapshotAsync(fixture, name, inherited, Ct));
            foreach (var table in inherited.Select(shape => shape.Table))
            {
                after[table].ShouldBe(before[table], $"the migration must not touch the rows of {table}");
            }
        }
        finally
        {
            // The class database is always left at the latest migration.
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
        }
    }

    [Fact]
    public async Task Down_DropsTheTableAndItsIndexes_AndReApplies()
    {
        var parents = await ParentsAsync();
        InsertJob(parents, "queued");

        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        var own = migrations.FindIndex(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        own.ShouldBeGreaterThan(0, "the migration must exist and have a previous one to revert to");
        var migrator = context.Database.GetService<IMigrator>();

        try
        {
            await migrator.MigrateAsync(migrations[own - 1], Ct);

            SqlProbe.ScalarText(Database, "SELECT coalesce(to_regclass('public.render_jobs')::text, 'gone')")
                .ShouldBe("gone", "Down drops the table");
            SqlProbe.Count(
                    Database,
                    "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'render_jobs'")
                .ShouldBe(0, "the drop takes the table's indexes with it");
            SqlProbe.Count(Database, $"SELECT count(*) FROM episodes WHERE id = '{parents.EpisodeId}'")
                .ShouldBe(1, "the episode the job pointed at stays");
        }
        finally
        {
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
        }

        // Re-applied (01 §3.3 expand-only), the table and its index set are back; only the data of the table
        // itself is lost.
        SqlProbe.Count(Database, "SELECT count(*) FROM render_jobs").ShouldBe(0, "the recreated table is empty");
        SqlProbe.QueryStrings(
                Database,
                "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'render_jobs'")
            .ShouldBe(ExpectedIndexes, ignoreOrder: true);
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    /// <summary>One user with a series, an episode and one story version, written through the TestKit builders.</summary>
    private async Task<Parents> ParentsAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);

        return new Parents(user.Id, episode.Id, InsertVersion(user.Id, episode.Id));
    }

    private Guid InsertVersion(Guid userId, Guid episodeId)
    {
        var id = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            "INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, "
            + "word_count, content, created_at) VALUES ("
            + $"'{id}', '{userId}', '{episodeId}', 1, 'generated', 'Seed title', 'Seed body', '{{}}', 2, "
            + "'{}'::jsonb, now())");

        return id;
    }

    private Guid InsertJob(Parents parents, string status, string stage = "queued")
    {
        var id = Guid.CreateVersion7();
        SqlProbe.Execute(Database, InsertJobSql(parents, id, status, stage: stage));

        return id;
    }

    /// <summary>
    /// The row the render command writes, with the columns every insert must supply; the stage is the caller's
    /// because a terminal attempt carries the stage it reached.
    /// </summary>
    private static string InsertJobSql(Parents parents, Guid id, string status, int attempt = 1, string stage = "queued") =>
        "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, "
        + "percent, params, attempt) VALUES ("
        + $"'{id}', '{parents.UserId}', '{parents.EpisodeId}', '{parents.StoryVersionId}', "
        + $"'{Guid.CreateVersion7()}', '{status}', '{stage}', 0, '{{}}'::jsonb, {attempt})";

    /// <summary>Turns <c>table=count:md5</c> snapshot lines into a table → fingerprint lookup.</summary>
    private static Dictionary<string, string> Snapshot(IReadOnlyList<string> lines) => lines.ToDictionary(
        line => line[..line.IndexOf('=', StringComparison.Ordinal)],
        line => line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..],
        StringComparer.Ordinal);

    private sealed record Parents(Guid UserId, Guid EpisodeId, Guid StoryVersionId);
}
