using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Autopilot.Ideas;
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
/// The <c>_02_AddEpisodeIdeas</c> migration (02.8.1-T1): the <c>episode_ideas</c> table — the four texts,
/// the canonical <c>structure</c> document (D3), the <c>novelty</c>/<c>score</c>/<c>similarity</c> columns
/// 02.8.1-T2 fills, the snake_case status CHECK, the list index, the partial unique index that makes
/// "one idea per episode" (plan §3.1) a database rule, and the three same-owner foreign keys of 09 W3
/// CR-46. Up is expand-only (create table and indexes plus the raw <c>SET NULL (episode_id)</c> rewrite);
/// the series and run keys cascade, and the episode key clears only its own column, so deleting the
/// episode an idea became records the pick without losing the row (plan §5).
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class AddEpisodeIdeasMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string MigrationName = "02_AddEpisodeIdeas";

    /// <summary>A flat <c>structure</c> document of the shape the strict schema accepts.</summary>
    private const string StructureJson =
        """{"sub_genre":"x","setting":"x","opening":"x","twist_type":"x","narrator_mode":"x","beats":["a","b","c"],"beat_signature":"a>b>c"}""";

    /// <summary>The columns in <c>name|type|length|nullable</c> form, in column-name order.</summary>
    private static readonly string[] ExpectedColumnSpecs =
    [
        "created_at|timestamp with time zone||NO",
        "episode_id|uuid||YES",
        "generation_run_id|uuid||NO",
        "hook|character varying|300|NO",
        "id|uuid||NO",
        "logline|character varying|300|NO",
        "novelty|jsonb||YES",
        "rejected_reason|character varying|40|YES",
        "score|numeric||YES",
        "series_id|uuid||NO",
        "setting|character varying|120|NO",
        "similarity|numeric||YES",
        "status|text||NO",
        "structure|jsonb||NO",
        "title|character varying|100|NO",
        "updated_at|timestamp with time zone||NO",
        "user_id|uuid||NO",
    ];

    private static readonly string[] ExpectedCheckConstraints = ["ck_episode_ideas_status"];

    /// <summary>
    /// The index set the plan §3.1 fixes: the primary key, the owner index, the three same-owner
    /// composites, the run traceability index, the run log/list index and the partial unique index of the
    /// episode link. Compared without order because EF names the plan's indexes in title case and the
    /// converter-generated ones in snake_case.
    /// </summary>
    private static readonly string[] ExpectedIndexes =
    [
        "IX_episode_ideas_generation_run",
        "IX_episode_ideas_series_status_created",
        "UX_episode_ideas_episode",
        "ix_episode_ideas_episode_id_user_id",
        "ix_episode_ideas_generation_run_id_user_id",
        "ix_episode_ideas_series_id_user_id",
        "ix_episode_ideas_user_id",
        "pk_episode_ideas",
    ];

    /// <summary>The three same-owner keys (09 W3 CR-46), as <c>name|definition</c>.</summary>
    private static readonly string[] ExpectedSameOwnerKeys =
    [
        "fk_episode_ideas_episodes_episode_id_user_id|FOREIGN KEY (episode_id, user_id) "
        + "REFERENCES episodes(id, user_id) ON DELETE SET NULL (episode_id)",
        "fk_episode_ideas_generation_runs_generation_run_id_user_id|FOREIGN KEY "
        + "(generation_run_id, user_id) REFERENCES generation_runs(id, user_id) ON DELETE CASCADE",
        "fk_episode_ideas_series_series_id_user_id|FOREIGN KEY (series_id, user_id) "
        + "REFERENCES series(id, user_id) ON DELETE CASCADE",
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
        context.Model.FindEntityType(typeof(EpisodeIdea))!.GetTableName().ShouldBe("episode_ideas");
    }

    [Fact]
    public void Applies_WithTheColumnsCheckAndIndexes()
    {
        SqlProbe.QueryStrings(
                Database,
                "SELECT column_name || '|' || data_type || '|' || coalesce(character_maximum_length::text, '') "
                + "|| '|' || is_nullable FROM information_schema.columns WHERE table_schema = 'public' "
                + "AND table_name = 'episode_ideas' ORDER BY column_name")
            .ShouldBe(ExpectedColumnSpecs);

        SqlProbe.QueryStrings(
                Database,
                "SELECT conname FROM pg_constraint WHERE conrelid = 'episode_ideas'::regclass AND contype = 'c' "
                + "ORDER BY conname")
            .ShouldBe(ExpectedCheckConstraints);

        SqlProbe.QueryStrings(
                Database,
                "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' "
                + "AND tablename = 'episode_ideas'")
            .ShouldBe(ExpectedIndexes, ignoreOrder: true);

        // "One idea per episode" (plan §3.1) is a database rule, and it is partial: a proposed or rejected
        // idea has no episode, so those rows must not collide on the NULL they share.
        var unique = SqlProbe.QueryStrings(
                Database,
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
                + "AND indexname = 'UX_episode_ideas_episode'")
            .ShouldHaveSingleItem();
        unique.ShouldContain("UNIQUE");
        unique.ShouldContain("(episode_id)");
        unique.ShouldContain("episode_id IS NOT NULL");

        // The run log and 02.8.1-T2's corpus read a series' ideas by status, newest first.
        SqlProbe.QueryStrings(
                Database,
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
                + "AND indexname = 'IX_episode_ideas_series_status_created'")
            .ShouldHaveSingleItem()
            .ShouldContain("(series_id, status, created_at DESC)");

        SqlProbe.QueryStrings(
                Database,
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
                + "AND indexname = 'IX_episode_ideas_generation_run'")
            .ShouldHaveSingleItem()
            .ShouldContain("(generation_run_id)");
    }

    [Fact]
    public void SameOwnerKeys_AreCompositeAndCascadeOrClearTheEpisodeColumn()
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
                      AND cl.relname = 'episode_ideas'
                      AND array_length(c.conkey, 1) > 1
                ORDER BY 1
                """)
            .ShouldBe(ExpectedSameOwnerKeys);
    }

    [Fact]
    public async Task StatusCheck_RejectsAnUnknownValueAndAcceptsTheFourStates()
    {
        var parents = await ParentsAsync();

        var failure = Should.Throw<PostgresException>(() => InsertIdea(parents, status: "bogus"));
        failure.SqlState.ShouldBe("23514");
        failure.ConstraintName.ShouldBe("ck_episode_ideas_status");

        // The closed set of the plan §3.1 state machine, all four accepted by the storage rule itself.
        var accepted = new[] { "proposed", "picked", "rejected", "used" };
        foreach (var status in accepted)
        {
            InsertIdea(parents, status: status);
        }

        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM episode_ideas WHERE user_id = '{parents.Owner}' AND status = ANY(ARRAY['proposed', "
                + "'picked', 'rejected', 'used'])")
            .ShouldBe(accepted.Length);
    }

    [Fact]
    public async Task Insert_StoresTheStructureDocumentAndTheMeasurements()
    {
        var parents = await ParentsAsync();
        var idea = InsertIdea(
            parents,
            status: "picked",
            noveltyJson: """{"lexical":0.25}""",
            score: 0.8125m,
            similarity: 0.5m);

        var row = SqlProbe.QueryStrings(
            Database,
            $"SELECT structure->>'beat_signature' || '|' || coalesce(novelty->>'lexical', 'null') || '|' "
            + $"|| score::text || '|' || similarity::text FROM episode_ideas WHERE id = '{idea}'");

        // numeric(5,4) keeps the scale, so the round-tripped text is the canonical form 02.8.1-T2 reads.
        string[] expected = [$"a>b>c|0.25|0.8125|0.5000"];
        row.ShouldBe(expected);
    }

    [Fact]
    public async Task UniqueEpisodeIndex_AllowsManyUnboundIdeasAndOnePerEpisode()
    {
        var parents = await ParentsAsync();
        InsertIdea(parents, episodeId: parents.EpisodeA);

        // The second call of MarkUsedAsync loses this race (409 idea_invalid_state), so the index is the
        // control, not the command.
        var duplicate = Should.Throw<PostgresException>(
            () => InsertIdea(parents, episodeId: parents.EpisodeA));
        duplicate.SqlState.ShouldBe("23505");
        duplicate.ConstraintName.ShouldBe("UX_episode_ideas_episode");

        // An unbound idea (proposed, rejected) may share the NULL with as many siblings as it likes, and a
        // different episode of the same user may hold its own idea.
        InsertIdea(parents);
        InsertIdea(parents);
        InsertIdea(parents, episodeId: parents.EpisodeB);

        SqlProbe.Count(Database, $"SELECT count(*) FROM episode_ideas WHERE user_id = '{parents.Owner}'")
            .ShouldBe(4);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM episode_ideas WHERE user_id = '{parents.Owner}' AND episode_id IS NULL")
            .ShouldBe(2);
    }

    [Fact]
    public async Task CrossTenantIdeas_AreRejectedByTheirSameOwnerKeys()
    {
        var parents = await ParentsAsync();

        // User A's idea may not name user B's series, run or episode: each row violates exactly one key,
        // so the constraint name in the error is the evidence, not a coincidence of ordering.
        var series = Should.Throw<PostgresException>(
            () => InsertIdea(parents, seriesId: parents.SeriesIntruder));
        series.SqlState.ShouldBe("23503");
        series.ConstraintName.ShouldBe("fk_episode_ideas_series_series_id_user_id");

        var run = Should.Throw<PostgresException>(
            () => InsertIdea(parents, runId: parents.RunIntruder));
        run.SqlState.ShouldBe("23503");
        run.ConstraintName.ShouldBe("fk_episode_ideas_generation_runs_generation_run_id_user_id");

        var episode = Should.Throw<PostgresException>(
            () => InsertIdea(parents, episodeId: parents.EpisodeIntruder));
        episode.SqlState.ShouldBe("23503");
        episode.ConstraintName.ShouldBe("fk_episode_ideas_episodes_episode_id_user_id");

        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM episode_ideas WHERE user_id = '{parents.Owner}'")
            .ShouldBe(0, "every insert above carried user A's id, so no idea of theirs may exist");
    }

    [Fact]
    public async Task DeletingTheEpisode_ClearsOnlyItsColumn_AndKeepsTheIdea()
    {
        var parents = await ParentsAsync();
        var idea = InsertIdea(parents, episodeId: parents.EpisodeA);

        SqlProbe.Execute(Database, $"DELETE FROM episodes WHERE id = '{parents.EpisodeA}'");

        // The column-list SET NULL of the migration: the reference to the episode goes, the row and its
        // owner do not (a plain SET NULL would try to null the NOT NULL user_id and fail the delete).
        SqlProbe.QueryStrings(
                Database,
                $"SELECT user_id::text || '|' || coalesce(episode_id::text, 'null') FROM episode_ideas "
                + $"WHERE id = '{idea}'")
            .ShouldBe([$"{parents.Owner}|null"]);
    }

    [Fact]
    public async Task DeletingTheSeries_CascadesItsIdeas()
    {
        var parents = await ParentsAsync();

        // The idea's run lives on the other series: generation_runs -> series is RESTRICT, so a series that
        // owns a run cannot be deleted at all. `SeriesEmpty` owns nothing else, so the series key is the only
        // link left, and the cascade it carries is what removes the idea.
        var idea = InsertIdea(parents, seriesId: parents.SeriesEmpty, episodeId: null);

        SqlProbe.Execute(Database, $"DELETE FROM series WHERE id = '{parents.SeriesEmpty}'");

        SqlProbe.Count(Database, $"SELECT count(*) FROM episode_ideas WHERE id = '{idea}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE id = '{parents.RunA}'")
            .ShouldBe(1, "the run the idea pointed at belongs to another series and must survive");
    }

    [Fact]
    public async Task DeletingTheRun_CascadesItsIdeas()
    {
        var parents = await ParentsAsync();
        var idea = InsertIdea(parents);

        SqlProbe.Execute(Database, $"DELETE FROM generation_runs WHERE id = '{parents.RunA}'");

        SqlProbe.Count(Database, $"SELECT count(*) FROM episode_ideas WHERE id = '{idea}'").ShouldBe(0);
    }

    [Fact]
    public async Task Migration_HasOnlyCreateOperations()
    {
        await using var context = Database.CreateAppDbContext();
        var migration = CompiledMigration(context, MigrationName);

        MigrationGuard.FindViolations([migration]).ShouldBeEmpty();
    }

    [Fact]
    public async Task Down_DropsOnlyTheIdeasTable_AndReApplies()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        var own = migrations.FindIndex(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        own.ShouldBeGreaterThan(0, "the migration must exist and have a previous one to revert to");
        var migrator = context.Database.GetService<IMigrator>();

        try
        {
            await migrator.MigrateAsync(migrations[own - 1], Ct);
            SqlProbe.ScalarText(Database, "SELECT coalesce(to_regclass('public.episode_ideas')::text, 'gone')")
                .ShouldBe("gone");
            // The parents of the new keys and the previous feature's table are untouched by the Down.
            SqlProbe.ScalarText(Database, "SELECT coalesce(to_regclass('public.series')::text, 'gone')")
                .ShouldNotBe("gone");
            SqlProbe.ScalarText(Database, "SELECT coalesce(to_regclass('public.source_candidates')::text, 'gone')")
                .ShouldNotBe("gone");
        }
        finally
        {
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
        }

        SqlProbe.Count(Database, "SELECT count(*) FROM episode_ideas").ShouldBe(0, "the recreated table is empty");
    }

    private static string InsertIdeaSql(
        Parents parents,
        Guid id,
        Guid seriesId,
        Guid runId,
        string status,
        string? noveltyJson,
        decimal? score,
        decimal? similarity,
        Guid? episodeId) =>
        "INSERT INTO episode_ideas (id, user_id, series_id, generation_run_id, title, logline, hook, setting, "
        + "structure, novelty, score, similarity, status, rejected_reason, episode_id, created_at, updated_at) "
        + $"VALUES ('{id}', '{parents.Owner}', '{seriesId}', '{runId}', 'Seed idea', 'Seed logline', "
        + $"'Seed hook', 'Seed setting', '{StructureJson}'::jsonb, "
        + (noveltyJson is null ? "NULL" : $"'{noveltyJson}'::jsonb") + ", "
        + (score is null ? "NULL" : score.Value.ToString(CultureInfo.InvariantCulture)) + ", "
        + (similarity is null ? "NULL" : similarity.Value.ToString(CultureInfo.InvariantCulture)) + ", "
        + $"'{status}', NULL, " + (episodeId is null ? "NULL" : $"'{episodeId}'") + ", now(), now())";

    private Guid InsertIdea(
        Parents parents,
        Guid? seriesId = null,
        Guid? runId = null,
        Guid? episodeId = null,
        string status = "proposed",
        string? noveltyJson = null,
        decimal? score = null,
        decimal? similarity = null)
    {
        var id = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            InsertIdeaSql(
                parents,
                id,
                seriesId ?? parents.SeriesA,
                runId ?? parents.RunA,
                status,
                noveltyJson,
                score,
                similarity,
                episodeId));

        return id;
    }

    /// <summary>
    /// One user with two series (the second empty, so it can be deleted at all) and two episodes, plus an
    /// intruder with a series and an episode of their own (the other tenant the same-owner keys must
    /// refuse), and one <c>ideas</c> run of each user. The runs carry no episode, which is the shape
    /// <c>ck_generation_runs_scope</c> requires of that kind and what keeps an episode delete from
    /// cascading into the run.
    /// </summary>
    private async Task<Parents> ParentsAsync()
    {
        var actor = new TestCurrentUser();
        var users = Database.CreateUserBuilder(actor);
        var owner = await users.CreateAsync(ct: Ct);
        var intruder = await users.CreateAsync(ct: Ct);

        var seriesBuilder = new SeriesBuilder(Database, actor);
        var seriesA = await seriesBuilder.CreateAsync(owner.Id, title: "Owner series", ct: Ct);
        var seriesEmpty = await seriesBuilder.CreateAsync(owner.Id, title: "Owner series without a run", ct: Ct);
        var seriesIntruder = await seriesBuilder.CreateAsync(intruder.Id, title: "Intruder series", ct: Ct);

        var episodeBuilder = new EpisodeBuilder(Database, actor);
        var episodeA = await episodeBuilder.CreateAsync(owner.Id, seriesA.Id, ct: Ct);
        var episodeB = await episodeBuilder.CreateAsync(owner.Id, seriesA.Id, ct: Ct);
        var episodeIntruder = await episodeBuilder.CreateAsync(intruder.Id, seriesIntruder.Id, ct: Ct);

        var runA = InsertRun(owner.Id, seriesA.Id);
        var runIntruder = InsertRun(intruder.Id, seriesIntruder.Id);

        return new Parents(
            owner.Id,
            seriesA.Id,
            seriesEmpty.Id,
            seriesIntruder.Id,
            episodeA.Id,
            episodeB.Id,
            episodeIntruder.Id,
            runA,
            runIntruder);
    }

    private Guid InsertRun(Guid userId, Guid seriesId)
    {
        var id = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            "INSERT INTO generation_runs (id, user_id, series_id, episode_id, kind, \"trigger\", status, stage, "
            + "counts_toward_limit, worst_case_usd, queued_at, created_at, updated_at) VALUES ("
            + $"'{id}', '{userId}', '{seriesId}', NULL, 'ideas', 'user', 'queued', 'queued', "
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
        Guid Owner,
        Guid SeriesA,
        Guid SeriesEmpty,
        Guid SeriesIntruder,
        Guid EpisodeA,
        Guid EpisodeB,
        Guid EpisodeIntruder,
        Guid RunA,
        Guid RunIntruder);
}
