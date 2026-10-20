using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Gates;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.OpenAI;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using NightTales.TestKit.Email;
using NightTales.TestKit.Moderation;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Autopilot;

/// <summary>
/// The content gate of the story gate over real rows (02.8.2-T2 task file "Tests", Acceptance 3, 4, 6, 7): check 7
/// moderates the narration, the metadata and every expanded scene prompt, check 8 detects the real-world signals,
/// and neither a flagged scene prompt nor a framing phrase ever approves or blocks a story silently.
/// </summary>
/// <remarks>
/// <para>
/// <b>The real host, the real evaluator.</b> Every case drives <see cref="IAutoApprovalGate.EvaluateStoryAsync"/> or
/// <see cref="IAutopilotContentGate.CheckAsync"/> from a scope pinned to the run's owner (<see cref="ISystemContext"/>,
/// D10/D13) and reads the stored rows with SQL: the assertions are about what production wrote. The moderator is the
/// TestKit's <see cref="FakeContentModerator"/> except in
/// <see cref="AutopilotModerationGate_RecordsUsageWithZeroCost"/>, which runs the real
/// <see cref="OpenAiContentModerator"/> over a scripted transport so the free-call usage row is the adapter's own.
/// </para>
/// <para>
/// <b>The scene prompt is the trigger, not the narration.</b> The seeded series carries a <c>style_templates</c> row
/// (<c>SeriesBuilder</c> inserts one, as the 03 catalog requires), so check 7 expands every segment keyword of the
/// story; the fake flags only the prompt of the one keyword this test names, which is what makes the
/// <c>moderation_events.excerpt</c> assertion evidence about attribution rather than about a whole-story verdict
/// (task Acceptance 6).
/// </para>
/// </remarks>
/// <param name="database">The class's own migrated database, from <see cref="IntegrationTest"/>.</param>
public sealed class ContentGateTests(TestDatabase database) : IntegrationTest(database)
{
    private const string NoticeType = "autopilot_owner_action_required.v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A keyword whose expanded prompt is flagged while the narration is clean fails check 7, writes exactly one
    /// <c>moderation_events</c> row whose excerpt is that scene prompt (never the narration), tells the owner which
    /// check failed, and leaves the episode in <c>story_review</c> (task Acceptance 3 and 6).
    /// </summary>
    [Fact]
    public async Task StoryGate_ScenePromptFlagged_FailsAndNotifiesOwner()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct);
        var scenePrompt = ScenePromptFor(world, GateRunSeed.HarbourKeyword);
        var moderator = new FakeContentModerator().EnqueueFlaggedWhen(
            ModerationStage.AutopilotGate,
            texts => texts.Any(text => text.Contains(scenePrompt, StringComparison.Ordinal)),
            "sexual");
        using var factory = Factory(dryRun: false, moderator: moderator);

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        evaluated.Value.Result.Pass.ShouldBeFalse();
        evaluated.Value.Reason.ShouldBe(GateStopReason.FailedChecks);
        FailedCheckIds(world.RunId).ShouldContain(StoryGateCheckIds.ModerationGate);
        StoryFlag(world.RunId, "pass").ShouldBeFalse();

        // One batch call, then one per-chunk re-check per chunk (plan step 5): the batch is the gate's own text
        // list, so the narration, the metadata and both scene prompts travel in one call.
        moderator.Checks[0].Texts.ShouldContain(text => text.Contains(GateRunSeed.StoryMarker, StringComparison.Ordinal));
        moderator.Checks[0].Texts.ShouldContain(scenePrompt);

        EventRows(world.EpisodeId).ShouldBe(["autopilot_gate|openai_text|sexual"]);
        var excerpts = EventExcerpts(world.EpisodeId);
        excerpts.Count.ShouldBe(1);
        excerpts[0].ShouldBe(scenePrompt, "the excerpt names the scene prompt that was flagged");
        excerpts[0].Length.ShouldBeLessThanOrEqualTo(300, "the excerpt is at most 300 characters (D12)");
        excerpts[0].ShouldNotContain(GateRunSeed.StoryMarker, Case.Sensitive, "the clean narration is not the excerpt");

        StoredStatus(world.EpisodeId).ShouldBe("story_review");
        NoticeRows(world).ShouldBe(1);
        NoticePayloads(world).ShouldContain(OwnerActionReasons.FailedChecks);
        NoticeFailureCodes(world).ShouldContain(StoryGateCheckIds.ModerationGate);
        AutoApproveAuditRows(world).ShouldBe(0);
    }

    /// <summary>
    /// A framing phrase in the narration fails check 8 on its own — the stored <c>real_world_claims[]</c> is empty —
    /// and even a live run approves nothing: the episode stays in <c>story_review</c> with the owner told
    /// (task Acceptance 4's hit side, 02 README F31).
    /// </summary>
    [Fact]
    public async Task StoryGate_RealWorldFramingPhrase_LeavesEpisodeInReview()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct, draft: GateRunSeed.RealWorldDraft());
        using var factory = Factory(dryRun: false);

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        evaluated.Value.Result.Pass.ShouldBeFalse();
        evaluated.Value.AutoApproved.ShouldBeFalse();
        evaluated.Value.Reason.ShouldBe(GateStopReason.FailedChecks);

        FailedCheckIds(world.RunId).ShouldContain(StoryGateCheckIds.RealWorldSignal);
        FailedCheckIds(world.RunId).ShouldNotContain(
            StoryGateCheckIds.ModerationGate,
            "a framing phrase is not a moderation flag: the two checks are independent");

        // The stored block names the kind and the D22 address of the hit (F31, task "Real-world detection").
        var realWorld = RealWorldBlock(world.RunId);
        realWorld.ShouldContain("\"hit\": true");
        realWorld.ShouldContain("framing_lexicon");
        realWorld.ShouldContain("p:1", Case.Sensitive, "the hit names the D22 segment it fired in");

        StoredStatus(world.EpisodeId).ShouldBe("story_review");
        NoticeRows(world).ShouldBe(1);
        NoticePayloads(world).ShouldContain(OwnerActionReasons.FailedChecks);
        NoticeFailureCodes(world).ShouldContain(StoryGateCheckIds.RealWorldSignal);
        AutoApproveAuditRows(world).ShouldBe(0, "nothing is approved while a content check fails, not even in live mode");
    }

    /// <summary>
    /// A clean story passes both content checks, the stored <c>real_world</c> block is exactly
    /// <c>{"hit": false, "signals": []}</c>, and the gate changes nothing about the version: the episode's
    /// <c>currentStory.auditReport</c> read through <c>GET /api/v1/episodes/{id}</c> is the one the seed stored
    /// (task Acceptance 4).
    /// </summary>
    [Fact]
    public async Task StoryGate_RealWorldClean_PassesContentChecks()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);

        var before = await AuditReportAsync(client, world.EpisodeId);
        before.ShouldNotBeNullOrWhiteSpace("the seed stores an audit.v1 document, so the comparison below is evidence");

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        evaluated.Value.Result.Pass.ShouldBeTrue();
        CheckPass(world.RunId, StoryGateCheckIds.ModerationGate).ShouldBeTrue();
        CheckPass(world.RunId, StoryGateCheckIds.RealWorldSignal).ShouldBeTrue();

        RealWorldBlock(world.RunId).ShouldBe(
            "{\"hit\": false, \"signals\": []}",
            "the clean shape the owner reads on the run row (Acceptance 4)");

        var after = await AuditReportAsync(client, world.EpisodeId);
        after.ShouldBe(before, "the gate result lives on the run, never on the story version");
    }

    /// <summary>
    /// The real adapter's free-call row through the production path: a full story evaluation over the production
    /// <see cref="OpenAiContentModerator"/> writes one <c>usage_records</c> row — <c>operation = moderation</c>,
    /// <c>cost_usd = 0</c>, <c>estimated = true</c> — attributed to the story generation run the evaluator pushes
    /// into <see cref="ModerationScope"/> (task Acceptance 3, D12/D14).
    /// </summary>
    /// <remarks>
    /// <b>The honest seam.</b> No other test in this solution drives the moderation adapter over HTTP from the Api
    /// host, so this one replaces only its transport with a scripted handler that answers one clean result per input;
    /// the adapter, the gate, the evaluator and the <see cref="IUsageMeter"/> are the production ones. The scope is
    /// opened by the evaluator (that is check 7's contract), and
    /// <see cref="StoryGate_ModerationGateCall_CarriesTheRunScope"/> pins which ids it carries, so the row's
    /// <c>generation_run_id</c> here is the seed's own row and not one the test invented.
    /// </remarks>
    [Fact]
    public async Task AutopilotModerationGate_RecordsUsageWithZeroCost()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct);
        var generationRunId = await SeedGenerationRunAsync(world);
        var handler = new ScriptedModerationHandler();
        using var factory = Factory(realModerator: handler);

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        evaluated.Value.Result.Pass.ShouldBeTrue();
        handler.Requests.ShouldBe(1, "the clean verdict is one batch call, and every chunk travels in it");

        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM usage_records WHERE episode_id = '{world.EpisodeId}' AND operation = 'moderation'")
            .ShouldBe(1);

        // version 2, not the seed's own 1: the Api host boots after GateRunSeed and its PriceVersionSeeder inserts the
        // config's row as max + 1 with active_from = the host clock's now (02 D14), so that row is the newest priced
        // version. The lookup instant is the evaluated version's own created_at — StoryGateEvaluator passes
        // version.CreatedAt into StoryGateInputFactory (StoryGateEvaluator.cs:260), which resolves the active row
        // with active_from <= it, newest first (IPriceCatalog.GetActiveAsync). The free moderation row must name the
        // price version the run's check 7 was evaluated under.
        var priceVersion = SqlProbe.ScalarText(
            Database,
            "SELECT version::text FROM price_versions WHERE active_from <= "
            + $"(SELECT created_at FROM story_versions WHERE id = '{world.VersionId}') "
            + "ORDER BY active_from DESC, version DESC LIMIT 1");

        priceVersion.ShouldBe("2");

        SqlProbe.ScalarText(
            Database,
            "SELECT provider || '|' || cost_usd::text || '|' || estimated::text || '|' || generation_run_id::text "
            + "|| '|' || price_version::text || '|' || user_id::text FROM usage_records "
            + $"WHERE episode_id = '{world.EpisodeId}' AND operation = 'moderation'")
            .ShouldBe($"openai|0.000000|true|{generationRunId}|{priceVersion}|{world.UserId}");
    }

    /// <summary>
    /// The evaluator is the caller that opens the <see cref="ModerationScope"/>: every check 7 makes runs inside a
    /// scope naming the run's owner, series and episode, so the free call is metered (task Acceptance 3).
    /// </summary>
    [Fact]
    public async Task StoryGate_ModerationGateCall_CarriesTheRunScope()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct);
        var generationRunId = await SeedGenerationRunAsync(world);
        var moderator = new FakeContentModerator();
        using var factory = Factory(moderator: moderator);

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        moderator.Calls.ShouldBe(1, "check 7 always runs the content gate, and a clean verdict needs one batch call");
        moderator.Checks[0].Stage.ShouldBe(ModerationStage.AutopilotGate);
        moderator.Scopes.ShouldAllBe(
            run => run != null
                && run.UserId == world.UserId
                && run.SeriesId == world.SeriesId
                && run.EpisodeId == world.EpisodeId
                && run.GenerationRunId == generationRunId,
            "a moderation call outside the run scope is not metered");
    }

    /// <summary>
    /// A category whose score breaches its ceiling while the provider did not flag it still fails check 7
    /// (02.8.2-T2 decision 5 (d), task Acceptance 2 at the level of the stored run row): the thresholds, not the
    /// provider's own flag, decide.
    /// </summary>
    [Fact]
    public async Task StoryGate_ScoreAboveThresholdButNotFlagged_FailsCheckSeven()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct);
        var moderator = new FakeContentModerator().EnqueueScores(
            ModerationStage.AutopilotGate,
            new Dictionary<string, double>(StringComparer.Ordinal) { ["violence/graphic"] = 0.4 });
        using var factory = Factory(moderator: moderator);

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        evaluated.Value.Result.Pass.ShouldBeFalse();
        FailedCheckIds(world.RunId).ShouldContain(StoryGateCheckIds.ModerationGate);

        // The category that breached its ceiling is the evidence the run row carries, even though the provider
        // never flagged it (its own flag threshold is higher than the profile's ceiling).
        SqlProbe.ScalarText(
            Database,
            $"SELECT gate_results -> 'moderation' ->> 'categories' FROM autopilot_runs WHERE id = '{world.RunId}'")
            .ShouldContain("violence/graphic");
        SqlProbe.ScalarText(
            Database,
            $"SELECT gate_results -> 'moderation' ->> 'flagged' FROM autopilot_runs WHERE id = '{world.RunId}'")
            .ShouldBe("true");

        StoredStatus(world.EpisodeId).ShouldBe("story_review");
        NoticeRows(world).ShouldBe(1);
        AutoApproveAuditRows(world).ShouldBe(0);
    }

    /// <summary>
    /// D19 over a full evaluation: the narration, the title, the description, a tag and the scene prompt carry a
    /// distinct marker each, and none of them reaches a log line, the stored verdict, an audit row or an outbox
    /// payload. <c>moderation_events.excerpt</c> is the one carrier allowed to hold content — and this world writes
    /// no such row, so the sweep needs no exception (task Acceptance 7).
    /// </summary>
    [Fact]
    public async Task StoryGate_Logs_ContainNoContentText()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct, draft: GateRunSeed.MarkerDraft());
        var logs = new CapturingLoggerProvider();
        using var factory = Factory(logs: logs);

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));

        // The sink is live and this evaluation wrote through it, so the absence below is evidence.
        var gateLines = logs.LinesOf(typeof(StoryGateEvaluator).FullName!);
        gateLines.ShouldNotBeEmpty("the gate logs every evaluation");
        gateLines.Any(line => line.Message.Contains(world.RunId.ToString(), StringComparison.Ordinal))
            .ShouldBeTrue("the gate logs the run it evaluated, so this capture is the gate's own");

        string[] markers =
        [
            GateRunSeed.ParagraphMarker,
            GateRunSeed.TitleMarker,
            GateRunSeed.DescriptionMarker,
            GateRunSeed.TagMarker,
            GateRunSeed.KeywordMarker,
        ];

        foreach (var line in logs.Lines())
        {
            foreach (var marker in markers)
            {
                line.Message.ShouldNotContain(marker, Case.Sensitive, $"no log line carries story text: {line.Message}");
            }
        }

        SqlProbe.Count(Database, $"SELECT count(*) FROM moderation_events WHERE episode_id = '{world.EpisodeId}'")
            .ShouldBe(0, "nothing was flagged, so the one carrier allowed to hold content wrote no row");

        var stored = string.Join(
            '\n',
            SqlProbe.QueryStrings(
                Database,
                $"SELECT coalesce(gate_results::text, '') FROM autopilot_runs WHERE episode_id = '{world.EpisodeId}'"),
            SqlProbe.QueryStrings(
                Database,
                $"SELECT coalesce(data::text, '') FROM audit_logs WHERE subject_user_id = '{world.UserId}'"),
            SqlProbe.QueryStrings(
                Database,
                $"SELECT payload::text FROM outbox_messages WHERE user_id = '{world.UserId}'"));

        foreach (var marker in markers)
        {
            stored.ShouldNotContain(marker, Case.Sensitive, "the verdict, the audit rows and the payload carry ids only");
        }
    }

    /// <summary>The scene prompt of one keyword: the seeded template with its <c>{term}</c> replaced (03 README D1).</summary>
    /// <param name="world">The seeded world, for its series' style template row.</param>
    /// <param name="keyword">The story keyword.</param>
    /// <returns>The moderation text <c>ScenePromptExpander</c> sends for that keyword.</returns>
    private string ScenePromptFor(GateRunSeed.GateWorld world, string keyword) =>
        GateRunSeed.StyleTemplateText(Database, world.SeriesId).Replace("{term}", keyword, StringComparison.Ordinal);

    /// <summary>Queues one active <c>story</c> generation run, the FK the moderation usage row points at.</summary>
    /// <param name="world">The seeded world the run belongs to.</param>
    /// <returns>The generation run's id.</returns>
    private async Task<Guid> SeedGenerationRunAsync(GateRunSeed.GateWorld world)
    {
        await using var context = Database.CreateAppDbContext(
            new TestCurrentUser { Id = world.UserId },
            new FakeClock(GateRunSeed.Start));

        var run = GenerationRun.Queue(
            world.UserId,
            world.SeriesId,
            world.EpisodeId,
            GenerationKind.Story,
            GenerationTrigger.User,
            idempotencyKey: null,
            previousEpisodeStatus: EpisodeStatus.Draft,
            inputFactSheetId: null,
            reservationId: Guid.CreateVersion7(),
            worstCaseUsd: 0.25m,
            now: GateRunSeed.Start,
            countsTowardLimit: false).Value;

        context.GenerationRuns.Add(run);
        await context.SaveChangesAsync(Ct);

        return run.Id;
    }

    /// <summary>The <c>currentStory.auditReport</c> of <c>GET /api/v1/episodes/{id}</c>, as stored jsonb.</summary>
    /// <param name="client">A client authenticated as the owner.</param>
    /// <param name="episodeId">The episode to read.</param>
    /// <returns>The audit report's JSON text.</returns>
    private static async Task<string> AuditReportAsync(HttpClient client, Guid episodeId)
    {
        using var response = await client.GetAsync($"/api/v1/episodes/{episodeId}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        return document.RootElement.GetProperty("currentStory").GetProperty("auditReport").GetRawText();
    }

    private static async Task<ErrorOr<StoryGateOutcome>> EvaluateAsync(
        ApiFactory factory,
        GateRunSeed.GateWorld world,
        int attempt = 1)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);
        var gate = scope.ServiceProvider.GetRequiredService<IAutoApprovalGate>();

        return await gate.EvaluateStoryAsync(world.RunId, attempt, Ct);
    }

    private ApiFactory Factory(
        bool dryRun = true,
        IContentModerator? moderator = null,
        HttpMessageHandler? realModerator = null,
        ILoggerProvider? logs = null)
    {
        var factory = new ApiFactory(Database, new FakeClock(GateRunSeed.Start))
            .WithSetting("Autopilot:Gates:DryRun", dryRun ? "true" : "false")
            .WithSetting("Similarity:Corpus:MaxPastEpisodes", "50")
            .WithServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddScoped<IEmailSender>(provider => new FakeEmailSender(
                    new FakeEmailSenderState(),
                    (AppDbContext)provider.GetRequiredService<IAppDbContext>(),
                    provider.GetRequiredService<IClock>()));

                if (realModerator is not null)
                {
                    // The production adapter over a scripted transport: the zero-cost row is the adapter's own
                    // (AutopilotModerationGate_RecordsUsageWithZeroCost).
                    services.Configure<HttpClientFactoryOptions>(
                        OpenAiServiceCollectionExtensions.HttpClientName,
                        options => options.HttpMessageHandlerBuilderActions.Add(
                            builder => builder.PrimaryHandler = realModerator));
                    services.RemoveAll<IContentModerator>();
                    services.AddScoped<IContentModerator, OpenAiContentModerator>();

                    return;
                }

                if (moderator is not null)
                {
                    services.RemoveAll<IContentModerator>();
                    services.AddSingleton(moderator);
                }
            });

        return logs is null ? factory : factory.WithLoggerProvider(logs);
    }

    private static string Describe(ErrorOr<StoryGateOutcome> evaluated) =>
        evaluated.IsError ? string.Join("; ", evaluated.Errors.Select(error => $"{error.Code}: {error.Description}")) : "ok";

    private bool CheckPass(Guid runId, string checkId) =>
        string.Equals(
            SqlProbe.ScalarText(
                Database,
                "SELECT entry ->> 'pass' FROM autopilot_runs, jsonb_array_elements(gate_results -> 'story' -> 'checks') AS entry "
                + $"WHERE id = '{runId}' AND entry ->> 'id' = '{checkId}'"),
            "true",
            StringComparison.Ordinal);

    private bool StoryFlag(Guid runId, string key) =>
        string.Equals(
            SqlProbe.ScalarText(Database, $"SELECT gate_results -> 'story' ->> '{key}' FROM autopilot_runs WHERE id = '{runId}'"),
            "true",
            StringComparison.Ordinal);

    private List<string> FailedCheckIds(Guid runId) =>
        SqlProbe.QueryStrings(
            Database,
            "SELECT entry ->> 'id' FROM autopilot_runs, jsonb_array_elements(gate_results -> 'story' -> 'checks') AS entry "
            + $"WHERE id = '{runId}' AND (entry ->> 'pass')::boolean = false");

    private string RealWorldBlock(Guid runId) =>
        SqlProbe.ScalarText(Database, $"SELECT (gate_results -> 'real_world')::text FROM autopilot_runs WHERE id = '{runId}'");

    private string StoredStatus(Guid episodeId) =>
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{episodeId}'");

    /// <summary>The evidence rows of this episode, one line each: <c>stage|source|categories</c>.</summary>
    /// <param name="episodeId">The episode whose rows to read.</param>
    /// <returns>One line per row, in write order.</returns>
    private List<string> EventRows(Guid episodeId) =>
        SqlProbe.QueryStrings(
            Database,
            "SELECT stage || '|' || source || '|' || array_to_string(categories, ',') FROM moderation_events "
            + $"WHERE episode_id = '{episodeId}' ORDER BY created_at");

    /// <summary>The <c>excerpt</c> of this episode's evidence rows.</summary>
    /// <param name="episodeId">The episode whose rows to read.</param>
    /// <returns>One excerpt per row.</returns>
    private List<string> EventExcerpts(Guid episodeId) =>
        SqlProbe.QueryStrings(
            Database,
            $"SELECT coalesce(excerpt, '') FROM moderation_events WHERE episode_id = '{episodeId}' ORDER BY created_at");

    /// <summary>The notice rows this world queued: <c>type</c> plus the owner, proven scoping.</summary>
    /// <param name="world">The seeded world.</param>
    /// <returns>How many notices the owner has.</returns>
    private long NoticeRows(GateRunSeed.GateWorld world) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM outbox_messages WHERE type = '{NoticeType}' AND user_id = '{world.UserId}'");

    /// <summary>The notice payloads themselves, so the reason they carry is asserted and not just counted.</summary>
    /// <param name="world">The seeded world.</param>
    /// <returns>The payloads, joined.</returns>
    private string NoticePayloads(GateRunSeed.GateWorld world) =>
        string.Join(
            '\n',
            SqlProbe.QueryStrings(
                Database,
                $"SELECT payload::text FROM outbox_messages WHERE type = '{NoticeType}' AND user_id = '{world.UserId}'"));

    /// <summary>The failure codes the notice audit row carries (check ids for a failed evaluation).</summary>
    /// <param name="world">The seeded world.</param>
    /// <returns>The <c>failures</c> array of the notice's audit row.</returns>
    private string NoticeFailureCodes(GateRunSeed.GateWorld world) =>
        SqlProbe.ScalarText(
            Database,
            $"SELECT coalesce(data -> 'failures', '[]'::jsonb)::text FROM audit_logs "
            + $"WHERE action = '{AutopilotNotificationService.AuditAction}' AND data->>'runId' = '{world.RunId}'");

    /// <summary>The auto-approval audit rows of this world's owner: zero unless the gate approved.</summary>
    /// <param name="world">The seeded world.</param>
    /// <returns>The row count.</returns>
    private long AutoApproveAuditRows(GateRunSeed.GateWorld world) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM audit_logs WHERE action = '{AutopilotApprovalService.StoryApprovedAction}' "
            + $"AND subject_user_id = '{world.UserId}'");

    /// <summary>
    /// A transport that answers one clean <c>omni-moderation-latest</c> result per input and counts the requests, so
    /// the real adapter's metering path is driven without a provider.
    /// </summary>
    private sealed class ScriptedModerationHandler : HttpMessageHandler
    {
        /// <summary>How many requests reached the transcript.</summary>
        public int Requests { get; private set; }

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;

            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(body);
            var inputs = document.RootElement.TryGetProperty("input", out var input) ? input.GetArrayLength() : 0;

            var results = new StringBuilder();
            for (var index = 0; index < inputs; index++)
            {
                if (index > 0)
                {
                    results.Append(',');
                }

                results.Append("""{"flagged":false,"categories":{"hate":false},"category_scores":{"hate":0.001}}""");
            }

            var payload = "{\"id\":\"modr-1\",\"model\":\"omni-moderation-latest\",\"results\":[" + results + "]}";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>One line the host rendered, with the category it came from.</summary>
    /// <param name="Category">The logger category.</param>
    /// <param name="Level">The level.</param>
    /// <param name="EventId">The event id.</param>
    /// <param name="Message">The rendered message.</param>
    private sealed record LogEntry(string Category, LogLevel Level, int EventId, string Message);

    /// <summary>
    /// A log provider that keeps every line of every category, so a test can assert what a component never writes
    /// (the <c>PollRenderJobTests</c> and <c>FindSourcesEndpointTests</c> precedent).
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = [];

        /// <summary>Every line captured so far, in write order.</summary>
        /// <returns>The lines.</returns>
        public List<LogEntry> Lines()
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }

        /// <summary>Every line of one logger category.</summary>
        /// <param name="category">The full category name, for example the evaluator's type name.</param>
        /// <returns>The lines of that category.</returns>
        public List<LogEntry> LinesOf(string category) =>
            [.. Lines().Where(entry => entry.Category.StartsWith(category, StringComparison.Ordinal))];

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

        /// <inheritdoc />
        public void Dispose()
        {
        }

        private sealed class Sink(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull =>
                null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._entries)
                {
                    owner._entries.Add(new LogEntry(category, logLevel, eventId.Id, formatter(state, exception)));
                }
            }
        }
    }
}
