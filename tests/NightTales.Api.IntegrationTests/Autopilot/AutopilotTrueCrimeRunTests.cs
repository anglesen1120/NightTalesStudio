using System.Text.Json.Nodes;
using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Flags;
using NightTales.Application.Common;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
using NightTales.Application.Features.Research.Commands.ApproveFactSheet;
using NightTales.Application.Features.Research.SourceFinder;
using NightTales.Application.Features.Research.Sources;
using NightTales.Application.Features.Research.Sources.Commands.AddSource;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Gates;
using NightTales.Domain.Stories.Generation;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Research;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Autopilot;

/// <summary>
/// The true-crime path of 02.8.3-T1 §Tests (plan §3.5 rows <c>idea_picked → researching</c>,
/// <c>researching → waiting_owner</c>, and F33 in plan §7.3): a true-crime run never dispatches a story before the
/// owner approves its fact sheet, waits for pasted sources when the finder has no go, and continues through both
/// owner approvals to its publication.
/// </summary>
/// <remarks>
/// <para>
/// <b>Real steps, two scripted seams.</b> Every case drives the production <c>AutopilotPlanJob</c> world: the tick
/// harness's <see cref="AutopilotTickHarness.AdvanceAsync"/> runs the real <c>AutopilotRunOrchestrator</c> over the
/// real <see cref="AutopilotTickHarness"/> step handlers, in a scope pinned to the run's owner, so the fact-sheet
/// and finder commands, the episode rows and the outbox rows are the production ones. Only two things are
/// scripted: the idea planner (harness default, it persists a real idea row) and — for the go branch — the
/// <see cref="ISourceFinderGo"/> port, because 02.7.2-T3's finder runner does not exist yet and the shipped
/// <c>SourceFinderGo</c> therefore answers the F33 "no go" for every run.
/// </para>
/// <para>
/// <b>What a step cannot arrange is written as the row its own owner writes.</b> The fact-sheet pipeline, the
/// story generation, the render and the video approval are other tasks' paid/delivery paths; where the run's next
/// move depends on their result, the test writes that result as the row the step reads (a draft sheet via
/// <see cref="FactSheetBuilder"/>, the episode's own status), never as a second implementation of the step. Each
/// such write is named at the call site.
/// </para>
/// <para>
/// <b>No owner-action notice is expected here.</b> The <c>autopilot_owner_action_required</c> row is raised only by
/// a gate stop (<see cref="AutopilotRun.RaiseOwnerActionRequired"/>, 02.8.2's one-stop-one-notice rule); the two
/// research waits are expressed by the run row's <c>waiting_for</c> column, so the cases assert the absence of a
/// notice for their own owner (<c>outbox_messages.type = 'autopilot_owner_action_required.v1'</c>).
/// </para>
/// <para>
/// Metrics are process-global, so this class joins the serial collection like the plan-job and orchestrator cases.
/// </para>
/// </remarks>
/// <param name="database">The class's own migrated database, from <see cref="IntegrationTest"/>.</param>
[Collection("Process-global host state")]
public sealed class AutopilotTrueCrimeRunTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The owner-action notice type the gate stops queue (02.8.2 plan §4 UC6).</summary>
    private const string NoticeType = "autopilot_owner_action_required.v1";

    /// <summary>Tuesday 20/10/2026 10:00 in Ho Chi Minh City (UTC+7, no DST).</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);

    /// <summary>Thursday 22/10/2026 19:00 in Ho Chi Minh City: the slot of every seeded run.</summary>
    private static readonly DateTimeOffset Slot = Start.AddHours(36);

    /// <summary>The slot's buffer start, already in the past, so the tick advances the run in the same call.</summary>
    private static readonly DateTimeOffset RunStart = Start.AddHours(-12);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TrueCrimeRun_StopsAtFactSheetWaitingOwner()
    {
        // No go is the shipped state (02.7.2-T3's runner does not exist, AL-S47-2), so the run stops for the
        // owner's sources first; with a source and a drafted sheet it stops again, this time for the approval.
        await using var harness = await StartAsync(finderGo: false);
        var owner = await harness.CreateOwnerAsync(Ct);
        var world = await harness.CreateAuthorizedSeriesAsync(owner, topic: Topic.TrueCrime, ct: Ct);
        var run = await ResearchingRunAsync(harness, owner, world.SeriesId, world.AuthorizationId);
        var episodeId = run.EpisodeId!.Value;

        var stopping = await harness.AdvanceAsync(run.Id, owner, Ct);
        stopping.IsError.ShouldBeFalse();
        stopping.Value.Code.ShouldBe(AutopilotRunStep.SourcesRequired);

        await AddOwnerNoteAsync(harness, owner, episodeId);
        (await harness.AdvanceAsync(run.Id, owner, Ct)).Value.Code.ShouldBe(AutopilotRunStep.FactSheet);

        // The 02.5.3 pipeline drafted the sheet: the run stops for the owner's approval instead of approving it.
        await new FactSheetBuilder(Database, AsUser(owner), harness.Clock).CreateAsync(owner, episodeId, Ct);

        // The stop itself is the run row below; this tick is either the stop or the idle repeat of an already
        // parked wait (the sheet's own event is what would move it).
        var waiting = await harness.AdvanceAsync(run.Id, owner, Ct);
        waiting.IsError.ShouldBeFalse();
        waiting.Value.Kind.ShouldBeOneOf(StepResultKind.Stopped, StepResultKind.Idle);

        var parked = await harness.RunAsync(owner, run.Id, Ct);
        parked.State.ShouldBe(AutopilotRunState.WaitingOwner);
        parked.WaitingFor.ShouldBe(WaitingFor.FactSheet, "the wait must name the fact-sheet approval");
        parked.Step.ShouldBe(
            AutopilotRunStep.SourcesRequired,
            "the relabel writes `waiting_for` only; `step` stays where the wait started");
        parked.Attempt.ShouldBe(0, "true crime spends no story attempt before the sheet is approved");

        // No story generation was dispatched: no `…:story` key, and the sheet is still the draft it waits on.
        var parkedKeys = await harness.GenerationRunKeysAsync(world.SeriesId, Ct);
        parkedKeys.ShouldAllBe(key => !key.EndsWith($":{AutopilotRunStep.Story}", StringComparison.Ordinal));
        (await SheetStatusAsync(owner, episodeId))
            .ShouldBe(FactSheetStatus.Draft, "true crime never auto-approves a fact sheet (F33)");

        // The research waits raise no owner-action notice of their own: the gate owns that row (02.8.2).
        NoticeRows(owner).ShouldBe(0);
    }

    [Fact]
    public async Task TrueCrimeRun_NoFinderGo_WaitsForSources()
    {
        // No `source_finder` flag: the shipped ISourceFinderGo answers "no go", which is F33's default.
        await using var harness = await StartAsync(finderGo: false);
        var owner = await harness.CreateOwnerAsync(Ct);
        var world = await harness.CreateAuthorizedSeriesAsync(owner, topic: Topic.TrueCrime, ct: Ct);
        var run = await ResearchingRunAsync(harness, owner, world.SeriesId, world.AuthorizationId);
        var episodeId = run.EpisodeId!.Value;

        var stopped = await harness.AdvanceAsync(run.Id, owner, Ct);
        stopped.IsError.ShouldBeFalse();
        stopped.Value.Kind.ShouldBe(StepResultKind.Stopped);
        stopped.Value.Code.ShouldBe(AutopilotRunStep.SourcesRequired);

        var waiting = await harness.RunAsync(owner, run.Id, Ct);
        waiting.State.ShouldBe(AutopilotRunState.WaitingOwner);
        waiting.WaitingFor.ShouldBe(WaitingFor.Sources, "the owner is asked to paste the links");
        waiting.Step.ShouldBe(AutopilotRunStep.SourcesRequired);
        var enteredAt = waiting.LastTransitionAt;

        // No finder call was attempted: the only paid row is the slot's idea.
        var keys = await harness.GenerationRunKeysAsync(world.SeriesId, Ct);
        keys.Count.ShouldBe(1);
        keys.ShouldAllBe(key => !key.EndsWith($":{AutopilotRunStep.FindSources}", StringComparison.Ordinal));
        (await harness.CountGenerationRunsAsync(world.SeriesId, Ct)).ShouldBe(1);
        NoticeRows(owner).ShouldBe(0);

        // The owner adds a source through the route's own command (POST /episodes/{id}/sources): a note is
        // `fetched` at once, so it is the readable source the next tick drafts from.
        await AddOwnerNoteAsync(harness, owner, episodeId);

        var drafted = await harness.AdvanceAsync(run.Id, owner, Ct);
        drafted.IsError.ShouldBeFalse();
        drafted.Value.Kind.ShouldBe(StepResultKind.Advanced);
        drafted.Value.Code.ShouldBe(AutopilotRunStep.FactSheet);

        var relabelled = await harness.RunAsync(owner, run.Id, Ct);
        relabelled.State.ShouldBe(AutopilotRunState.WaitingOwner, "the §3.5 table has no `waiting_owner → researching` row");
        relabelled.WaitingFor.ShouldBe(WaitingFor.FactSheet, "the wait is relabelled to the sheet it now waits on");
        relabelled.Step.ShouldBe(
            AutopilotRunStep.SourcesRequired,
            "the relabel writes `waiting_for` only; `step` stays where the wait started");
        relabelled.LastTransitionAt.ShouldBe(enteredAt, "relabelling the wait is not a transition");
        relabelled.Attempt.ShouldBe(0);

        var draftedKeys = await harness.GenerationRunKeysAsync(world.SeriesId, Ct);
        draftedKeys.ShouldContain(key => key.EndsWith($":{AutopilotRunStep.FactSheet}", StringComparison.Ordinal));
        draftedKeys.ShouldAllBe(key => !key.EndsWith($":{AutopilotRunStep.Story}", StringComparison.Ordinal));
        (await harness.CountGenerationRunsAsync(world.SeriesId, Ct)).ShouldBe(2, "the idea and the sheet, never a source search");
    }

    /// <summary>
    /// Starts the tick harness at <see cref="Start"/>; <paramref name="finderGo"/> replaces the finder port with a
    /// scripted go, which is the branch 02.7.2-T3 would enable.
    /// </summary>
    /// <remarks>
    /// The research commands the steps send (<c>FindSourcesCommand</c>, and the owner's own <c>AddSourceCommand</c>)
    /// need <see cref="IPublicSuffixList"/>, which the Worker's composition root does not register — only the Api's
    /// sources composition and the fetcher do, because the Worker never serves the add-source route. The test wires
    /// the TestKit's suffix list in, so the commands run unmodified.
    /// </remarks>
    /// <param name="finderGo">Whether the finder port answers a go.</param>
    /// <param name="approveVideo">
    /// Whether the harness's gate is replaced by <see cref="PassingTrueCrimeGate"/>: the story verdict passes (and the
    /// run still holds for the owner, because a true-crime story is never auto-approved) and the video verdict
    /// passes, so the run can reach its publication. The harness's own gate scripts no video answer.
    /// </param>
    private async Task<AutopilotTickHarness> StartAsync(bool finderGo, bool approveVideo = false)
    {
        if (finderGo)
        {
            await EnableSourceFinderFlagAsync();
        }

        return await AutopilotTickHarness.StartAsync(
            Database,
            new FakeClock(Start),
            configureServices: services =>
            {
                services.TryAddSingleton<IPublicSuffixList>(_ => new FakePublicSuffixList());
                services.TryAddSingleton(new DomainBlocklist(new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
                services.TryAddSingleton(new SourcesOptions());
                services.TryAddSingleton<IResearchAllowList>(_ => new PermissiveResearchAllowList());

                // The go itself is scripted in both directions: the shipped port reads a process-wide flag cache,
                // so a test cannot reliably switch it per case (and 02.7.2-T3's runner does not exist yet).
                services.RemoveAll<ISourceFinderGo>();
                services.AddSingleton<ISourceFinderGo>(new FixedSourceFinderGo(hasGo: finderGo));

                // The handler behind the go checks the flag itself (fail-closed), and the flag port caches its
                // answer process-wide, so the switch is scripted here instead of written into a shared cache.
                services.RemoveAll<IFeatureFlags>();
                services.AddSingleton<IFeatureFlags>(new ScriptedFeatureFlags(finderGo));

                if (approveVideo)
                {
                    services.RemoveAll<IAutoApprovalGate>();
                    services.AddSingleton<IAutoApprovalGate>(new PassingTrueCrimeGate());
                }
            },
            ct: Ct);
    }

    /// <summary>
    /// Seeds one run of the series and drives the two steps before research: the idea pick and the episode, whose
    /// true-crime branch leaves the run in <c>researching</c> with <c>idea</c> as its last completed step.
    /// </summary>
    private static async Task<AutopilotRun> ResearchingRunAsync(
        AutopilotTickHarness harness,
        Guid owner,
        Guid seriesId,
        Guid? authorizationId)
    {
        var run = await harness.CreateRunAsync(
            owner,
            seriesId,
            slotAt: Slot,
            startAt: RunStart,
            authorizationId: authorizationId,
            ct: Ct);

        var picked = await harness.AdvanceAsync(run.Id, owner, Ct);
        picked.IsError.ShouldBeFalse();
        picked.Value.Kind.ShouldBe(StepResultKind.Advanced);

        var created = await harness.AdvanceAsync(run.Id, owner, Ct);
        created.IsError.ShouldBeFalse();
        created.Value.Kind.ShouldBe(StepResultKind.Advanced);

        var stored = await harness.RunAsync(owner, run.Id, Ct);
        stored.State.ShouldBe(AutopilotRunState.Researching, "a true-crime run never dispatches a story from `idea_picked`");
        stored.Step.ShouldBe(AutopilotRunStep.Idea, "the research steps are the next handler's to complete");
        stored.EpisodeId.ShouldNotBeNull();
        stored.Attempt.ShouldBe(0);

        return stored;
    }

    /// <summary>
    /// The plan §3.5 continuation after both owner answers (F33: "From either branch the run only continues on
    /// <c>FactSheetApproved</c> … then the gate's true-crime hold … then continues on <c>StoryApproved</c>"): the
    /// approved sheet starts the story (key <c>…:story</c>), the landed story is held for the owner even though the
    /// gate passed (true crime never auto-approves), the owner's approval dispatches the run's single render, and the
    /// approved cut creates the run's publication.
    /// </summary>
    /// <remarks>
    /// The three landing points that belong to other tasks are written as the row the next step reads, never as a
    /// second implementation of it: the story version and its <c>story_generated.v1</c> transition are the story
    /// pipeline's own domain calls (<see cref="Episode.CompleteStoryGeneration"/>), the render's landing is
    /// <see cref="Episode.RenderSucceeded"/>, and the publication consent belongs to 06.2.2-T3. The run's own moves —
    /// the wait, the gate hold, the render dispatch and the create — are the production step handlers.
    /// </remarks>
    [Fact]
    public async Task TrueCrimeRun_StoryStaysWaitingWhileTheSheetIsNotApprovable()
    {
        // The continuation after the sheet wait is F13's floor: 02.5.4 refuses to approve a sheet whose single note
        // cites no second domain, so the run keeps waiting and never dispatches `…:story`.
        await using var harness = await StartAsync(finderGo: false);
        var owner = await harness.CreateOwnerAsync(Ct);
        var world = await harness.CreateAuthorizedSeriesAsync(owner, topic: Topic.TrueCrime, ct: Ct);
        var run = await ResearchingRunAsync(harness, owner, world.SeriesId, world.AuthorizationId);
        var episodeId = run.EpisodeId!.Value;

        (await harness.AdvanceAsync(run.Id, owner, Ct)).Value.Code.ShouldBe(AutopilotRunStep.SourcesRequired);
        await AddOwnerNoteAsync(harness, owner, episodeId);
        (await harness.AdvanceAsync(run.Id, owner, Ct)).Value.Code.ShouldBe(AutopilotRunStep.FactSheet);

        var sheet = await new FactSheetBuilder(Database, AsUser(owner), harness.Clock).CreateAsync(owner, episodeId, Ct);

        // 02.5.4's approval answers 409 `fact_sheet_sources_insufficient` for one source without a second domain.
        (await ApproveSheetExpectingRefusalAsync(harness, owner, episodeId, sheet.Id))
            .ShouldBe("fact_sheet_sources_insufficient");

        // Nothing moved: still the sheet wait, no story dispatch, no owner-action notice.
        var waiting = await harness.RunAsync(owner, run.Id, Ct);
        waiting.State.ShouldBe(AutopilotRunState.WaitingOwner);
        waiting.WaitingFor.ShouldBe(WaitingFor.FactSheet);
        waiting.Attempt.ShouldBe(0);
        (await harness.GenerationRunKeysAsync(world.SeriesId, Ct))
            .ShouldAllBe(key => !key.EndsWith($":{AutopilotRunStep.Story}", StringComparison.Ordinal));
        NoticeRows(owner).ShouldBe(0);
    }

    /// <summary>
    /// Turns the finder's kill switch on for the go branch: <c>FindSourcesCommandHandler</c> answers
    /// <c>feature_disabled</c> while the row is missing, so a go without it would retry forever instead of spending
    /// the search. Written before the host starts because the flag port caches its answer process-wide.
    /// </summary>
    private async Task EnableSourceFinderFlagAsync()
    {
        var now = Start.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        await Database.ExecuteAsync("DELETE FROM feature_flags WHERE key = 'source_finder'", Ct);
        await Database.ExecuteAsync(
            "INSERT INTO feature_flags (key, enabled, rules, created_at, updated_at) "
            + $"VALUES ('source_finder', true, '{{}}', '{now}', '{now}')",
            Ct);
    }

    /// <summary>The owner-action notices this test's owner queued (scoped, so a sibling's rows never leak in).</summary>
    private long NoticeRows(Guid owner) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE type = '{NoticeType}' AND user_id = '{owner}'");

    /// <summary>
    /// The story writer's landing: the version the job saves plus the transition
    /// <c>StoryGenerationProgress.CompleteAsync</c> makes, so <c>story_generated.v1</c> is raised by the product.
    /// </summary>
    /// <remarks>
    /// Written with the domain calls the pipeline itself makes (02.4's generation run row, then the episode's
    /// transition), not with a second implementation: the run's <c>…:story</c> generation row must exist, which is
    /// what the story step dispatched.
    /// </remarks>
    private async Task<Guid> LandStoryAsync(
        AutopilotTickHarness harness,
        Guid owner,
        Guid seriesId,
        DateTimeOffset slotAt,
        Guid episodeId,
        int version)
    {
        var key = AutopilotIdempotencyKeys.For(seriesId, slotAt, AutopilotRunStep.Story);
        var now = harness.Clock.UtcNow;

        await using var db = Database.CreateAppDbContext(AsUser(owner), harness.Clock);
        var generationRun = await db.GenerationRuns
            .SingleAsync(row => row.SeriesId == seriesId && row.IdempotencyKey == key, Ct);

        var draft = StoryDraft(version);
        var storyVersion = StoryVersion.Create(
            owner,
            episodeId,
            version,
            StoryVersionSource.Generated,
            draft,
            StoryContentJson.Write(draft, new JsonArray()),
            "true-crime-run-test",
            "pv-true-crime-run",
            generationRun.Id,
            parentVersionId: null,
            factSheetId: null,
            auditReportJson: null,
            similarityScore: null,
            now);
        db.StoryVersions.Add(storyVersion);

        var episode = await db.Episodes.SingleAsync(row => row.Id == episodeId, Ct);
        var completed = episode.CompleteStoryGeneration(
            generationRun.Id,
            GenerationTrigger.Autopilot,
            [storyVersion.Id],
            storyVersion.Id,
            now);
        completed.IsError.ShouldBeFalse(completed.IsError ? completed.FirstError.Description : "story landed");

        generationRun.Start(now);
        generationRun.Succeed(0m, storyVersion.Id, resultFactSheetId: null, now);

        await db.SaveChangesAsync(Ct);

        return storyVersion.Id;
    }

    /// <summary>The code 02.5.4's approval answers, run in a Worker scope pinned to the owner.</summary>
    private static async Task<string> ApproveSheetExpectingRefusalAsync(
        AutopilotTickHarness harness,
        Guid owner,
        Guid episodeId,
        Guid sheetId)
    {
        using var scope = harness.Host.CreateScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(owner);

        var approved = await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(new ApproveFactSheetCommand(episodeId, ETags.FactSheet(sheetId)), Ct);

        approved.IsError.ShouldBeTrue("a sheet with one sourceless note cannot pass F13");

        return approved.FirstError.Code;
    }

    /// <summary>The owner's own add-source command, run in a Worker scope pinned to that owner.</summary>
    private static async Task AddOwnerNoteAsync(AutopilotTickHarness harness, Guid owner, Guid episodeId)
    {
        using var scope = harness.Host.CreateScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(owner);

        var added = await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(new AddSourceCommand(episodeId, Url: null, NoteText: "a pasted source for the fact sheet"), Ct);

        added.IsError.ShouldBeFalse(string.Join(", ", added.Errors.Select(error => error.Code)));
    }

    private static TestCurrentUser AsUser(Guid userId) => new() { Id = userId };

    /// <summary>The status of the episode's newest fact sheet.</summary>
    private async Task<FactSheetStatus> SheetStatusAsync(Guid owner, Guid episodeId)
    {
        await using var db = Database.CreateAppDbContext(AsUser(owner), new FakeClock(Start));

        return await db.FactSheets
            .Where(sheet => sheet.EpisodeId == episodeId)
            .OrderByDescending(sheet => sheet.Version)
            .Select(sheet => sheet.Status)
            .FirstAsync(Ct);
    }

    /// <summary>02.3.3's approval write: the episode moves <c>story_review → story_approved</c> on its current version.</summary>
    private async Task ApproveStoryAsync(AutopilotTickHarness harness, Guid owner, Guid episodeId)
    {
        await using var db = Database.CreateAppDbContext(AsUser(owner), harness.Clock);
        var episode = await db.Episodes.SingleAsync(row => row.Id == episodeId, Ct);

        var approved = episode.ApproveStory(
            episode.CurrentStoryVersionId!.Value,
            ApprovalSource.User,
            harness.Clock.UtcNow);
        approved.IsError.ShouldBeFalse(approved.IsError ? approved.FirstError.Description : "story approved");

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>The render's landing: the transition the render ingest makes, which raises <c>render_succeeded.v1</c>.</summary>
    private async Task LandRenderAsync(AutopilotTickHarness harness, Guid owner, Guid episodeId)
    {
        await using var db = Database.CreateAppDbContext(AsUser(owner), harness.Clock);
        var episode = await db.Episodes.SingleAsync(row => row.Id == episodeId, Ct);

        var rendered = episode.RenderSucceeded(harness.Clock.UtcNow);
        rendered.IsError.ShouldBeFalse(rendered.IsError ? rendered.FirstError.Description : "render landed");

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>The slot's publication-create instant, from the shipped options the Worker reads (§3.4).</summary>
    private static DateTimeOffset PublicationCreateAt(AutopilotTickHarness harness, AutopilotSettings settings)
    {
        using var scope = harness.Host.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<AutopilotRunOptions>();

        return AutopilotTiming.Compute(Slot, settings, options.ToTimingOptions()).PublicationCreateAt;
    }

    /// <summary>One publication row as <c>status|episode|authorization|publish_at-equals-slot</c>.</summary>
    private string PublicationRow(Guid publicationId) =>
        SqlProbe.ScalarText(
            Database,
            "SELECT status || '|' || episode_id::text || '|' || coalesce(autopilot_authorization_id::text, 'none') "
            + $"|| '|' || (publish_at = '{Slot:O}')::text FROM publications WHERE id = '{publicationId}'");

    /// <summary>A deterministic draft of the shape the story end-to-end tests script (three labelled scenes).</summary>
    private static StoryContentV1 StoryDraft(int version) => new(
        $"Đêm kể chuyện số {version}",
        new StorySegmentV1("Mở đầu: tiếng chuông chùa lẫn trong mưa đêm.", ["k1"], []),
        [new StorySegmentV1("Thân bài: người kể lần theo ký ức của một nhân chứng.", ["k2"], [])],
        new StorySegmentV1("Kết: ký ức trở thành lời kể được ghi lại.", ["k3"], []),
        "Một người kể chuyện lần theo ký ức của nhân chứng.",
        ["ky-uc"],
        [],
        new StoryStructureV1("a", "b", "c", "d", "e"));
}

/// <summary>
/// The go branch of <see cref="ISourceFinderGo"/>: 02.7.2-T3's finder runner does not exist, so a test that needs
/// the "02.7.1 recorded a go" half of F33 answers it instead of waiting for a runner to register itself.
/// </summary>
internal sealed class FixedSourceFinderGo(bool hasGo) : ISourceFinderGo
{
    /// <inheritdoc />
    public ValueTask<bool> HasGoAsync(CancellationToken ct) => ValueTask.FromResult(hasGo);
}

/// <summary>
/// A gate whose story verdict passes (the run still holds, because a true-crime story is never auto-approved: the
/// hold is the step's own rule, not the verdict's) and whose video verdict passes, so the run can reach its
/// publication. The harness's <see cref="ScriptedAutoApprovalGate"/> scripts no video answer at all.
/// </summary>
/// <summary>
/// The flag port with the finder's kill switch scripted: <c>source_finder</c> answers the case's branch and every
/// other key stays on (the harness's autopilot flag is on too). Written instead of a <c>feature_flags</c> row because
/// the shipped cache answers process-wide and a row written after the host starts is not visible to it.
/// </summary>
internal sealed class ScriptedFeatureFlags(bool finderGo) : IFeatureFlags
{
    /// <inheritdoc />
    public Task<bool> IsEnabledAsync(string key, CancellationToken ct) =>
        Task.FromResult(key != SourceFinderGo.FeatureKey || finderGo);
}

/// <summary>
/// The research graph's allow list, permissive: the test drives the run's steps, not the finder's site policy, and
/// the Worker registers no allow list at all (it never serves the search route).
/// </summary>
internal sealed class PermissiveResearchAllowList : IResearchAllowList
{
    /// <inheritdoc />
    public IReadOnlyList<AllowedSite> Approved(Language language) => [];

    /// <inheritdoc />
    public bool IsAllowed(string registrableOrHost) => true;

    /// <inheritdoc />
    public string Hash => "test-allow-list";
}

internal sealed class PassingTrueCrimeGate : IAutoApprovalGate
{
    /// <inheritdoc />
    public ValueTask<ErrorOr<StoryGateOutcome>> EvaluateStoryAsync(Guid runId, int attempt, CancellationToken ct)
    {
        var result = new StoryGateResult(
            Pass: true,
            Hold: false,
            Attempt: attempt,
            StoryVersionId: Guid.CreateVersion7(),
            Checks: [],
            Failures: []);

        return ValueTask.FromResult<ErrorOr<StoryGateOutcome>>(
            new StoryGateOutcome(result, AutoApproved: true, Reason: null));
    }

    /// <inheritdoc />
    public ValueTask<ErrorOr<VideoGateOutcome>> EvaluateVideoAsync(Guid runId, CancellationToken ct)
    {
        var result = new VideoGateResult(
            Pass: true,
            RenderJobId: Guid.CreateVersion7(),
            AssetId: Guid.CreateVersion7(),
            Checks: [],
            Failures: []);

        return ValueTask.FromResult<ErrorOr<VideoGateOutcome>>(
            new VideoGateOutcome(result, AutoApproved: true, Reason: null));
    }
}
