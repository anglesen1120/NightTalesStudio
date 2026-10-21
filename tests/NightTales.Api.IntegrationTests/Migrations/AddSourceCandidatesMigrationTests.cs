using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Research;
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
/// The <c>_02_AddSourceCandidates</c> migration (02.7.2-T2): the <c>source_candidates</c> table with its
/// snake_case status CHECK, the two plan §3 invariants (one URL per episode, <c>added ⇔ source_id</c>), the list
/// index and the three same-owner foreign keys of 09 W3 CR-46. Up is expand-only (create table and indexes); the
/// episode cascade is D19, and the run/source cascades are this migration's deviation from the plan's unqualified
/// "FK →" (both are NOT NULL or must stay consistent with the added invariant).
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class AddSourceCandidatesMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string MigrationName = "02_AddSourceCandidates";

    /// <summary>The columns in <c>name|type|length|nullable</c> form, in column-name order.</summary>
    private static readonly string[] ExpectedColumnSpecs =
    [
        "created_at|timestamp with time zone||NO",
        "episode_id|uuid||NO",
        "generation_run_id|uuid||NO",
        "id|uuid||NO",
        "normalized_url|character varying|2048|NO",
        "rank|smallint||NO",
        "registrable_domain|character varying|253|NO",
        "site_name|text||NO",
        "source_id|uuid||YES",
        "status|text||NO",
        "title|character varying|300|YES",
        "updated_at|timestamp with time zone||NO",
        "url|character varying|2048|NO",
        "user_id|uuid||NO",
    ];

    private static readonly string[] ExpectedCheckConstraints =
    [
        "ck_source_candidates_added",
        "ck_source_candidates_rank",
        "ck_source_candidates_status",
    ];

    private static readonly string[] ExpectedIndexes =
    [
        "ix_source_candidates_episode_id_user_id",
        "ix_source_candidates_episode_status_rank",
        "ix_source_candidates_generation_run_id_user_id",
        "ix_source_candidates_source_id_user_id",
        "ix_source_candidates_user_id",
        "pk_source_candidates",
        "ux_source_candidates_episode_url",
    ];

    /// <summary>The three same-owner keys (09 W3 CR-46), as <c>name|definition</c>.</summary>
    private static readonly string[] ExpectedSameOwnerKeys =
    [
        "fk_source_candidates_episodes_episode_id_user_id|FOREIGN KEY (episode_id, user_id) "
        + "REFERENCES episodes(id, user_id) ON DELETE CASCADE",
        "fk_source_candidates_generation_runs_generation_run_id_user_id|FOREIGN KEY "
        + "(generation_run_id, user_id) REFERENCES generation_runs(id, user_id) ON DELETE CASCADE",
        "fk_source_candidates_story_sources_source_id_user_id|FOREIGN KEY (source_id, user_id) "
        + "REFERENCES story_sources(id, user_id) ON DELETE CASCADE",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_IsPresent_AndModelHasNoPendingChange()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        // 02.8.1-T1 appended a migration after this one, so this class no longer owns the last-migration
        // check (AddRenderJobWorstCaseUsdMigrationTests does); it still proves its own migration is compiled in.
        migrations.ShouldContain(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "an entity change without a migration must fail here (run `dotnet ef migrations add`)");
        context.Model.FindEntityType(typeof(SourceCandidate))!.GetTableName().ShouldBe("source_candidates");
    }

    [Fact]
    public void Applies_WithTheColumnsChecksAndIndexes()
    {
        SqlProbe.QueryStrings(
                Database,
                "SELECT column_name || '|' || data_type || '|' || coalesce(character_maximum_length::text, '') "
                + "|| '|' || is_nullable FROM information_schema.columns WHERE table_schema = 'public' "
                + "AND table_name = 'source_candidates' ORDER BY column_name")
            .ShouldBe(ExpectedColumnSpecs);

        SqlProbe.QueryStrings(
                Database,
                "SELECT conname FROM pg_constraint WHERE conrelid = 'source_candidates'::regclass AND contype = 'c' "
                + "ORDER BY conname")
            .ShouldBe(ExpectedCheckConstraints);

        SqlProbe.QueryStrings(
                Database,
                "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' "
                + "AND tablename = 'source_candidates' ORDER BY indexname")
            .ShouldBe(ExpectedIndexes);

        var unique = SqlProbe.QueryStrings(
                Database,
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
                + "AND indexname = 'ux_source_candidates_episode_url'")
            .ShouldHaveSingleItem();
        unique.ShouldContain("UNIQUE");
        unique.ShouldContain("(episode_id, normalized_url)");

        SqlProbe.QueryStrings(
                Database,
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
                + "AND indexname = 'ix_source_candidates_episode_status_rank'")
            .ShouldHaveSingleItem()
            .ShouldContain("(episode_id, status, rank)");
    }

    [Fact]
    public void SameOwnerKeys_AreCompositeAndCascade()
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
                      AND cl.relname = 'source_candidates'
                      AND array_length(c.conkey, 1) > 1
                ORDER BY 1
                """)
            .ShouldBe(ExpectedSameOwnerKeys);
    }

    [Fact]
    public async Task Checks_RejectAnUnknownStatusABadRankAndAnAddedMismatch()
    {
        var parents = await ParentsAsync();

        var statusFailure = Should.Throw<PostgresException>(
            () => InsertCandidate(parents, status: "accepted"));
        statusFailure.SqlState.ShouldBe("23514");
        statusFailure.ConstraintName.ShouldBe("ck_source_candidates_status");

        var rankFailure = Should.Throw<PostgresException>(
            () => InsertCandidate(parents, rank: 0, url: "https://example.com/rank"));
        rankFailure.SqlState.ShouldBe("23514");
        rankFailure.ConstraintName.ShouldBe("ck_source_candidates_rank");

        // An added row without a source, and a proposed row that already names one, are both refused:
        // the invariant is an equivalence, not an implication.
        var addedWithoutSource = Should.Throw<PostgresException>(
            () => InsertCandidate(parents, status: "added", url: "https://example.com/added"));
        addedWithoutSource.SqlState.ShouldBe("23514");
        addedWithoutSource.ConstraintName.ShouldBe("ck_source_candidates_added");

        var proposedWithSource = Should.Throw<PostgresException>(
            () => InsertCandidate(parents, status: "proposed", sourceId: parents.SourceA, url: "https://example.com/proposed"));
        proposedWithSource.SqlState.ShouldBe("23514");
        proposedWithSource.ConstraintName.ShouldBe("ck_source_candidates_added");
    }

    [Fact]
    public async Task UniqueIndex_AllowsOneUrlPerEpisode_AcrossRuns()
    {
        var parents = await ParentsAsync();
        InsertCandidate(parents, url: "https://example.com/once");

        // The second run proposes the same URL again: the same normalised URL, a different cited spelling.
        var duplicate = Should.Throw<PostgresException>(
            () => InsertCandidate(
                parents,
                runId: parents.RunB,
                url: "https://example.com/once#section",
                normalizedUrl: "https://example.com/once",
                rank: 2));
        duplicate.SqlState.ShouldBe("23505");
        duplicate.ConstraintName.ShouldBe("ux_source_candidates_episode_url");

        // Another episode of the same user may hold the same URL.
        InsertCandidate(
            parents, episodeId: parents.EpisodeB, runId: parents.RunB, url: "https://example.com/once");
    }

    [Fact]
    public async Task DeletingTheEpisode_CascadesItsCandidates()
    {
        var parents = await ParentsAsync();
        var candidate = InsertCandidate(parents, url: "https://example.com/episode-cascade");

        SqlProbe.Execute(Database, $"DELETE FROM episodes WHERE id = '{parents.EpisodeA}'");

        SqlProbe.Count(Database, $"SELECT count(*) FROM source_candidates WHERE id = '{candidate}'")
            .ShouldBe(0);
    }

    [Fact]
    public async Task DeletingTheSource_CascadesTheAddedCandidate()
    {
        var parents = await ParentsAsync();
        var candidate = InsertCandidate(
            parents, status: "added", sourceId: parents.SourceA, url: "https://example.com/source-cascade");

        SqlProbe.Execute(Database, $"DELETE FROM story_sources WHERE id = '{parents.SourceA}'");

        SqlProbe.Count(Database, $"SELECT count(*) FROM source_candidates WHERE id = '{candidate}'")
            .ShouldBe(0);
    }

    [Fact]
    public async Task DeletingTheRun_CascadesItsCandidates()
    {
        var parents = await ParentsAsync();
        var candidate = InsertCandidate(parents, url: "https://example.com/run-cascade");

        SqlProbe.Execute(Database, $"DELETE FROM generation_runs WHERE id = '{parents.RunA}'");

        SqlProbe.Count(Database, $"SELECT count(*) FROM source_candidates WHERE id = '{candidate}'")
            .ShouldBe(0);
    }

    [Fact]
    public async Task Migration_HasOnlyCreateOperations()
    {
        await using var context = Database.CreateAppDbContext();
        var migration = CompiledMigration(context, MigrationName);

        MigrationGuard.FindViolations([migration]).ShouldBeEmpty();
    }

    [Fact]
    public async Task Down_DropsOnlyTheCandidatesTable_AndReApplies()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        var own = migrations.FindIndex(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        own.ShouldBeGreaterThan(0, "the migration must exist and have a previous one to revert to");
        var migrator = context.Database.GetService<IMigrator>();

        try
        {
            await migrator.MigrateAsync(migrations[own - 1], Ct);
            SqlProbe.ScalarText(Database, "SELECT coalesce(to_regclass('public.source_candidates')::text, 'gone')")
                .ShouldBe("gone");
            // The parents of the new keys and the previous feature's table are untouched by the Down.
            SqlProbe.ScalarText(Database, "SELECT coalesce(to_regclass('public.story_sources')::text, 'gone')")
                .ShouldNotBe("gone");
            SqlProbe.ScalarText(Database, "SELECT coalesce(to_regclass('public.platform_quota_usage')::text, 'gone')")
                .ShouldNotBe("gone");
        }
        finally
        {
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
        }

        SqlProbe.Count(Database, "SELECT count(*) FROM source_candidates").ShouldBe(0, "the recreated table is empty");
    }

    private static string InsertCandidateSql(
        Parents parents,
        Guid id,
        Guid episodeId,
        Guid runId,
        string url,
        string normalizedUrl,
        short rank,
        string status,
        Guid? sourceId) =>
        "INSERT INTO source_candidates (id, user_id, episode_id, generation_run_id, url, normalized_url, "
        + "registrable_domain, title, site_name, rank, status, source_id, created_at, updated_at) VALUES ("
        + $"'{id}', '{parents.UserId}', '{episodeId}', '{runId}', '{url}', '{normalizedUrl}', "
        + "'example.com', 'Seed candidate', 'Example', "
        + $"{rank}, '{status}', "
        + (sourceId is null ? "NULL" : $"'{sourceId}'")
        + ", now(), now())";

    private Guid InsertCandidate(
        Parents parents,
        Guid? episodeId = null,
        Guid? runId = null,
        string url = "https://example.com/a",
        string? normalizedUrl = null,
        short rank = 1,
        string status = "proposed",
        Guid? sourceId = null)
    {
        var id = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            InsertCandidateSql(
                parents,
                id,
                episodeId ?? parents.EpisodeA,
                runId ?? parents.RunA,
                url,
                normalizedUrl ?? url,
                rank,
                status,
                sourceId));

        return id;
    }

    /// <summary>One user with a series, two episodes, one run each and one pending source on the first episode.</summary>
    private async Task<Parents> ParentsAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episodeA = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);
        var episodeB = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);

        var runA = InsertRun(user.Id, series.Id, episodeA.Id, runKind: "find_sources");
        var runB = InsertRun(user.Id, series.Id, episodeB.Id, runKind: "find_sources");

        var sourceA = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            "INSERT INTO story_sources (id, user_id, episode_id, kind, url, normalized_url, registrable_domain, "
            + $"status, created_at) VALUES ('{sourceA}', '{user.Id}', '{episodeA.Id}', 'url', "
            + "'https://example.com/seed-source', 'https://example.com/seed-source', 'example.com', 'pending', now())");

        return new Parents(user.Id, series.Id, episodeA.Id, episodeB.Id, runA, runB, sourceA);
    }

    private Guid InsertRun(Guid userId, Guid seriesId, Guid episodeId, string runKind)
    {
        var id = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            "INSERT INTO generation_runs (id, user_id, series_id, episode_id, kind, \"trigger\", status, stage, "
            + "counts_toward_limit, worst_case_usd, queued_at, created_at, updated_at) VALUES ("
            + $"'{id}', '{userId}', '{seriesId}', '{episodeId}', '{runKind}', 'user', 'queued', 'queued', "
            + "false, 0, now(), now(), now())");

        return id;
    }

    private static Migration CompiledMigration(AppDbContext context, string idFragment)
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        var type = assembly.Migrations
            .Single(pair => pair.Key.Contains(idFragment, StringComparison.Ordinal))
            .Value;

        return (Migration)Activator.CreateInstance(type, nonPublic: true)!;
    }

    private sealed record Parents(
        Guid UserId,
        Guid SeriesId,
        Guid EpisodeA,
        Guid EpisodeB,
        Guid RunA,
        Guid RunB,
        Guid SourceA);
}
