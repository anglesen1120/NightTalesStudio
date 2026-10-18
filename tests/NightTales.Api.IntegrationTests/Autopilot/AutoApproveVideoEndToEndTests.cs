using System.Text.Json;
using System.Text.Json.Nodes;
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
/// The video gate's end-to-end walk (02.8.2-T1 plan §4 UC4): a live run whose ten checks all pass approves the
/// episode through the real <c>ApproveVideoCommand</c> — the same command a user's HTTP call sends — and the
/// approval, its audit rows, its event and the retention re-stamp are the production ones.
/// </summary>
/// <remarks>
/// <para>
/// The gate is resolved from the Api host's composition root in a scope pinned to the run's owner
/// (<see cref="ISystemContext.For"/>, 09 README D10), so the tenant filter, the command pipeline, the audit
/// writer and the outbox interceptor are all exercised. No fake sender stands in for the command: the only thing
/// this class fakes is the clock.
/// </para>
/// <para>
/// The rows are seeded with the builders and raw SQL (<c>PollRenderJobTests.SeedJobAsync</c> and
/// <c>AutopilotNotificationTests.SeedWorldAsync</c> precedents) and every assertion is scoped to this test's own
/// user, episode and run, because the class database is shared with the rest of the suite.
/// </para>
/// </remarks>
/// <param name="database">The class's own migrated database, from <see cref="IntegrationTest"/>.</param>
[Trait("Category", "Integration")]
public sealed class AutoApproveVideoEndToEndTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The audit action an accepted autopilot video approval writes.</summary>
    private const string AutoApprovedAction = "autopilot.video_auto_approved";

    /// <summary>The audit action the approve command itself writes.</summary>
    private const string EpisodeApprovedAction = "episode.video_approved";

    /// <summary>The event an accepted cut raises.</summary>
    private const string VideoApprovedEvent = "video_approved.v1";

    /// <summary>The audit action of one gate stop (02.8.2 plan §4 UC6).</summary>
    private const string OwnerActionAction = "autopilot.owner_action_required";

    /// <summary>The stored verdict of a story half that ran first, merged by the video write (plan §3.4).</summary>
    private const string StoryOnlyGateResults =
        """{"schema_version":"gates.v1","mode":"live","profile":"default","story":{"pass":true,"hold":false,"attempt":1,"checks":[{"id":"self_review_present","kind":"flag","value":1,"threshold":1,"pass":true,"detail":null}],"failures":[]}}""";

    /// <summary>The one instant this class's clock reads.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The SHA-256 the stored report and the stored asset share.</summary>
    private static readonly string ReportSha256 = new('c', 64);

    /// <summary>The ingest's own retention stamp, re-stamped by the approval (WT56).</summary>
    private static readonly DateTimeOffset IngestStamp = Start.AddDays(7);

    /// <summary>30 days (<c>Assets:RetentionDays</c>): the stamp an accepted cut carries.</summary>
    private static readonly DateTimeOffset ApprovedStamp = Start.AddDays(30);

    private static CancellationToken Ct => TestContextToken.Value;

    /// <summary>
    /// A live pass with all ten checks green approves the episode: it becomes <c>ready</c> with the command's own
    /// stamp, one autopilot audit row and one command audit row are written, the cut's event is queued, the live
    /// assets are re-stamped for the approved window, and the story half of the verdict stays untouched beside the
    /// video half (plan §3.4, §4 UC4).
    /// </summary>
    [Fact]
    public async Task AutoApproveVideo_EndToEnd_SetsReady()
    {
        var world = await SeedAsync(gateResults: StoryOnlyGateResults);
        using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        using var pin = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);

        var evaluated = await scope.ServiceProvider.GetRequiredService<IAutoApprovalGate>()
            .EvaluateVideoAsync(world.RunId, Ct);

        evaluated.IsError.ShouldBeFalse(string.Join(", ", evaluated.Errors.Select(error => error.Code)));
        evaluated.Value.Result.Pass.ShouldBeTrue(string.Join(", ", evaluated.Value.Result.Failures));
        evaluated.Value.AutoApproved.ShouldBeTrue("a live pass sends the real approve command");
        evaluated.Value.Reason.ShouldBeNull();

        // The episode is ready on the command's own clock, and the live assets carry the approved window.
        Scalar($"SELECT status || '|' || (ready_at = TIMESTAMPTZ '{Start:O}')::text FROM episodes WHERE id = '{world.EpisodeId}'")
            .ShouldBe("ready|true");
        Scalar($"SELECT (retention_until = TIMESTAMPTZ '{ApprovedStamp:O}')::text FROM assets WHERE id = '{world.AssetId}'")
            .ShouldBe("true", "the real command re-stamps the episode's live assets (WT56)");

        // Two audit rows: the gate's own approval, and the command's, as the pipeline writes it.
        AuditCount(world, AutoApprovedAction).ShouldBe(1);
        AuditCount(world, EpisodeApprovedAction).ShouldBe(1);
        Scalar($"SELECT entity_type || '|' || (entity_id = '{world.EpisodeId}')::text || '|' || actor_type "
            + $"FROM audit_logs WHERE action = '{AutoApprovedAction}' AND subject_user_id = '{world.UserId}'")
            .ShouldBe("episode|true|system");
        Scalar($"SELECT (data->>'runId' = '{world.RunId}')::text "
            + $"FROM audit_logs WHERE action = '{AutoApprovedAction}' AND subject_user_id = '{world.UserId}'")
            .ShouldBe("true");
        OutboxCount(world, VideoApprovedEvent).ShouldBe(1);

        // Nothing asked the owner for anything: the gate approved this cut by itself.
        AuditCount(world, OwnerActionAction).ShouldBe(0);

        // The verdict: one video block of ten passing checks beside the story block written earlier, unchanged.
        DocumentValue(world, "mode").ShouldBe("live");
        DocumentValue(world, "schema_version").ShouldBe(GateResultWriter.SchemaVersion);
        DocumentValue(world, "profile").ShouldBe(AutopilotGateOptions.DefaultProfileName);
        DocumentInner(world, "video", "pass").ShouldBe("true");
        Scalar($"SELECT jsonb_array_length(gate_results->'video'->'checks')::text FROM autopilot_runs WHERE id = '{world.RunId}'")
            .ShouldBe(VideoGateCheckIds.All.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Scalar($"SELECT jsonb_array_length(gate_results->'video'->'failures')::text FROM autopilot_runs WHERE id = '{world.RunId}'")
            .ShouldBe("0");

        DocumentInner(world, "story", "attempt").ShouldBe("1");
        DocumentInner(world, "story", "pass").ShouldBe("true");
        Scalar($"SELECT gate_results->'story'->'checks'->0->>'id' FROM autopilot_runs WHERE id = '{world.RunId}'")
            .ShouldBe("self_review_present");
        Scalar($"SELECT jsonb_array_length(gate_results->'story'->'checks')::text FROM autopilot_runs WHERE id = '{world.RunId}'")
            .ShouldBe("1", "the video write merges its half; it never rewrites the story half");
    }

    /// <summary>
    /// A cut the owner already accepted is not approved a second time: the gate records its verdict, treats the
    /// approval as already done and writes neither an audit row nor an event — a replay, exactly as the second call
    /// of the gate is (plan §4 UC4).
    /// </summary>
    [Fact]
    public async Task AutoApproveVideo_AfterTheOwnerApproved_ApprovesNothingTwice()
    {
        var world = await SeedAsync(episodeStatus: EpisodeStatus.Ready);
        using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        using var pin = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);

        var evaluated = await scope.ServiceProvider.GetRequiredService<IAutoApprovalGate>()
            .EvaluateVideoAsync(world.RunId, Ct);

        evaluated.IsError.ShouldBeFalse(string.Join(", ", evaluated.Errors.Select(error => error.Code)));
        evaluated.Value.Result.Pass.ShouldBeTrue(string.Join(", ", evaluated.Value.Result.Failures));
        evaluated.Value.AutoApproved.ShouldBeTrue("the episode is past this review for this render");
        evaluated.Value.Reason.ShouldBeNull();

        AuditCount(world, AutoApprovedAction).ShouldBe(0, "the audit row describes an approval this call did not make");
        AuditCount(world, EpisodeApprovedAction).ShouldBe(0);
        OutboxCount(world, VideoApprovedEvent).ShouldBe(0);
        AuditCount(world, OwnerActionAction).ShouldBe(0);

        // The verdict is still recorded: the run log shows what the gate read, whatever the episode's state was.
        DocumentValue(world, "mode").ShouldBe("live");
        DocumentInner(world, "video", "pass").ShouldBe("true");
    }

    /// <summary>The host of these tests: the real Api with the fixed clock and the gate live.</summary>
    /// <returns>The factory.</returns>
    private ApiFactory Factory() =>
        new ApiFactory(Database, new FakeClock(Start)).WithSetting("Autopilot:Gates:DryRun", "false");

    /// <summary>
    /// One owner whose episode carries a finished render and (optionally) a story verdict, ready for the video
    /// gate.
    /// </summary>
    /// <param name="episodeStatus">The episode's status.</param>
    /// <param name="gateResults">A pre-stored <c>gate_results</c> document, or <see langword="null"/> for none.</param>
    /// <returns>The seeded ids.</returns>
    private async Task<World> SeedAsync(
        EpisodeStatus episodeStatus = EpisodeStatus.VideoReview,
        string? gateResults = null)
    {
        var actor = new TestCurrentUser();
        var clock = new FakeClock(Start);
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        actor.Id = user.Id;

        var series = await new SeriesBuilder(Database, actor, clock).CreateAsync(
            user.Id,
            aspect: Aspect.Landscape,
            ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor, clock).CreateAsync(
            user.Id,
            series.Id,
            title: "Bản dựng tự duyệt",
            status: episodeStatus,
            ct: Ct);

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
            retentionUntil: IngestStamp,
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

        var submittedAt = Start.AddMinutes(-10);
        await Database.ExecuteAsync(
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, "
            + "percent, params, attempt, quality_report, submitted_at, started_at, finished_at, worst_case_usd) "
            + $"VALUES ('{renderJobId}', '{user.Id}', '{episode.Id}', '{version.Id}', '{Guid.CreateVersion7()}', "
            + $"'succeeded', 'done', 100, '{ParamsJson(version.Id)}'::jsonb, 1, '{ReportJson()}'::jsonb, "
            + $"'{submittedAt:O}', '{Start.AddMinutes(-9):O}', '{Start.AddMinutes(-5):O}', 0)",
            Ct);

        return new World(user.Id, episode.Id, runId, renderJobId, asset.Id);
    }

    /// <summary>One top-level member of the stored <c>gate_results</c> document, as text.</summary>
    /// <param name="world">The seeded world.</param>
    /// <param name="key">The top-level member's key, e.g. <c>mode</c>.</param>
    /// <returns>The stored value.</returns>
    private string DocumentValue(World world, string key) =>
        Scalar($"SELECT gate_results->>'{key}' FROM autopilot_runs WHERE id = '{world.RunId}'");

    /// <summary>One member of a nested block of the stored verdict, as text.</summary>
    /// <param name="world">The seeded world.</param>
    /// <param name="parent">The block's key, e.g. <c>video</c>.</param>
    /// <param name="key">The member's key, e.g. <c>pass</c>.</param>
    /// <returns>The stored value.</returns>
    private string DocumentInner(World world, string parent, string key) =>
        Scalar($"SELECT gate_results->'{parent}'->>'{key}' FROM autopilot_runs WHERE id = '{world.RunId}'");

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

    /// <summary>Runs one scalar text statement.</summary>
    /// <param name="sql">The statement.</param>
    /// <returns>The value (empty for null).</returns>
    private string Scalar(string sql) => SqlProbe.ScalarText(Database, sql);

    /// <summary>A complete <c>render-params.v1</c> document (05 D11): every member is <c>[JsonRequired]</c>.</summary>
    /// <param name="storyVersionId">The version the params name.</param>
    /// <returns>The serialised document.</returns>
    private static string ParamsJson(Guid storyVersionId) =>
        JsonSerializer.Serialize(new RenderParamsV1(
            RenderParamsV1.CurrentMapperVersion,
            storyVersionId,
            "landscape",
            "vi",
            "kokoro:test",
            Guid.CreateVersion7(),
            "style=test@0123abcd;neg=v1",
            new ImagePlanSummary(3, 1, 2, 1, 0, [], ["paragraph:1"], "style=test@0123abcd;neg=v1"),
            new SubtitlesParams(true, "landscape@1"),
            null,
            3));

    /// <summary>
    /// The stored <c>render-quality.v1</c> document (05 README D10) of a render that passed every blocking check,
    /// written through the report type itself so a renamed member fails this test instead of degrading silently.
    /// </summary>
    /// <returns>The serialised document.</returns>
    private static string ReportJson() =>
        JsonSerializer.Serialize(new RenderQualityReportV1
        {
            Pass = true,
            PolicyVersion = 1,
            CheckToolVersion = "render_check.py@1",
            DurationSec = 300.0,
            TtsDurationSec = 301.37,
            IntegratedLufs = -15.0,
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

    /// <summary>
    /// The smallest clean story document the version factory accepts (the <c>PollRenderJobTests</c> shape); the
    /// gate never reads it, the row exists so <c>render_jobs.story_version_id</c> holds.
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

    /// <summary>The ids of one seeded world.</summary>
    /// <param name="UserId">The run's owner.</param>
    /// <param name="EpisodeId">The episode under review.</param>
    /// <param name="RunId">The autopilot run being gated.</param>
    /// <param name="RenderJobId">The finished render job.</param>
    /// <param name="AssetId">The episode's final video asset.</param>
    private sealed record World(
        Guid UserId,
        Guid EpisodeId,
        Guid RunId,
        Guid RenderJobId,
        Guid AssetId);
}
