using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Application.Features.Stories;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Assets;
using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Gates;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Autopilot;

/// <summary>
/// <c>VideoGateEvaluator</c> (02.8.2-T1 plan §3.2, §4 UC3–UC7) against a real PostgreSQL database and the real
/// Api host: the dry run that records a verdict and approves nothing, a failing check that leaves the episode in
/// <c>video_review</c>, the fail-closed reads that answer an error and write nothing, the image-moderation window,
/// the idempotent replay and the stale-verdict refusal.
/// </summary>
/// <remarks>
/// <para>
/// The evaluator is resolved from the real composition root, in a scope pinned to the run's owner
/// (<see cref="ISystemContext.For"/>, 09 README D10) exactly as 02.8.3-T1's orchestrator resolves it, so the
/// tenant filter, the <c>gate_results</c> write, the notice transaction and the approve command are the
/// production ones. The rows are seeded with builders and raw SQL (the <c>PollRenderJobTests.SeedJobAsync</c> and
/// <c>AutopilotNotificationTests.SeedWorldAsync</c> precedents) and never through Agent 3A's story seeding.
/// </para>
/// <para>
/// Every audit, outbox and e-mail assertion is scoped to this test's own user/run (the class database is shared
/// with the rest of the suite).
/// </para>
/// </remarks>
/// <param name="database">The class's own migrated database, from <see cref="IntegrationTest"/>.</param>
[Trait("Category", "Integration")]
public sealed class VideoGateEvaluatorTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The audit action of an autopilot approval (02 README D13, <c>AutopilotApprovalService</c>).</summary>
    private const string VideoApprovedAction = "autopilot.video_auto_approved";

    /// <summary>The audit action of one gate stop (02.8.2 plan §4 UC6).</summary>
    private const string OwnerActionAction = "autopilot.owner_action_required";

    /// <summary>The audit action of the owner's own approve command.</summary>
    private const string EpisodeApprovedAction = "episode.video_approved";

    /// <summary>The outbox type of the owner-action notice.</summary>
    private const string OwnerActionEvent = "autopilot_owner_action_required.v1";

    /// <summary>The outbox type an accepted cut raises.</summary>
    private const string VideoApprovedEvent = "video_approved.v1";

    /// <summary>The e-mail template of the owner-action notice.</summary>
    private const string OwnerActionTemplate = "autopilot_owner_action_required";

    /// <summary>The one instant this class's clock and stamps are built from.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The SHA-256 the stored report and the stored asset share (64 lowercase hex, the column width).</summary>
    private static readonly string ReportSha256 = new('b', 64);

    private static CancellationToken Ct => TestContextToken.Value;

    /// <summary>
    /// A dry run records the verdict, leaves the video in review, tells the owner why and approves nothing: the
    /// calibration mode of 02.8.2-T3 (plan §3.3, §4 UC7).
    /// </summary>
    [Fact]
    public async Task EvaluateVideo_DryRun_RecordsVerdictAndNotifiesDryRun()
    {
        var world = await SeedAsync();
        using var factory = Factory(dryRun: true);

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(string.Join(", ", evaluated.Errors.Select(error => error.Code)));
        evaluated.Value.Result.Pass.ShouldBeTrue("every seeded check passes; the mode is what stops the approval");
        evaluated.Value.AutoApproved.ShouldBeFalse();
        evaluated.Value.Reason.ShouldBe(GateStopReason.DryRun);

        StoredStatus(world).ShouldBe("video_review");
        Scalar($"SELECT (ready_at IS NULL)::text FROM episodes WHERE id = '{world.EpisodeId}'").ShouldBe("true");

        // The verdict is stored, with the mode that decided nothing: the evidence T3's calibration reads.
        DocumentValue(world, "mode").ShouldBe("dry_run");
        DocumentValue(world, "schema_version").ShouldBe(GateResultWriter.SchemaVersion);
        DocumentValue(world, "profile").ShouldBe(AutopilotGateOptions.DefaultProfileName);
        VideoBlock(world)["pass"]!.GetValue<bool>().ShouldBeTrue();
        VideoChecks(world).Count.ShouldBe(VideoGateCheckIds.All.Count);

        // Nothing was approved — not by the command and not by an audit row of its own.
        AuditCount(world, VideoApprovedAction).ShouldBe(0);
        AuditCount(world, EpisodeApprovedAction).ShouldBe(0);
        OutboxCount(world, VideoApprovedEvent).ShouldBe(0);

        // The owner is told, in the verdict's own transaction.
        AuditCount(world, OwnerActionAction).ShouldBe(1);
        Scalar($"SELECT data->>'reason' FROM audit_logs WHERE action = '{OwnerActionAction}' AND data->>'runId' = '{world.RunId}'")
            .ShouldBe(OwnerActionReasons.DryRun);
        OutboxCount(world, OwnerActionEvent).ShouldBe(1);
        EmailCount(world).ShouldBe(1);
    }

    /// <summary>
    /// A check that fails stops the approval, records itself in the verdict and tells the owner which check it
    /// was: the integrated loudness outside the accepted band (plan §3.2 check 4).
    /// </summary>
    [Fact]
    public async Task EvaluateVideo_LoudnessOutOfBand_FailsAndLeavesVideoInReview()
    {
        var world = await SeedAsync(integratedLufs: -25.5);
        using var factory = Factory(dryRun: false);

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(string.Join(", ", evaluated.Errors.Select(error => error.Code)));
        evaluated.Value.Result.Pass.ShouldBeFalse();
        evaluated.Value.AutoApproved.ShouldBeFalse();
        evaluated.Value.Reason.ShouldBe(GateStopReason.FailedChecks);
        evaluated.Value.Result.Failures.ShouldContain(VideoGateCheckIds.Loudness);

        StoredStatus(world).ShouldBe("video_review");
        AuditCount(world, VideoApprovedAction).ShouldBe(0);
        AuditCount(world, EpisodeApprovedAction).ShouldBe(0);
        OutboxCount(world, VideoApprovedEvent).ShouldBe(0);

        // The stored verdict lists the failure with the value and the bound it faced (plan §3.4).
        DocumentValue(world, "mode").ShouldBe("live");
        VideoBlock(world)["pass"]!.GetValue<bool>().ShouldBeFalse();
        (VideoBlock(world)["failures"] as JsonArray)!
            .Select(node => node!.GetValue<string>())
            .ShouldBe([VideoGateCheckIds.Loudness]);

        var loudness = CheckOf(world, VideoGateCheckIds.Loudness);
        loudness["pass"]!.GetValue<bool>().ShouldBeFalse();
        loudness["value"]!.ToJsonString().ShouldBe("-25.5", "the stored check carries the measured value it faced");

        // The notice names the failing check, never a sentence of its own.
        Scalar($"SELECT data->>'reason' FROM audit_logs WHERE action = '{OwnerActionAction}' AND data->>'runId' = '{world.RunId}'")
            .ShouldBe(OwnerActionReasons.FailedChecks);
        Scalar($"SELECT jsonb_array_length(data->'failures')::text FROM audit_logs WHERE action = '{OwnerActionAction}' AND data->>'runId' = '{world.RunId}'")
            .ShouldBe("1");
    }

    /// <summary>
    /// An episode with no <c>succeeded</c> render has nothing to gate: the call answers the error, no verdict is
    /// recorded and nothing is approved or notified (fail closed, plan §"Failure handling").
    /// </summary>
    [Fact]
    public async Task EvaluateVideo_NoSucceededRender_ReturnsRenderMissingAndWritesNothing()
    {
        var world = await SeedAsync(succeededRender: false);
        using var factory = Factory(dryRun: false);

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeTrue();
        evaluated.FirstError.Code.ShouldBe(VideoGateEvaluator.RenderMissingErrorCode);

        RunGateResults(world).ShouldBeNull("no verdict may be stored for a render the gate could not read");
        StoredStatus(world).ShouldBe("video_review");
        AuditCount(world, VideoApprovedAction).ShouldBe(0);
        AuditCount(world, OwnerActionAction).ShouldBe(0);
        OutboxCount(world, OwnerActionEvent).ShouldBe(0);
        EmailCount(world).ShouldBe(0);
    }

    /// <summary>
    /// An <c>openai_image</c> moderation event inside the render's window fails <c>image_moderation</c>: the
    /// check that keeps a cut with a moderated-away image from auto-publishing (plan §3.2 check 8).
    /// </summary>
    [Fact]
    public async Task EvaluateVideo_ImageModerationInsideRenderWindow_FailsImageModeration()
    {
        var world = await SeedAsync();
        await AddImageModerationAsync(world, Start.AddMinutes(-7));
        using var factory = Factory(dryRun: false);

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(string.Join(", ", evaluated.Errors.Select(error => error.Code)));
        evaluated.Value.Result.Failures.ShouldContain(VideoGateCheckIds.ImageModeration);
        evaluated.Value.AutoApproved.ShouldBeFalse();
        StoredStatus(world).ShouldBe("video_review");

        var moderation = CheckOf(world, VideoGateCheckIds.ImageModeration);
        moderation["value"]!.ToJsonString().ShouldBe("1");
        moderation["pass"]!.GetValue<bool>().ShouldBeFalse();
        AuditCount(world, VideoApprovedAction).ShouldBe(0);
        AuditCount(world, OwnerActionAction).ShouldBe(1);
    }

    /// <summary>
    /// Rows of another render — before this job was submitted or after it finished — are not this render's: the
    /// check counts zero and the verdict passes. Without the window, an earlier render's moderated image would
    /// refuse an approvable cut forever.
    /// </summary>
    [Fact]
    public async Task EvaluateVideo_ImageModerationOutsideRenderWindow_IsIgnored()
    {
        var world = await SeedAsync();
        await AddImageModerationAsync(world, Start.AddMinutes(-30));
        await AddImageModerationAsync(world, Start.AddMinutes(10));
        using var factory = Factory(dryRun: true);

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(string.Join(", ", evaluated.Errors.Select(error => error.Code)));
        evaluated.Value.Result.Pass.ShouldBeTrue();
        evaluated.Value.Result.Failures.ShouldBeEmpty();

        var moderation = CheckOf(world, VideoGateCheckIds.ImageModeration);
        moderation["value"]!.ToJsonString().ShouldBe("0");
        moderation["pass"]!.GetValue<bool>().ShouldBeTrue();
    }

    /// <summary>
    /// The replay of an already-approved render is a success that approves nothing a second time: no second
    /// <c>autopilot.video_auto_approved</c> row, no second approve command and no second notice (plan §4 UC4).
    /// </summary>
    [Fact]
    public async Task EvaluateVideo_SecondCall_IsIdempotent()
    {
        var world = await SeedAsync();
        using var factory = Factory(dryRun: false);

        var first = await EvaluateAsync(factory, world);

        first.IsError.ShouldBeFalse(string.Join(", ", first.Errors.Select(error => error.Code)));
        first.Value.AutoApproved.ShouldBeTrue();
        StoredStatus(world).ShouldBe("ready");
        AuditCount(world, VideoApprovedAction).ShouldBe(1);
        AuditCount(world, EpisodeApprovedAction).ShouldBe(1);
        OutboxCount(world, VideoApprovedEvent).ShouldBe(1);

        var second = await EvaluateAsync(factory, world);

        second.IsError.ShouldBeFalse(string.Join(", ", second.Errors.Select(error => error.Code)));
        second.Value.Result.Pass.ShouldBeTrue();

        // The second evaluation re-records the same verdict (one video block, ten checks) and stops there: the
        // approval and its audit row belong to the first call.
        AuditCount(world, VideoApprovedAction).ShouldBe(1);
        AuditCount(world, EpisodeApprovedAction).ShouldBe(1);
        OutboxCount(world, VideoApprovedEvent).ShouldBe(1);
        AuditCount(world, OwnerActionAction).ShouldBe(0);
        Scalar($"SELECT jsonb_array_length(gate_results->'video'->'checks')::text FROM autopilot_runs WHERE id = '{world.RunId}'")
            .ShouldBe(VideoGateCheckIds.All.Count.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A verdict that judges a render the episode no longer carries is refused (<c>video_verdict_stale</c>) and
    /// never approved, and the refusal lands on the run with the owner's notice — the guard that keeps a re-render
    /// from being accepted on the old cut's verdict (plan §"Failure handling").
    /// </summary>
    /// <remarks>
    /// The guard's real trigger is a re-render ingested between the evaluator's read and the approval's own read of
    /// the episode; no test can inject that interleaving, so the service is driven directly with the earlier
    /// render's verdict and the evaluator's own refusal branch (record the code, notify the owner) is executed
    /// through the recorder it calls.
    /// </remarks>
    [Fact]
    public async Task EvaluateVideo_StaleVerdict_IsRefusedAndNotified()
    {
        var world = await SeedAsync();
        var staleAssetId = Guid.CreateVersion7();
        using var factory = Factory(dryRun: false);

        await using var scope = factory.Services.CreateAsyncScope();
        using var pin = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);

        // The verdict of the render the episode carried before the re-render: same passing report, another asset.
        var input = new VideoGateInput(
            Report(),
            new RenderJobFacts(world.RenderJobId),
            new AssetFacts(staleAssetId, ReportSha256, 301.37m),
            SubtitlesEnabled: true,
            Aspect.Landscape,
            ImageModerationEvents: 0);
        var verdict = VideoGateRules.Evaluate(input, AutopilotGateProfile.Defaults);
        verdict.Pass.ShouldBeTrue("the earlier render itself passed; only the episode moved on");

        var approvals = scope.ServiceProvider.GetRequiredService<AutopilotApprovalService>();
        var refused = await approvals.ApproveVideoAsync(world.RunId, verdict, GateMode.Live, Ct);

        refused.IsError.ShouldBeTrue();
        refused.FirstError.Code.ShouldBe("video_verdict_stale");

        StoredStatus(world).ShouldBe("video_review");
        AuditCount(world, VideoApprovedAction).ShouldBe(0);
        AuditCount(world, EpisodeApprovedAction).ShouldBe(0);
        OutboxCount(world, VideoApprovedEvent).ShouldBe(0);

        // The evaluator's own branch on a refused approval: the code is appended to the run's verdict and the
        // owner is told to approve it by hand.
        var recorder = scope.ServiceProvider.GetRequiredService<GateOutcomeRecorder>();
        var recorded = await recorder.RecordApprovalFailureAsync(
            world.RunId,
            VideoGateEvaluator.GateName,
            ApprovalFailureCodes.Of(refused.FirstError),
            Ct);

        recorded.IsError.ShouldBeFalse(string.Join(", ", recorded.Errors.Select(error => error.Code)));
        Scalar($"SELECT gate_results->'video'->>'pass' FROM autopilot_runs WHERE id = '{world.RunId}'").ShouldBe("false");
        Scalar($"SELECT gate_results->'video'->'failures'->>0 FROM autopilot_runs WHERE id = '{world.RunId}'")
            .ShouldBe(ApprovalFailureCodes.ApproveFailed);
        Scalar($"SELECT data->>'reason' FROM audit_logs WHERE action = '{OwnerActionAction}' AND data->>'runId' = '{world.RunId}'")
            .ShouldBe(OwnerActionReasons.OwnerApprovalRequired);
        StoredStatus(world).ShouldBe("video_review");
    }

    /// <summary>The host of these tests: the real Api with the fixed clock and the mode under test.</summary>
    /// <param name="dryRun">The value of <c>Autopilot:Gates:DryRun</c> for this host.</param>
    /// <returns>The factory the gate is resolved from.</returns>
    private ApiFactory Factory(bool dryRun) =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithSetting("Autopilot:Gates:DryRun", dryRun ? "true" : "false");

    /// <summary>Runs one video-gate evaluation the way 02.8.3-T1 does: in a scope pinned to the run's owner.</summary>
    /// <param name="factory">The host.</param>
    /// <param name="world">The seeded world.</param>
    /// <returns>The verdict, or the error that stopped the evaluation.</returns>
    private static async Task<ErrorOr<VideoGateOutcome>> EvaluateAsync(ApiFactory factory, World world)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        using var pin = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);

        var gate = scope.ServiceProvider.GetRequiredService<IAutoApprovalGate>();

        return await gate.EvaluateVideoAsync(world.RunId, Ct);
    }

    /// <summary>
    /// One owner whose episode is in <c>video_review</c>, with a series, a story version, an <c>autopilot_runs</c>
    /// row, a finished render, its asset and the episode's final-video pointer.
    /// </summary>
    /// <param name="episodeStatus">The episode's status.</param>
    /// <param name="succeededRender">Whether the episode carries a <c>succeeded</c> render job.</param>
    /// <param name="integratedLufs">The stored report's integrated loudness (check 4).</param>
    /// <param name="subtitlesEnabled">The stored <c>params.subtitles.enabled</c> flag.</param>
    /// <param name="gateResults">A pre-stored <c>gate_results</c> document, or <see langword="null"/> for none.</param>
    /// <param name="autopilotSettings">The series' <c>autopilot_settings</c> document.</param>
    /// <returns>The seeded ids.</returns>
    private async Task<World> SeedAsync(
        string episodeStatus = "video_review",
        bool succeededRender = true,
        double integratedLufs = -15.0,
        bool subtitlesEnabled = true,
        string? gateResults = null,
        string? autopilotSettings = null)
    {
        var actor = new TestCurrentUser();
        var clock = new FakeClock(Start);
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        actor.Id = user.Id;

        var series = await new SeriesBuilder(Database, actor, clock).CreateAsync(
            user.Id,
            aspect: Aspect.Landscape,
            autopilotSettings: autopilotSettings,
            ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor, clock).CreateAsync(
            user.Id,
            series.Id,
            title: "Bản dựng cổng",
            status: EpisodeStatus.VideoReview,
            ct: Ct);

        if (episodeStatus != "video_review")
        {
            await Database.ExecuteAsync(
                $"UPDATE episodes SET status = '{episodeStatus}' WHERE id = '{episode.Id}'",
                Ct);
        }

        var draft = Draft();
        var version = StoryVersion.Create(
            user.Id,
            episode.Id,
            1,
            StoryVersionSource.Generated,
            draft,
            StoryContentJson.Write(draft, new JsonArray()),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            Start);

        await using (var seedWriter = Database.CreateAppDbContext(actor, clock))
        {
            seedWriter.StoryVersions.Add(version);
            await seedWriter.SaveChangesAsync(Ct);
        }

        var builder = new EpisodeBuilder(Database, actor, clock);
        var asset = await builder.CreateAssetAsync(
            user.Id,
            episode.Id,
            kind: AssetKind.Video,
            durationSec: 301.37m,
            sha256: ReportSha256,
            ct: Ct);
        await builder.AttachFinalVideoAsync(user.Id, episode.Id, asset.Id, Ct);

        var runId = Guid.CreateVersion7();
        var renderJobId = Guid.CreateVersion7();
        await Database.ExecuteAsync(
            "INSERT INTO autopilot_runs (id, user_id, series_id, episode_id, started_at, outcome, gate_results, "
            + "created_at, updated_at) "
            + $"VALUES ('{runId}', '{user.Id}', '{series.Id}', '{episode.Id}', '{Start:O}', 'gate_failed', "
            + (gateResults is null ? "NULL" : $"'{gateResults}'::jsonb")
            + ", now(), now())",
            Ct);

        // BD5's one-active-render-per-episode index covers only the active statuses, so a terminal row is free to
        // stand beside nothing else; a failed row is the "no succeeded render" case.
        var status = succeededRender ? "succeeded" : "failed";
        var submittedAt = Start.AddMinutes(-10);
        var startedAt = Start.AddMinutes(-9);
        var finishedAt = Start.AddMinutes(-5);
        await Database.ExecuteAsync(
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, "
            + "percent, params, attempt, quality_report, submitted_at, started_at, finished_at, worst_case_usd) "
            + $"VALUES ('{renderJobId}', '{user.Id}', '{episode.Id}', '{version.Id}', '{Guid.CreateVersion7()}', "
            + $"'{status}', 'done', 100, '{ParamsJson(version.Id, subtitlesEnabled)}'::jsonb, 1, "
            + $"'{ReportJson(integratedLufs)}'::jsonb, '{submittedAt:O}', '{startedAt:O}', '{finishedAt:O}', 0)",
            Ct);

        return new World(user.Id, series.Id, episode.Id, runId, renderJobId, asset.Id);
    }

    /// <summary>Records one <c>openai_image</c>/<c>render_image</c> moderation event of the episode (check 8).</summary>
    /// <param name="world">The seeded world.</param>
    /// <param name="at">The instant the row carries; the window is what makes it count.</param>
    private Task AddImageModerationAsync(World world, DateTimeOffset at) =>
        Database.ExecuteAsync(
            "INSERT INTO moderation_events (id, user_id, episode_id, source, stage, categories, excerpt, created_at) "
            + $"VALUES ('{Guid.CreateVersion7()}', '{world.UserId}', '{world.EpisodeId}', 'openai_image', "
            + $"'{VideoGateEvaluator.ImageModerationStage}', ARRAY['violence'], NULL, '{at:O}')",
            Ct);

    /// <summary>The check entry of the stored video verdict with the given id.</summary>
    /// <param name="world">The seeded world.</param>
    /// <param name="checkId">One of <see cref="VideoGateCheckIds"/>.</param>
    /// <returns>The stored check object.</returns>
    private JsonObject CheckOf(World world, string checkId) =>
        VideoChecks(world).Single(check => check["id"]!.GetValue<string>() == checkId);

    /// <summary>Every check line of the stored video verdict, in evaluation order.</summary>
    /// <param name="world">The seeded world.</param>
    /// <returns>The stored checks.</returns>
    private List<JsonObject> VideoChecks(World world)
    {
        var checks = new List<JsonObject>();
        if (VideoBlock(world)["checks"] is JsonArray stored)
        {
            foreach (var node in stored)
            {
                checks.Add(node!.AsObject());
            }
        }

        return checks;
    }

    /// <summary>The stored <c>gate_results.video</c> block.</summary>
    /// <param name="world">The seeded world.</param>
    /// <returns>The block.</returns>
    private JsonObject VideoBlock(World world) =>
        JsonNode.Parse(Scalar($"SELECT gate_results->'video' FROM autopilot_runs WHERE id = '{world.RunId}'"))!.AsObject();

    /// <summary>One top-level key of the stored <c>gates.v1</c> document, as text.</summary>
    /// <param name="world">The seeded world.</param>
    /// <param name="key">The document key.</param>
    /// <returns>The stored value.</returns>
    private string DocumentValue(World world, string key) =>
        Scalar($"SELECT gate_results->>'{key}' FROM autopilot_runs WHERE id = '{world.RunId}'");

    /// <summary>The stored <c>gate_results</c> document, or <see langword="null"/> when the column is empty.</summary>
    /// <param name="world">The seeded world.</param>
    /// <returns>The document text.</returns>
    private string? RunGateResults(World world) =>
        Scalar($"SELECT coalesce(gate_results::text, '') FROM autopilot_runs WHERE id = '{world.RunId}'") is { Length: > 0 } text
            ? text
            : null;

    /// <summary>The episode's status.</summary>
    /// <param name="world">The seeded world.</param>
    /// <returns>The status wire name.</returns>
    private string StoredStatus(World world) =>
        Scalar($"SELECT status FROM episodes WHERE id = '{world.EpisodeId}'");

    /// <summary>How many audit rows of one action this test's own user has.</summary>
    /// <param name="world">The seeded world.</param>
    /// <param name="action">The audit action.</param>
    /// <returns>The count.</returns>
    private long AuditCount(World world, string action) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM audit_logs WHERE action = '{action}' AND subject_user_id = '{world.UserId}'");

    /// <summary>How many outbox rows of one type this test's own user has.</summary>
    /// <param name="world">The seeded world.</param>
    /// <param name="type">The outbox type.</param>
    /// <returns>The count.</returns>
    private long OutboxCount(World world, string type) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM outbox_messages WHERE type = '{type}' AND user_id = '{world.UserId}'");

    /// <summary>How many owner-action e-mails this test's own user was queued.</summary>
    /// <param name="world">The seeded world.</param>
    /// <returns>The count.</returns>
    private long EmailCount(World world) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM email_messages WHERE template = '{OwnerActionTemplate}' AND user_id = '{world.UserId}'");

    /// <summary>Runs one scalar text statement (empty when the value is null).</summary>
    /// <param name="sql">The statement.</param>
    /// <returns>The value.</returns>
    private string Scalar(string sql) => SqlProbe.ScalarText(Database, sql);

    /// <summary>
    /// A complete <c>render-params.v1</c> document (05 D11): every member is <c>[JsonRequired]</c>, so the row could
    /// not be loaded without one.
    /// </summary>
    /// <param name="storyVersionId">The version the params name.</param>
    /// <param name="subtitlesEnabled">The <c>subtitles.enabled</c> flag checks 6 and 10 read.</param>
    /// <returns>The serialised document.</returns>
    private static string ParamsJson(Guid storyVersionId, bool subtitlesEnabled) =>
        JsonSerializer.Serialize(new RenderParamsV1(
            RenderParamsV1.CurrentMapperVersion,
            storyVersionId,
            "landscape",
            "vi",
            "kokoro:test",
            Guid.CreateVersion7(),
            "style=test@0123abcd;neg=v1",
            new ImagePlanSummary(3, 1, 2, 1, 0, [], ["paragraph:1"], "style=test@0123abcd;neg=v1"),
            new SubtitlesParams(subtitlesEnabled, "landscape@1"),
            null,
            3));

    /// <summary>
    /// The stored <c>render-quality.v1</c> document (05 README D10) of a render that passed every blocking check,
    /// written by the report type itself so a renamed member fails these tests instead of degrading silently.
    /// </summary>
    /// <param name="integratedLufs">The measured integrated loudness (check 4).</param>
    /// <returns>The serialised document.</returns>
    private static string ReportJson(double integratedLufs) =>
        JsonSerializer.Serialize(new RenderQualityReportV1
        {
            Pass = true,
            PolicyVersion = 1,
            CheckToolVersion = "render_check.py@1",
            DurationSec = 300.0,
            TtsDurationSec = 301.37,
            IntegratedLufs = integratedLufs,
            TruePeakDbtp = -1.0,
            Width = 1920,
            Height = 1080,
            SubtitlesPresent = true,
            Sha256 = ReportSha256,
            Normalized = true,
            Checks = [],
            Hints = [],
            Images = JsonNode.Parse("""{"skipped":[],"moderated_count":0}"""),
        });

    /// <summary>The gate's view of the same passing report, for the stale-verdict test.</summary>
    /// <returns>The facts.</returns>
    private static QualityReportFacts Report() => new(
        Pass: true,
        DurationSec: 300.0,
        TtsDurationSec: 301.37,
        IntegratedLufs: -15.0,
        Width: 1920,
        Height: 1080,
        SubtitlesPresent: true,
        Sha256: ReportSha256,
        SkippedSceneCount: 0,
        ModeratedImageCount: 0,
        Hints: []);

    /// <summary>
    /// The smallest clean story document the version factory accepts (the <c>PollRenderJobTests</c> shape). The gate
    /// never reads it: the row exists so <c>render_jobs.story_version_id</c> holds.
    /// </summary>
    /// <returns>The story content.</returns>
    private static StoryContentV1 Draft() => new(
        "The pier at dawn",
        new StorySegmentV1("Grey mist drifted over the riverside.", ["quiet riverside morning"], []),
        [new StorySegmentV1("A wooden pier stood empty at dusk.", ["empty wooden pier at dusk"], [], "setup")],
        new StorySegmentV1("Cold morning light returned.", ["cold morning light"], []),
        "A quiet story about an empty pier.",
        ["mist", "pier"],
        ["atmosphere"],
        new StoryStructureV1("ghost", "riverside", "a misty dawn", "the narrator", "first_person"));

    /// <summary>The ids of one seeded <c>video_review</c> world.</summary>
    /// <param name="UserId">The run's owner.</param>
    /// <param name="SeriesId">The series.</param>
    /// <param name="EpisodeId">The episode under review.</param>
    /// <param name="RunId">The autopilot run being gated.</param>
    /// <param name="RenderJobId">The finished render job the verdict belongs to.</param>
    /// <param name="AssetId">The episode's final video asset.</param>
    private sealed record World(
        Guid UserId,
        Guid SeriesId,
        Guid EpisodeId,
        Guid RunId,
        Guid RenderJobId,
        Guid AssetId);
}
