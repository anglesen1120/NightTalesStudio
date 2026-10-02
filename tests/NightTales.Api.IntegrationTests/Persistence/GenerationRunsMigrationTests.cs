using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Persistence;

/// <summary>
/// The <c>_02_GenerationRuns</c> migration and the entities it persists (02.2.2-T1): the table of 02 D2 with
/// both partial unique indexes and every CHECK, the CR-4 columns of <c>story_versions</c>, the append-only rule of
/// <c>StoryVersion</c> and the episode row lock. The shape is read from the EF model and from the live schema of
/// the class database, like the other migration tests.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class GenerationRunsMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly string[] ExpectedColumns =
    [
        "attempt", "cost_usd", "counts_toward_limit", "created_at", "episode_id", "error_code", "error_detail",
        "finished_at", "id", "idempotency_key", "input_fact_sheet_id", "kind", "models", "pause_reason",
        "previous_episode_status", "prompt_version", "queued_at", "reservation_id", "result_fact_sheet_id",
        "result_story_version_id", "series_id", "stage", "started_at", "status", "trigger", "updated_at",
        "user_id", "worst_case_usd",
    ];

    private static readonly string[] ExpectedChecks =
    [
        "ck_generation_runs_failed", "ck_generation_runs_kind", "ck_generation_runs_money",
        "ck_generation_runs_pause", "ck_generation_runs_prev", "ck_generation_runs_scope",
        "ck_generation_runs_stage", "ck_generation_runs_status", "ck_generation_runs_times",
        "ck_generation_runs_trigger",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_CreatesGenerationRunsWithTheD2Shape()
    {
        await using var context = Database.CreateAppDbContext();

        var entity = context.Model.FindEntityType(typeof(GenerationRun));
        entity.ShouldNotBeNull();
        entity!.GetTableName().ShouldBe("generation_runs");

        SqlProbe.QueryStrings(
                Database,
                "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' "
                + "AND table_name = 'generation_runs' ORDER BY column_name")
            .ShouldBe(ExpectedColumns);

        // The columns whose type or default the plan fixes.
        SqlProbe.QueryStrings(
                Database,
                "SELECT column_name || ':' || data_type || ':' || is_nullable || ':' || coalesce(column_default, '') "
                + "|| ':' || coalesce(numeric_precision::text || ',' || numeric_scale::text, '') "
                + "FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'generation_runs' "
                + "AND column_name IN ('worst_case_usd', 'cost_usd', 'counts_toward_limit', 'attempt', 'models', "
                + "'queued_at', 'started_at', 'episode_id', 'series_id') ORDER BY column_name")
            .ShouldBe(
            [
                "attempt:integer:NO:0:32,0",
                "cost_usd:numeric:YES::12,6",
                "counts_toward_limit:boolean:NO:true:",
                "episode_id:uuid:YES::",
                "models:jsonb:YES::",
                "queued_at:timestamp with time zone:NO::",
                "series_id:uuid:NO::",
                "started_at:timestamp with time zone:YES::",
                "worst_case_usd:numeric:NO::12,6",
            ]);

        SqlProbe.QueryStrings(
                Database,
                "SELECT conname FROM pg_constraint WHERE conrelid = 'generation_runs'::regclass AND contype = 'c' "
                + "ORDER BY conname")
            .ShouldBe(ExpectedChecks);

        // "episode_id -> CASCADE" (acceptance check 3); the run table never holds a fact_sheets FK yet.
        SqlProbe.QueryStrings(
                Database,
                "SELECT a.attname || '|' || c.confdeltype::text FROM pg_constraint c "
                + "JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = c.conkey[1] "
                + "WHERE c.conrelid = 'generation_runs'::regclass AND c.contype = 'f' ORDER BY 1")
            .ShouldBe(["episode_id|c", "result_story_version_id|n", "series_id|r", "user_id|r"]);

        var indexes = SqlProbe.QueryStrings(
            Database,
            "SELECT indexname || '|' || indexdef FROM pg_indexes WHERE schemaname = 'public' "
            + "AND tablename = 'generation_runs' ORDER BY indexname");

        indexes.ShouldContain(index => index.StartsWith("ux_generation_runs_active_episode|", StringComparison.Ordinal)
            && index.Contains("UNIQUE", StringComparison.Ordinal)
            && index.Contains("(episode_id, kind)", StringComparison.Ordinal)
            && index.Contains("episode_id IS NOT NULL", StringComparison.Ordinal));
        indexes.ShouldContain(index => index.StartsWith("ux_generation_runs_active_series|", StringComparison.Ordinal)
            && index.Contains("UNIQUE", StringComparison.Ordinal)
            && index.Contains("(series_id, kind)", StringComparison.Ordinal)
            && index.Contains("episode_id IS NULL", StringComparison.Ordinal));
        indexes.ShouldContain(index => index.StartsWith("ix_generation_runs_episode_kind_queued|", StringComparison.Ordinal)
            && index.Contains("queued_at DESC", StringComparison.Ordinal));
        indexes.ShouldContain(index => index.StartsWith("ix_generation_runs_open|", StringComparison.Ordinal)
            && index.Contains("INCLUDE (worst_case_usd)", StringComparison.Ordinal));
        indexes.ShouldContain(index => index.StartsWith("ix_generation_runs_sweep|", StringComparison.Ordinal)
            && index.Contains("COALESCE(started_at, queued_at)", StringComparison.Ordinal));
        indexes.ShouldContain(index => index.StartsWith("ix_generation_runs_user_id|", StringComparison.Ordinal));

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "an entity change without a migration must fail here (run `dotnet ef migrations add`)");
    }

    [Fact]
    public void Migration_AddsTheCr4ColumnsOfStoryVersions()
    {
        SqlProbe.QueryStrings(
                Database,
                "SELECT column_name || ':' || data_type || ':' || is_nullable || ':' || coalesce(column_default, '') "
                + "FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'story_versions' "
                + "AND column_name IN ('parent_version_id', 'generation_run_id', 'fact_sheet_id', 'schema_version') "
                + "ORDER BY column_name")
            .ShouldBe(
            [
                "fact_sheet_id:uuid:YES:",
                "generation_run_id:uuid:YES:",
                "parent_version_id:uuid:YES:",
                "schema_version:text:NO:'story.v1'::text",
            ]);

        SqlProbe.QueryStrings(
                Database,
                "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'story_versions' "
                + "AND indexname IN ('ux_story_versions_episode_version', 'ix_story_versions_generation_run_id') "
                + "ORDER BY indexname")
            .ShouldBe(["ix_story_versions_generation_run_id", "ux_story_versions_episode_version"]);
    }

    [Fact]
    public async Task ActiveRunIndex_RejectsASecondQueuedOrRunningRunOfOneEpisodeAndKind()
    {
        var parents = await ParentsAsync();
        InsertRun(parents, "story", "queued");

        SqlProbe.SqlState(Database, InsertRunSql(parents, "story", "queued"))
            .ShouldBe("23505", "one active run per (episode, kind) — the backstop of the episode row lock");
        SqlProbe.SqlState(Database, InsertRunSql(parents, "story", "running"))
            .ShouldBe("23505", "queued and running both count as active");

        // Other kinds and finished runs are not blocked.
        InsertRun(parents, "fact_sheet", "queued");
        InsertRun(parents, "story", "failed", errorCode: "story_generation_failed");
        InsertRun(parents, "story", "succeeded");
        InsertRun(parents, "story", "cancelled");
    }

    [Fact]
    public async Task ActiveRunIndex_AfterTheActiveRunFinishes_AllowsTheNextOne()
    {
        var parents = await ParentsAsync();
        var first = InsertRun(parents, "story", "running");

        SqlProbe.Execute(
            Database,
            $"UPDATE generation_runs SET status = 'succeeded', stage = 'done', finished_at = now() WHERE id = '{first}'");

        InsertRun(parents, "story", "queued");
    }

    [Fact]
    public async Task ActiveSeriesIndex_RejectsASecondSeriesLevelRunOfOneSeriesAndKind()
    {
        var parents = await ParentsAsync();
        InsertRun(parents, "ideas", "queued", episode: false);

        SqlProbe.SqlState(Database, InsertRunSql(parents, "ideas", "running", episode: false))
            .ShouldBe("23505");

        InsertRun(parents, "bible_proposal", "queued", episode: false);
        InsertRun(parents, "ideas", "cancelled", episode: false);
    }

    // ---- QA 02.2.2-T1: index scoping, paused runs, series RESTRICT ----

    [Fact]
    public async Task ActiveRunIndex_IsPerEpisode_AnotherEpisodeOfTheSameSeriesMayRunTheSameKind()
    {
        var parents = await ParentsAsync();
        var actor = new TestCurrentUser();
        var other = await new EpisodeBuilder(Database, actor).CreateAsync(parents.UserId, parents.SeriesId, ct: Ct);
        InsertRun(parents, "story", "queued");

        InsertRun(parents with { EpisodeId = other.Id }, "story", "queued");
        SqlProbe.SqlState(Database, InsertRunSql(parents with { EpisodeId = other.Id }, "story", "running"))
            .ShouldBe("23505", "the second episode is itself limited to one active run per kind");
    }

    [Fact]
    public async Task ActiveRunIndex_APausedQueuedRunStillHoldsTheIndex()
    {
        var parents = await ParentsAsync();
        InsertRun(parents, "story", "queued", pauseReason: "'spend_cap_reached'");

        SqlProbe.SqlState(Database, InsertRunSql(parents, "story", "queued"))
            .ShouldBe("23505", "TL T19: a paused run is still queued, so a new request is 409 in progress");
    }

    [Fact]
    public async Task ActiveSeriesIndex_IsPerSeries_AnotherSeriesOfTheUserMayRunTheSameKind()
    {
        var parents = await ParentsAsync();
        var actor = new TestCurrentUser();
        var otherSeries = await new SeriesBuilder(Database, actor).CreateAsync(parents.UserId, ct: Ct);
        InsertRun(parents, "ideas", "queued", episode: false);

        InsertRun(parents with { SeriesId = otherSeries.Id }, "ideas", "queued", episode: false);
    }

    [Fact]
    public async Task SeriesDelete_IsRestrictedWhileASeriesLevelRunReferencesIt()
    {
        var parents = await ParentsAsync();
        var actor = new TestCurrentUser();
        var emptySeries = await new SeriesBuilder(Database, actor).CreateAsync(parents.UserId, ct: Ct);
        var runId = InsertRun(parents with { SeriesId = emptySeries.Id }, "ideas", "queued", episode: false);

        SqlProbe.SqlState(Database, $"DELETE FROM series WHERE id = '{emptySeries.Id}'")
            .ShouldBe("23503", "fk_generation_runs_series is RESTRICT: 13.3.1 must delete series-level runs first");

        SqlProbe.Execute(Database, $"DELETE FROM generation_runs WHERE id = '{runId}'");
        SqlProbe.Execute(Database, $"DELETE FROM series WHERE id = '{emptySeries.Id}'");
    }

    [Theory]
    [InlineData("ideas", true)]
    [InlineData("bible_proposal", true)]
    [InlineData("story", false)]
    [InlineData("fact_sheet", false)]
    [InlineData("find_sources", false)]
    public async Task ScopeCheck_SeriesLevelKindsHaveNoEpisode_EveryOtherKindHasOne(string kind, bool seriesLevel)
    {
        var parents = await ParentsAsync();

        // The wrong shape for the kind is refused (TL T17) ...
        SqlProbe.SqlState(Database, InsertRunSql(parents, kind, "queued", episode: seriesLevel))
            .ShouldBe("23514", $"{kind} runs {(seriesLevel ? "must not" : "must")} carry an episode");

        // ... and the right one is accepted.
        InsertRun(parents, kind, "queued", episode: !seriesLevel);
    }

    [Fact]
    public async Task Checks_RejectValuesTheDomainWouldRefuse()
    {
        var parents = await ParentsAsync();

        SqlProbe.SqlState(Database, InsertRunSql(parents, "story", "failed"))
            .ShouldBe("23514", "ck_generation_runs_failed: a failed run needs an error_code");
        SqlProbe.SqlState(Database, InsertRunSql(parents, "story", "queued", worstCase: "-0.01"))
            .ShouldBe("23514", "ck_generation_runs_money");
        SqlProbe.SqlState(Database, InsertRunSql(parents, "story", "queued", pauseReason: "'bogus'"))
            .ShouldBe("23514", "ck_generation_runs_pause");
        SqlProbe.SqlState(Database, InsertRunSql(parents, "story", "queued", previous: "'published'"))
            .ShouldBe("23514", "ck_generation_runs_prev");
        SqlProbe.SqlState(
                Database,
                InsertRunSql(parents, "story", "succeeded", started: "now()", finished: "now() - interval '1 hour'"))
            .ShouldBe("23514", "ck_generation_runs_times");
        SqlProbe.SqlState(Database, InsertRunSql(parents, "bogus_kind", "queued"))
            .ShouldBe("23514", "ck_generation_runs_kind");
        SqlProbe.SqlState(Database, InsertRunSql(parents, "story", "queued", stage: "'bogus'"))
            .ShouldBe("23514", "ck_generation_runs_stage");

        // The two pause reasons of TL T19 are accepted.
        InsertRun(parents, "fact_sheet", "queued", pauseReason: "'spend_cap_reached'");
        InsertRun(parents, "story", "queued", pauseReason: "'provider_funds_exhausted'");
    }

    [Fact]
    public async Task GenerationRun_RoundTripsThroughTheContext_AndIsHiddenFromAnotherTenant()
    {
        var parents = await ParentsAsync();
        var run = GenerationRun.Queue(
                parents.UserId,
                parents.SeriesId,
                parents.EpisodeId,
                GenerationKind.Story,
                GenerationTrigger.Autopilot,
                "autopilot:key",
                EpisodeStatus.StoryReview,
                null,
                Guid.CreateVersion7(),
                0.375m,
                DateTimeOffset.UtcNow)
            .Value;
        run.Start(DateTimeOffset.UtcNow);
        run.AdvanceStage(GenerationStage.Writing, DateTimeOffset.UtcNow).IsError.ShouldBeFalse();
        run.Fail("story_moderated", "brief_flagged", countsTowardLimit: true, DateTimeOffset.UtcNow);

        await using (var insert = Database.CreateAppDbContext(new TestCurrentUser { Id = parents.UserId }))
        {
            insert.GenerationRuns.Add(run);
            await insert.SaveChangesAsync(Ct);
        }

        await using (var read = Database.CreateAppDbContext(new TestCurrentUser { Id = parents.UserId }))
        {
            var loaded = await read.GenerationRuns.SingleAsync(row => row.Id == run.Id, Ct);

            loaded.Kind.ShouldBe(GenerationKind.Story);
            loaded.Trigger.ShouldBe(GenerationTrigger.Autopilot);
            loaded.Status.ShouldBe(GenerationRunStatus.Failed);
            loaded.Stage.ShouldBe(GenerationStage.Writing);
            loaded.PreviousEpisodeStatus.ShouldBe(EpisodeStatus.StoryReview);
            loaded.CountsTowardLimit.ShouldBeTrue();
            loaded.WorstCaseUsd.ShouldBe(0.375m);
            loaded.ErrorCode.ShouldBe("story_moderated");
            loaded.ErrorDetail.ShouldBe("brief_flagged");
            loaded.IdempotencyKey.ShouldBe("autopilot:key");
            loaded.StartedAt.ShouldNotBeNull();
            loaded.FinishedAt.ShouldNotBeNull();
            loaded.CreatedAt.ShouldNotBe(default);
            loaded.UpdatedAt.ShouldNotBe(default);
        }

        SqlProbe.ScalarText(Database, $"SELECT kind || '/' || \"trigger\" || '/' || previous_episode_status FROM generation_runs WHERE id = '{run.Id}'")
            .ShouldBe("story/autopilot/story_review");

        // 09.2.3: a user-owned row is invisible to every other tenant, and to an anonymous scope.
        await using var other = Database.CreateAppDbContext(new TestCurrentUser { Id = Guid.CreateVersion7() });
        (await other.GenerationRuns.CountAsync(row => row.Id == run.Id, Ct)).ShouldBe(0);
        await using var anonymous = Database.CreateAppDbContext();
        (await anonymous.GenerationRuns.CountAsync(row => row.Id == run.Id, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task GenerationRun_FalseCountsTowardLimit_IsStoredNotReplacedByTheColumnDefault()
    {
        var parents = await ParentsAsync();
        var run = GenerationRun.Queue(
                parents.UserId,
                parents.SeriesId,
                parents.EpisodeId,
                GenerationKind.Story,
                GenerationTrigger.User,
                null,
                EpisodeStatus.Draft,
                null,
                null,
                0m,
                DateTimeOffset.UtcNow)
            .Value;
        run.MarkPaused(GenerationPauseReason.ProviderFundsExhausted, DateTimeOffset.UtcNow);

        await using (var insert = Database.CreateAppDbContext())
        {
            insert.GenerationRuns.Add(run);
            await insert.SaveChangesAsync(Ct);
        }

        SqlProbe.ScalarText(
                Database,
                $"SELECT counts_toward_limit::text || '/' || pause_reason FROM generation_runs WHERE id = '{run.Id}'")
            .ShouldBe("false/provider_funds_exhausted");
    }

    [Fact]
    public async Task GenerationRun_ResultAndProvenance_RoundTripThroughTheContext()
    {
        var parents = await ParentsAsync();
        var run = GenerationRun.Queue(
                parents.UserId,
                parents.SeriesId,
                parents.EpisodeId,
                GenerationKind.Story,
                GenerationTrigger.User,
                null,
                EpisodeStatus.Draft,
                null,
                null,
                0.5m,
                DateTimeOffset.UtcNow)
            .Value;
        run.Start(DateTimeOffset.UtcNow);
        run.RecordProvenance("p1+w1", "{\"writer\":\"model-a\",\"reviewer\":\"model-b\"}", DateTimeOffset.UtcNow);
        run.RecordAttempt(DateTimeOffset.UtcNow);

        await using (var insert = Database.CreateAppDbContext(new TestCurrentUser { Id = parents.UserId }))
        {
            insert.GenerationRuns.Add(run);
            await insert.SaveChangesAsync(Ct);
        }

        var versionId = InsertVersion(parents, 1, run.Id);

        await using (var complete = Database.CreateAppDbContext(new TestCurrentUser { Id = parents.UserId }))
        {
            var loadedRun = await complete.GenerationRuns.SingleAsync(row => row.Id == run.Id, Ct);
            loadedRun.Succeed(0.2m, versionId, null, DateTimeOffset.UtcNow);
            await complete.SaveChangesAsync(Ct);
        }

        await using var read = Database.CreateAppDbContext(new TestCurrentUser { Id = parents.UserId });
        var loaded = await read.GenerationRuns.SingleAsync(row => row.Id == run.Id, Ct);

        loaded.Status.ShouldBe(GenerationRunStatus.Succeeded);
        loaded.ResultStoryVersionId.ShouldBe(versionId);
        loaded.ResultFactSheetId.ShouldBeNull();
        loaded.PromptVersion.ShouldBe("p1+w1");
        loaded.Attempt.ShouldBe(1);
        loaded.CountsTowardLimit.ShouldBeTrue();
        SqlProbe.ScalarText(Database, $"SELECT models->>'reviewer' FROM generation_runs WHERE id = '{run.Id}'")
            .ShouldBe("model-b");
    }

    [Fact]
    public async Task EpisodeDelete_CascadesItsRuns_ButNotTheSeriesLevelOnes()
    {
        var parents = await ParentsAsync();
        var episodeRun = InsertRun(parents, "story", "queued");
        var seriesRun = InsertRun(parents, "ideas", "queued", episode: false);

        SqlProbe.Execute(Database, $"DELETE FROM episodes WHERE id = '{parents.EpisodeId}'");

        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE id = '{episodeRun}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM generation_runs WHERE id = '{seriesRun}'").ShouldBe(1);
    }

    [Fact]
    public async Task DeletingARun_ClearsOnlyTheRunColumnOfItsStoryVersion()
    {
        var parents = await ParentsAsync();
        var run = InsertRun(parents, "story", "succeeded");
        var version = InsertVersion(parents, 1, run);

        SqlProbe.Execute(Database, $"DELETE FROM generation_runs WHERE id = '{run}'");

        // The column-list SET NULL keeps user_id (a plain SET NULL would have failed on the NOT NULL column).
        SqlProbe.ScalarText(
                Database,
                $"SELECT coalesce(generation_run_id::text, 'null') || '|' || user_id::text FROM story_versions WHERE id = '{version}'")
            .ShouldBe($"null|{parents.UserId}");
    }

    [Fact]
    public async Task DeletingAVersion_ClearsOnlyTheResultAndParentReferences()
    {
        var parents = await ParentsAsync();
        var parent = InsertVersion(parents, 1, null);
        var child = InsertVersion(parents, 2, null, parent);
        var run = InsertRun(parents, "story", "succeeded");
        SqlProbe.Execute(
            Database,
            $"UPDATE generation_runs SET result_story_version_id = '{parent}' WHERE id = '{run}'");

        SqlProbe.Execute(Database, $"DELETE FROM story_versions WHERE id = '{parent}'");

        SqlProbe.ScalarText(Database, $"SELECT coalesce(parent_version_id::text, 'null') FROM story_versions WHERE id = '{child}'")
            .ShouldBe("null");
        SqlProbe.ScalarText(
                Database,
                $"SELECT coalesce(result_story_version_id::text, 'null') || '|' || user_id::text FROM generation_runs WHERE id = '{run}'")
            .ShouldBe($"null|{parents.UserId}");
    }

    [Fact]
    public async Task StoryVersion_SecondRowWithTheSameEpisodeAndVersion_IsRejected()
    {
        var parents = await ParentsAsync();
        InsertVersion(parents, 1, null);

        SqlProbe.SqlState(Database, InsertVersionSql(parents, 1, null, null))
            .ShouldBe("23505", "a lost max+1 race raises instead of writing a duplicate version");

        InsertVersion(parents, 2, null);
    }

    [Fact]
    public async Task StoryVersion_AnyPropertyChange_ThrowsOnSaveChanges()
    {
        var parents = await ParentsAsync();
        var version = StoryVersion.Create(
            parents.UserId,
            parents.EpisodeId,
            1,
            StoryVersionSource.Generated,
            Draft(),
            "{}",
            "model-x",
            "rel=1",
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow);

        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = parents.UserId });
        context.StoryVersions.Add(version);
        await context.SaveChangesAsync(Ct);

        var entry = context.Entry(version);
        entry.Property(nameof(StoryVersion.Title)).CurrentValue = "rewritten";

        await Should.ThrowAsync<InvalidOperationException>(() => context.SaveChangesAsync(Ct));

        SqlProbe.ScalarText(Database, $"SELECT title FROM story_versions WHERE id = '{version.Id}'")
            .ShouldBe("Tựa", "the row is untouched");
    }

    [Fact]
    public async Task StoryVersion_Model_EveryPropertyIsImmutable()
    {
        await using var context = Database.CreateAppDbContext();
        var entity = context.Model.FindEntityType(typeof(StoryVersion))!;

        foreach (var property in entity.GetProperties())
        {
            property.GetAfterSaveBehavior().ShouldBe(PropertySaveBehavior.Throw, property.Name);
            property.ValueGenerated.ShouldBe(ValueGenerated.Never, property.Name);
        }
    }

    [Fact]
    public async Task StoryVersion_CreatedByTheFactory_PersistsProvenanceAndDerivedFields()
    {
        var parents = await ParentsAsync();
        var run = InsertRun(parents, "story", "succeeded");
        var version = StoryVersion.Create(
            parents.UserId,
            parents.EpisodeId,
            1,
            StoryVersionSource.Generated,
            Draft(),
            "{\"schema_version\":\"story.v1\"}",
            "model-x",
            "rel=1",
            run,
            null,
            Guid.CreateVersion7(),
            "{\"v\":\"audit.v1\"}",
            0.5m,
            DateTimeOffset.UtcNow);

        await using (var insert = Database.CreateAppDbContext())
        {
            insert.StoryVersions.Add(version);
            await insert.SaveChangesAsync(Ct);
        }

        await using var read = Database.CreateAppDbContext(new TestCurrentUser { Id = parents.UserId });
        var loaded = await read.StoryVersions.SingleAsync(row => row.Id == version.Id, Ct);

        loaded.GenerationRunId.ShouldBe(run);
        loaded.FactSheetId.ShouldBe(version.FactSheetId);
        loaded.ParentVersionId.ShouldBeNull();
        loaded.SchemaVersion.ShouldBe("story.v1");
        loaded.Body.ShouldBe("Mở đầu.\n\nĐoạn một.\n\nKết.");
        loaded.Keywords.ShouldBe(["opening", "middle"]);
        loaded.WordCount.ShouldBe(version.WordCount);
        loaded.SimilarityScore.ShouldBe(0.5m);
    }

    [Fact]
    public async Task LockEpisodeForUpdateAsync_HoldsTheRowLockUntilTheTransactionEnds()
    {
        var parents = await ParentsAsync();
        var lockStatement = $"SELECT id FROM episodes WHERE id = '{parents.EpisodeId}' FOR UPDATE NOWAIT";

        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = parents.UserId });
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);

        await context.LockEpisodeForUpdateAsync(parents.EpisodeId, Ct);

        SqlProbe.SqlState(Database, lockStatement)
            .ShouldBe("55P03", "a second generate call must wait for (here: fail on) the episode row");

        await transaction.CommitAsync(Ct);

        SqlProbe.Execute(Database, lockStatement);
    }

    [Fact]
    public async Task LockEpisodeForUpdateAsync_ForAnotherUsersOrAnAnonymousScope_LocksNothing()
    {
        var parents = await ParentsAsync();
        var lockStatement = $"SELECT id FROM episodes WHERE id = '{parents.EpisodeId}' FOR UPDATE NOWAIT";

        await using var other = Database.CreateAppDbContext(new TestCurrentUser { Id = Guid.CreateVersion7() });
        await using var otherTransaction = await other.Database.BeginTransactionAsync(Ct);
        await other.LockEpisodeForUpdateAsync(parents.EpisodeId, Ct);

        await using var anonymous = Database.CreateAppDbContext();
        await using var anonymousTransaction = await anonymous.Database.BeginTransactionAsync(Ct);
        await anonymous.LockEpisodeForUpdateAsync(parents.EpisodeId, Ct);

        // The owner's own lock is still free on a second connection (it would be 55P03 had B or anonymous locked it).
        SqlProbe.Execute(Database, lockStatement);
    }

    /// <summary>
    /// The <c>Down</c> check: reverting to the migration before this one drops the table and the four columns, and
    /// applying the latest again leaves an empty table. It finds its migration by name, so it does not depend on
    /// being the last one (the newest migration's own test owns that check, 05.3.2-T3).
    /// </summary>
    [Fact]
    public async Task DownMigration_DropsTheTableAndTheFourColumns_AndUpRecreatesThem()
    {
        var parents = await ParentsAsync();
        var run = InsertRun(parents, "story", "succeeded");
        InsertVersion(parents, 1, run);
        var versionsBefore = SqlProbe.Count(Database, "SELECT count(*) FROM story_versions");

        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        var own = migrations.FindIndex(name => name.EndsWith("_02_GenerationRuns", StringComparison.Ordinal));
        own.ShouldBeGreaterThan(0, "the migration must exist and have a previous one to revert to");

        try
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(migrations[own - 1], Ct);

            SqlProbe.ScalarText(Database, "SELECT coalesce(to_regclass('public.generation_runs')::text, 'gone')")
                .ShouldBe("gone");
            SqlProbe.Count(
                    Database,
                    "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' "
                    + "AND table_name = 'story_versions' AND column_name IN "
                    + "('parent_version_id', 'generation_run_id', 'fact_sheet_id', 'schema_version')")
                .ShouldBe(0);
            SqlProbe.Count(Database, "SELECT count(*) FROM story_versions")
                .ShouldBe(versionsBefore, "Down keeps every version row");
        }
        finally
        {
            // Always come back to the latest migration so a failed assertion cannot strand the class database.
            await context.Database.MigrateAsync(CancellationToken.None);
        }

        SqlProbe.Count(Database, "SELECT count(*) FROM generation_runs").ShouldBe(0);
        SqlProbe.ScalarText(Database, "SELECT schema_version FROM story_versions LIMIT 1").ShouldBe("story.v1");
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    private async Task<Parents> ParentsAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);

        return new Parents(user.Id, series.Id, episode.Id);
    }

    private Guid InsertRun(
        Parents parents,
        string kind,
        string status,
        bool episode = true,
        string? errorCode = null,
        string? pauseReason = null)
    {
        var id = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            InsertRunSql(
                parents,
                kind,
                status,
                episode,
                errorCode: errorCode,
                pauseReason: pauseReason,
                id: id));
        return id;
    }

    private static string InsertRunSql(
        Parents parents,
        string kind,
        string status,
        bool episode = true,
        string? errorCode = null,
        string worstCase = "0.25",
        string? pauseReason = null,
        string? previous = null,
        string? started = null,
        string? finished = null,
        string stage = "'queued'",
        Guid? id = null) =>
        "INSERT INTO generation_runs (id, user_id, series_id, episode_id, kind, \"trigger\", status, stage, "
        + "previous_episode_status, worst_case_usd, error_code, pause_reason, queued_at, started_at, finished_at, "
        + "created_at, updated_at) VALUES ("
        + $"'{id ?? Guid.CreateVersion7()}', '{parents.UserId}', '{parents.SeriesId}', "
        + (episode ? $"'{parents.EpisodeId}'" : "NULL")
        + $", '{kind}', 'user', '{status}', {stage}, {previous ?? "NULL"}, {worstCase}, "
        + (errorCode is null ? "NULL" : $"'{errorCode}'")
        + $", {pauseReason ?? "NULL"}, now(), {started ?? "NULL"}, {finished ?? "NULL"}, now(), now())";

    private Guid InsertVersion(Parents parents, int version, Guid? runId, Guid? parentId = null)
    {
        var id = Guid.CreateVersion7();
        SqlProbe.Execute(Database, InsertVersionSql(parents, version, runId, parentId, id));
        return id;
    }

    private static string InsertVersionSql(Parents parents, int version, Guid? runId, Guid? parentId, Guid? id = null) =>
        "INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, word_count, "
        + "content, generation_run_id, parent_version_id, created_at) VALUES ("
        + $"'{id ?? Guid.CreateVersion7()}', '{parents.UserId}', '{parents.EpisodeId}', {version}, 'generated', 't', "
        + "'b', '{}', 1, '{}'::jsonb, "
        + (runId is null ? "NULL" : $"'{runId}'")
        + ", "
        + (parentId is null ? "NULL" : $"'{parentId}'")
        + ", now())";

    private static StoryContentV1 Draft() =>
        new(
            "Tựa",
            new StorySegmentV1("Mở đầu.", ["opening"], []),
            [new StorySegmentV1("Đoạn một.", ["middle"], [], "setup")],
            new StorySegmentV1("Kết.", [], []),
            "Mô tả",
            ["tag"],
            [],
            new StoryStructureV1("sub", "setting", "opening", "twist", "first_person"));

    private sealed record Parents(Guid UserId, Guid SeriesId, Guid EpisodeId);
}
