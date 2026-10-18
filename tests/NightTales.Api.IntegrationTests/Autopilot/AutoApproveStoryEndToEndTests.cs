using ErrorOr;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Common;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Application.Features.Stories.Commands.ApproveStory;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Gates;
using NightTales.Domain.Stories;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Email;
using NightTales.TestKit.Moderation;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Autopilot;

/// <summary>
/// The story gate end to end (02.8.2-T1 task file, "Tests"): a live pass approves through the real
/// <c>ApproveStoryCommand</c> as a system actor, a second call is a replay, and a true-crime story is held for
/// the owner instead of approved — including the case where a caller asks for the approval itself with
/// <see cref="ApprovalSource.Autopilot"/> on a true-crime episode.
/// </summary>
/// <remarks>
/// <para>
/// <b>No fake sender.</b> The gate resolves <c>ApproveStoryCommand</c> through the host's own pipeline, so the
/// episode, its <c>approved_story_version_id</c>, the <c>autopilot.story_auto_approved</c> audit row and the
/// History come from production code. Assertions read the database with SQL, and the D19/D22 sweep proves no
/// story text, brief or source URL was stored anywhere the gate wrote.
/// </para>
/// <para>
/// <b>Deviation from the task file's test name, and a production gap.</b> The file names the fourth test
/// <c>AutoApproveStory_AutopilotSourceOnTrueCrime_ReturnsEpisodeInvalidState</c>. The plan's Autopilot+true-crime
/// guard is <b>not implemented</b>: <c>Episode.ApproveStory</c> (Episode.Story.cs:307-311) checks only the status
/// and the current version, and the handler carries <see cref="ApprovalSource"/> into the audit only
/// (ApproveStoryCommand.cs:33). What refuses today is <c>StoryApprovalGate</c>'s fact-sheet requirement
/// (StoryApprovalGate.cs:70,213). The test therefore asserts the one code the pipeline really produces and names
/// the missing guard here and as an [OPEN] gap in this agent's report.
/// </para>
/// </remarks>
/// <param name="database">The class's own migrated database, from <see cref="IntegrationTest"/>.</param>
public sealed class AutoApproveStoryEndToEndTests(TestDatabase database) : IntegrationTest(database)
{
    private const string NoticeType = "autopilot_owner_action_required.v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A live pass approves the story through the real command: the episode is <c>story_approved</c> on the version
    /// the gate evaluated, exactly one auto-approval audit row exists, the stored verdict has all 15 checks and the
    /// owner is not asked for anything.
    /// </summary>
    [Fact]
    public async Task AutoApproveStory_EndToEnd_SetsStoryApprovedAndWritesAudit()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct);
        using var factory = Factory();

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        evaluated.Value.Result.Pass.ShouldBeTrue();
        evaluated.Value.AutoApproved.ShouldBeTrue();

        EpisodeStatus(world.EpisodeId).ShouldBe("story_approved");
        ApprovedVersion(world.EpisodeId).ShouldBe(world.VersionId);
        // A fiction verdict carries 13 checks; the two true-crime ones are the task file's "15 checks".
        CheckCount(world.RunId).ShouldBe(13);
        StoryFlag(world.RunId, "pass").ShouldBeTrue();

        AutoApproveAuditRows(world).ShouldBe(1);
        AutoApproveAuditSubject(world).ShouldBe("episode|true|system");
        NoticeRows(world).ShouldBe(0);

        AssertNoStoryText(world);
    }

    /// <summary>
    /// Evaluating the same run twice is a replay of an approval that already happened: no second audit row, no
    /// appended story version, and the outcome still reports the auto-approval.
    /// </summary>
    [Fact]
    public async Task AutoApproveStory_SecondCall_IsIdempotent()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct);
        using var factory = Factory();

        (await EvaluateAsync(factory, world)).Value.AutoApproved.ShouldBeTrue();
        var versionsAfterFirst = VersionRows(world.EpisodeId);

        var second = await EvaluateAsync(factory, world);

        second.IsError.ShouldBeFalse(Describe(second));
        second.Value.AutoApproved.ShouldBeTrue();
        AutoApproveAuditRows(world).ShouldBe(1);
        VersionRows(world.EpisodeId).ShouldBe(versionsAfterFirst);
        ApprovedVersion(world.EpisodeId).ShouldBe(world.VersionId);
        EpisodeStatus(world.EpisodeId).ShouldBe("story_approved");
        NoticeRows(world).ShouldBe(0);
    }

    /// <summary>
    /// A true-crime story is held for the owner: the episode stays in <c>story_review</c>, the verdict carries the
    /// 15 checks including the two true-crime-only ones, the notice names the hold, and nothing is approved or
    /// written to the audit log.
    /// </summary>
    [Fact]
    public async Task AutoApproveStory_TrueCrime_LeavesStoryInReview()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct, topic: Topic.TrueCrime);
        using var factory = Factory();

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        evaluated.Value.Result.Hold.ShouldBeTrue();
        evaluated.Value.AutoApproved.ShouldBeFalse();
        evaluated.Value.Reason.ShouldBe(GateStopReason.Hold);

        CheckCount(world.RunId).ShouldBe(15);
        StoryFlag(world.RunId, "hold").ShouldBeTrue();
        EpisodeStatus(world.EpisodeId).ShouldBe("story_review");
        ApprovedVersion(world.EpisodeId).ShouldBeNull();

        NoticeRows(world).ShouldBe(1);
        NoticePayloads(world).ShouldContain(OwnerActionReasons.TrueCrimeHold);
        NoticeAuditRows(world, OwnerActionReasons.TrueCrimeHold).ShouldBe(1);
        AutoApproveAuditRows(world).ShouldBe(0);

        AssertNoStoryText(world);
    }

    /// <summary>
    /// The approval itself may not be asked for on a true-crime story: sending <c>ApproveStoryCommand</c> with
    /// <see cref="ApprovalSource.Autopilot"/> is refused, the episode stays in review, and no auto-approval audit
    /// row appears.
    /// </summary>
    [Fact]
    public async Task AutoApproveStory_AutopilotSourceOnTrueCrime_ReturnsEpisodeInvalidState()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct, topic: Topic.TrueCrime);
        using var factory = Factory();

        var refused = await SendApproveStoryAsync(factory, world);

        // The handler's own guard (defence in depth behind gate check 15) answers before the fact-sheet and
        // attestation stages, with the one code the plan names (§8).
        refused.IsError.ShouldBeTrue(Describe(refused));
        refused.FirstError.Code.ShouldBe(ErrorCodes.EpisodeInvalidState);

        EpisodeStatus(world.EpisodeId).ShouldBe("story_review");
        ApprovedVersion(world.EpisodeId).ShouldBeNull();
        AutoApproveAuditRows(world).ShouldBe(0);
    }

    private static string Describe(ErrorOr<StoryGateOutcome> evaluated) =>
        evaluated.IsError ? string.Join("; ", evaluated.Errors.Select(error => $"{error.Code}: {error.Description}")) : "ok";

    private static string Describe(ErrorOr<ApprovedStoryDto> refused) =>
        refused.IsError ? string.Join("; ", refused.Errors.Select(error => $"{error.Code}: {error.Description}")) : "ok";

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

    private static async Task<ErrorOr<ApprovedStoryDto>> SendApproveStoryAsync(ApiFactory factory, GateRunSeed.GateWorld world)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);
        var sender = scope.ServiceProvider.GetRequiredService<IMediator>();

        return await sender.Send(
            new ApproveStoryCommand(world.EpisodeId, $"\"sv-{world.VersionId}\"", null, ApprovalSource.Autopilot),
            Ct);
    }

    private ApiFactory Factory(bool dryRun = false) =>
        new ApiFactory(Database, new FakeClock(GateRunSeed.Start))
            .WithSetting("Autopilot:Gates:DryRun", dryRun ? "true" : "false")
            .WithSetting("Similarity:Corpus:MaxPastEpisodes", "50")
            .WithServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddScoped<IEmailSender>(provider => new FakeEmailSender(
                    new FakeEmailSenderState(),
                    (AppDbContext)provider.GetRequiredService<IAppDbContext>(),
                    provider.GetRequiredService<IClock>()));

                services.RemoveAll<IContentModerator>();
                services.AddSingleton<IContentModerator>(new FakeContentModerator());
            });

    /// <summary>
    /// The D19/D22 sweep over everything the gate wrote for this episode: the runs' verdicts, the episode's audit
    /// rows and its notices carry neither the story marker nor the seeded source URL.
    /// </summary>
    private void AssertNoStoryText(GateRunSeed.GateWorld world)
    {
        var verdicts = SqlProbe.QueryStrings(
            Database,
            $"SELECT coalesce(gate_results::text, '') FROM autopilot_runs WHERE episode_id = '{world.EpisodeId}'");
        var audits = SqlProbe.QueryStrings(
            Database,
            $"SELECT coalesce(data::text, '') FROM audit_logs WHERE subject_user_id = '{world.UserId}'");

        var stored = string.Join('\n', new[] { verdicts, audits, new List<string> { NoticePayloads(world) } }
            .SelectMany(rows => rows));

        stored.ShouldNotContain(GateRunSeed.StoryMarker);
        stored.ShouldNotContain(GateRunSeed.SourceUrlText);
    }

    private string EpisodeStatus(Guid episodeId) =>
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{episodeId}'");

    private Guid? ApprovedVersion(Guid episodeId)
    {
        var pointer = SqlProbe.ScalarText(
            Database,
            $"SELECT coalesce(approved_story_version_id::text, '') FROM episodes WHERE id = '{episodeId}'");

        return string.IsNullOrEmpty(pointer) ? null : Guid.Parse(pointer);
    }

    private long VersionRows(Guid episodeId) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM story_versions WHERE episode_id = '{episodeId}'");

    private int CheckCount(Guid runId) =>
        int.Parse(
            SqlProbe.ScalarText(
                Database,
                $"SELECT coalesce(jsonb_array_length(gate_results -> 'story' -> 'checks'), 0)::text FROM autopilot_runs WHERE id = '{runId}'"),
            System.Globalization.CultureInfo.InvariantCulture);

    private bool StoryFlag(Guid runId, string key) =>
        string.Equals(
            SqlProbe.ScalarText(Database, $"SELECT gate_results -> 'story' ->> '{key}' FROM autopilot_runs WHERE id = '{runId}'"),
            "true",
            StringComparison.Ordinal);

    /// <summary>The notice rows this world queued: <c>type</c> plus the owner, proven scoping.</summary>
    private long NoticeRows(GateRunSeed.GateWorld world) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM outbox_messages WHERE type = '{NoticeType}' AND user_id = '{world.UserId}'");

    /// <summary>The notice payloads themselves, so the reason they carry is asserted and not just counted.</summary>
    private string NoticePayloads(GateRunSeed.GateWorld world) =>
        string.Join(
            '\n',
            SqlProbe.QueryStrings(
                Database,
                $"SELECT payload::text FROM outbox_messages WHERE type = '{NoticeType}' AND user_id = '{world.UserId}'"));

    /// <summary>The gate's own notice audit row for this run and reason.</summary>
    private long NoticeAuditRows(GateRunSeed.GateWorld world, string reason) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM audit_logs WHERE action = '{AutopilotNotificationService.AuditAction}' "
            + $"AND data->>'runId' = '{world.RunId}' AND data->>'reason' = '{reason}'");

    private long AutoApproveAuditRows(GateRunSeed.GateWorld world) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM audit_logs WHERE action = '{AutopilotApprovalService.StoryApprovedAction}' "
            + $"AND subject_user_id = '{world.UserId}'");

    /// <summary>The approval audit row's own attribution: the episode, as the <c>system</c> actor.</summary>
    private string AutoApproveAuditSubject(GateRunSeed.GateWorld world) =>
        SqlProbe.ScalarText(
            Database,
            $"SELECT entity_type || '|' || (entity_id = '{world.EpisodeId}')::text || '|' || actor_type FROM audit_logs "
            + $"WHERE action = '{AutopilotApprovalService.StoryApprovedAction}' AND subject_user_id = '{world.UserId}'");
}
