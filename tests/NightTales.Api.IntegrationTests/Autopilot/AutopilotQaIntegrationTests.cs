using System.Globalization;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Domain.Series.Autopilot;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Autopilot;

/// <summary>
/// The independent QA pass over 02.8.3-T1's integration surface: cross-tenant isolation of one orchestrator call,
/// the plan §3.6 / §3.5 wire values the two CHECKs must accept (and one they must refuse), and D19's "ids and codes
/// only" rule on the lines this feature writes.
/// </summary>
/// <remarks>
/// Written by a reviewer who did not write the product code. The rows go through
/// <see cref="AutopilotTickHarness"/> and the same raw-SQL escape hatches the migration tests use, the clock is
/// <see cref="FakeClock"/>, and no provider is called.
/// </remarks>
[Collection("Process-global host state")]
public sealed class AutopilotQaIntegrationTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>Tuesday 20/10/2026 10:00 in Ho Chi Minh City (UTC+7, no DST).</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);

    /// <summary>Plan §3.6's sixteen reason codes, in table order (row 6b's wire value is the documented exception).</summary>
    private static readonly string[] PauseReasonCodes =
    [
        "gate_failed_streak",
        "spend_cap_reached",
        "run_cost_cap_reached",
        "provider_funds_exhausted",
        "channel_not_active",
        "autopilot_channel_unsupported",
        "oauth_client_incident",
        "youtube_quota_exhausted",
        "upload_failed",
        "video_rejected",
        "veto_notice_undelivered",
        "repeated_run_failures",
        "slot_missed",
        "pre_audit_private_limit",
        "content_reported",
        "owner_paused",
    ];

    /// <summary>Plan §3.5's eighteen state wire values, in the enum's declaration order.</summary>
    private static readonly string[] StateCodes =
    [
        "planned",
        "awaiting_authorization",
        "idea_picked",
        "researching",
        "story_generating",
        "story_gate",
        "waiting_owner",
        "rendering",
        "video_gate",
        "publication_created",
        "veto_window",
        "scheduled",
        "published",
        "published_private_locked",
        "gate_failed",
        "cancelled_by_owner",
        "paused",
        "failed",
    ];

    [Fact]
    public async Task Qa_Advance_CrossTenantCall_IsNotFoundAndLeavesTheRowUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await AutopilotTickHarness.StartAsync(Database, new FakeClock(Start), ct: ct);

        var owner = await harness.CreateOwnerAsync(ct);
        var stranger = await harness.CreateOwnerAsync(ct);
        var settings = AutopilotTickHarness.Settings(daysOfWeek: [DayOfWeek.Thursday]);
        var world = await harness.CreateAuthorizedSeriesAsync(owner, settings, ct: ct);

        var run = await harness.CreateRunAsync(
            owner,
            world.SeriesId,
            slotAt: new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero),
            startAt: Start.AddMinutes(-1),
            authorizationId: world.AuthorizationId,
            state: AutopilotRunState.Planned,
            ct: ct);

        // The stranger names the run id but not its owner: 09.2.3's tenant filter is on inside the orchestrator,
        // so the row is invisible and the call answers "not found" instead of advancing another tenant's run.
        var attempt = await harness.AdvanceAsync(run.Id, stranger, ct);

        attempt.IsError.ShouldBeTrue();
        attempt.FirstError.Code.ShouldBe(ErrorCodes.AutopilotRunNotFound);

        var unchanged = await harness.RunAsync(owner, run.Id, ct);

        unchanged.State.ShouldBe(AutopilotRunState.Planned);
        unchanged.Step.ShouldBeNull();
        unchanged.LastTransitionAt.ShouldBe(run.LastTransitionAt);
        (await harness.CountGenerationRunsAsync(world.SeriesId, ct)).ShouldBe(
            0,
            "a stranger's tick must not spend the owner's budget");
    }

    [Fact]
    public async Task Qa_Schema_AcceptsEveryPlanSection36PauseReason_AndRefusesAnUnknownOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await AutopilotTickHarness.StartAsync(Database, new FakeClock(Start), ct: ct);

        var owner = await harness.CreateOwnerAsync(ct);
        var world = await harness.CreateAuthorizedSeriesAsync(owner, AutopilotTickHarness.Settings(), ct: ct);

        for (var index = 0; index < PauseReasonCodes.Length; index++)
        {
            var sql = InsertRunSql(owner, world.SeriesId, Start.AddDays(index + 1), state: "paused", pauseReason: PauseReasonCodes[index]);

            Should.NotThrow(
                () => SqlProbe.Execute(Database, sql),
                $"plan §3.6's code '{PauseReasonCodes[index]}' must be accepted by ck_autopilot_runs_pause_reason");
        }

        var unknown = InsertRunSql(
            owner,
            world.SeriesId,
            Start.AddDays(30),
            state: "paused",
            pauseReason: "not_a_plan_reason");

        SqlProbe.SqlState(Database, unknown).ShouldBe(
            "23514",
            "the CHECK must refuse a reason plan §3.6 does not name (check_violation)");
    }

    [Fact]
    public async Task Qa_Schema_AcceptsEveryPlanSection35State()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await AutopilotTickHarness.StartAsync(Database, new FakeClock(Start), ct: ct);

        var owner = await harness.CreateOwnerAsync(ct);
        var world = await harness.CreateAuthorizedSeriesAsync(owner, AutopilotTickHarness.Settings(), ct: ct);

        for (var index = 0; index < StateCodes.Length; index++)
        {
            var sql = InsertRunSql(owner, world.SeriesId, Start.AddDays(index + 1), StateCodes[index], pauseReason: null);

            Should.NotThrow(
                () => SqlProbe.Execute(Database, sql),
                $"plan §3.5's state '{StateCodes[index]}' must be accepted by ck_autopilot_runs_state");
        }
    }

    [Fact]
    public async Task Qa_Logs_CarryIdsAndCodesOnly_NotThePickedIdeaText()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await AutopilotTickHarness.StartAsync(Database, new FakeClock(Start), ct: ct);

        var owner = await harness.CreateOwnerAsync(ct);
        var settings = AutopilotTickHarness.Settings(daysOfWeek: [DayOfWeek.Thursday]);
        var world = await harness.CreateAuthorizedSeriesAsync(owner, settings, ct: ct);

        var run = await harness.CreateRunAsync(
            owner,
            world.SeriesId,
            slotAt: new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero),
            startAt: Start.AddMinutes(-1),
            authorizationId: world.AuthorizationId,
            state: AutopilotRunState.Planned,
            ct: ct);

        var advanced = await harness.AdvanceAsync(run.Id, owner, ct);

        advanced.IsError.ShouldBeFalse(string.Join("; ", advanced.Errors.Select(error => $"{error.Code}:{error.Description}")));
        (await harness.RunAsync(owner, run.Id, ct)).State.ShouldBe(AutopilotRunState.IdeaPicked);

        var autopilotLines = harness.Logs.Events
            .Select(logEvent => logEvent.RenderMessage(CultureInfo.InvariantCulture)
                + " | " + string.Join(' ', logEvent.Properties.Select(property => property.Key + "=" + property.Value)))
            .Where(line => line.Contains("autopilot", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        autopilotLines.ShouldNotBeEmpty("the tick and the idea step log their progress");

        foreach (var line in autopilotLines)
        {
            // D19: the picked idea's text is content, never a log field — only ids, step names, codes, attempts,
            // durations and token counts may appear.
            line.ShouldNotContain(harness.Ideas.Title);
            line.ShouldNotContain(harness.Ideas.Logline);
            line.ShouldNotContain(harness.Ideas.Hook);
            line.ShouldNotContain(harness.Ideas.Setting);
        }
    }

    [Fact]
    public async Task Qa_StoryPlatformFailure_AttemptOne_RegeneratesThroughTheStory2KeyOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock(Start);
        await using var harness = await AutopilotTickHarness.StartAsync(Database, clock, ct: ct);

        var owner = await harness.CreateOwnerAsync(ct);
        var world = await harness.CreateAuthorizedSeriesAsync(
            owner,
            AutopilotTickHarness.Settings(daysOfWeek: [DayOfWeek.Tuesday]),
            ct: ct);

        var run = await StartStoryAsync(harness, owner, world, clock, ct);

        // Attempt 1 failed on the platform: the episode is back in draft and its row carries the code.
        await FailStoryGenerationAsync(harness, run.EpisodeId!.Value, "story_generation_failed", clock, ct);

        var spent = await harness.AdvanceAsync(run.Id, owner, ct);

        spent.IsError.ShouldBeFalse();
        spent.Value.Kind.ShouldBe(StepResultKind.Advanced);

        var attemptTwo = await harness.RunAsync(owner, run.Id, ct);
        attemptTwo.Attempt.ShouldBe(2, "plan §3.5: the platform failure spends the one regenerate");
        attemptTwo.State.ShouldBe(AutopilotRunState.StoryGenerating);
        attemptTwo.Step.ShouldBe(AutopilotRunStep.Story, "the attempt-1 step keeps its name; only the dispatch moves to story2");

        var dispatched = await harness.AdvanceAsync(run.Id, owner, ct);

        dispatched.Value.Kind.ShouldBe(StepResultKind.Advanced);
        (await harness.RunAsync(owner, run.Id, ct)).Step.ShouldBe(AutopilotRunStep.Story2);

        var storyKeys = (await harness.GenerationRunKeysAsync(world.SeriesId, ct))
            .Where(key => key.EndsWith(":story", StringComparison.Ordinal)
                || key.EndsWith(":story2", StringComparison.Ordinal))
            .ToList();

        storyKeys.Count.ShouldBe(2, "one story per attempt and no third one: " + string.Join(" | ", storyKeys));
        storyKeys.ShouldContain(key => key.EndsWith(":story", StringComparison.Ordinal));
        storyKeys.ShouldContain(key => key.EndsWith(":story2", StringComparison.Ordinal));

        // The regeneration fails too: attempt 2 is spent, so the run ends `failed` and nothing is dispatched again.
        await FailStoryGenerationAsync(harness, run.EpisodeId!.Value, "story_generation_failed", clock, ct);

        var ended = await harness.AdvanceAsync(run.Id, owner, ct);

        ended.Value.Kind.ShouldBe(StepResultKind.Stopped);
        (await harness.RunAsync(owner, run.Id, ct)).State.ShouldBe(AutopilotRunState.Failed);

        (await harness.GenerationRunKeysAsync(world.SeriesId, ct))
            .Count(key => key.EndsWith(":story2", StringComparison.Ordinal))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Qa_StoryModeratedAtAttemptOne_EndsTheRunWithoutASecondGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock(Start);
        await using var harness = await AutopilotTickHarness.StartAsync(Database, clock, ct: ct);

        var owner = await harness.CreateOwnerAsync(ct);
        var world = await harness.CreateAuthorizedSeriesAsync(
            owner,
            AutopilotTickHarness.Settings(daysOfWeek: [DayOfWeek.Tuesday]),
            ct: ct);

        var run = await StartStoryAsync(harness, owner, world, clock, ct);

        // Plan §3.5 line 209: `story_moderated` ends the run whatever the attempt; moderation is never retried,
        // because the second attempt would buy another flag.
        await FailStoryGenerationAsync(harness, run.EpisodeId!.Value, "story_moderated", clock, ct);

        var ended = await harness.AdvanceAsync(run.Id, owner, ct);

        ended.IsError.ShouldBeFalse();
        ended.Value.Kind.ShouldBe(StepResultKind.Stopped);

        var failed = await harness.RunAsync(owner, run.Id, ct);
        failed.State.ShouldBe(AutopilotRunState.Failed, "a moderated story is not regenerated");

        (await harness.GenerationRunKeysAsync(world.SeriesId, ct))
            .Count(key => key.EndsWith(":story2", StringComparison.Ordinal))
            .ShouldBe(0, "no second paid generation after moderation");
    }

    /// <summary>Plans one run, then advances it to <c>story_generating</c> with attempt 1 dispatched.</summary>
    private static async Task<AutopilotRun> StartStoryAsync(
        AutopilotTickHarness harness,
        Guid owner,
        SeededSeries world,
        FakeClock clock,
        CancellationToken ct)
    {
        var run = await harness.CreateRunAsync(
            owner,
            world.SeriesId,
            slotAt: new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero),
            startAt: Start.AddMinutes(-1),
            authorizationId: world.AuthorizationId,
            state: AutopilotRunState.Planned,
            ct: ct);

        clock.Set(run.StartAt);

        (await harness.AdvanceAsync(run.Id, owner, ct)).Value.Kind.ShouldBe(StepResultKind.Advanced);
        var generating = await harness.AdvanceAsync(run.Id, owner, ct);
        generating.IsError.ShouldBeFalse();

        var story = await harness.RunAsync(owner, run.Id, ct);
        story.State.ShouldBe(AutopilotRunState.StoryGenerating);
        story.EpisodeId.ShouldNotBeNull();

        return story;
    }

    /// <summary>Reports a failed attempt the way 02.2.2's pipeline does: the row fails, the episode returns to draft.</summary>
    private static async Task FailStoryGenerationAsync(
        AutopilotTickHarness harness,
        Guid episodeId,
        string code,
        FakeClock clock,
        CancellationToken ct)
    {
        var now = clock.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        await harness.Database.ExecuteAsync(
            $"UPDATE generation_runs SET status = 'failed', error_code = '{code}', finished_at = '{now}' "
            + $"WHERE episode_id = '{episodeId}' AND status <> 'failed'",
            ct);

        await harness.Database.ExecuteAsync(
            $"UPDATE episodes SET status = 'draft', updated_at = '{now}' WHERE id = '{episodeId}'",
            ct);
    }

    /// <summary>One raw <c>autopilot_runs</c> insert, for the CHECK cases the aggregate cannot reach in one call.</summary>
    private static string InsertRunSql(
        Guid owner,
        Guid seriesId,
        DateTimeOffset slotAt,
        string state,
        string? pauseReason) =>
        "INSERT INTO autopilot_runs (id, user_id, series_id, slot_at, start_at, started_at, state, pause_reason, "
        + "last_transition_at, created_at, updated_at) VALUES ("
        + $"'{Guid.CreateVersion7()}', '{owner}', '{seriesId}', "
        + $"'{slotAt.ToString("O", CultureInfo.InvariantCulture)}', "
        + $"'{slotAt.AddDays(-2).ToString("O", CultureInfo.InvariantCulture)}', "
        + $"'{Start.ToString("O", CultureInfo.InvariantCulture)}', "
        + $"'{state}', {(pauseReason is null ? "NULL" : $"'{pauseReason}'")}, "
        + $"'{Start.ToString("O", CultureInfo.InvariantCulture)}', "
        + $"'{Start.ToString("O", CultureInfo.InvariantCulture)}', "
        + $"'{Start.ToString("O", CultureInfo.InvariantCulture)}')";
}
