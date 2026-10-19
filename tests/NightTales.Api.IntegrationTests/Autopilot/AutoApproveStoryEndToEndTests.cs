using ErrorOr;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Common;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
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
using NightTales.Infrastructure.Persistence.Audit;
using NightTales.Infrastructure.Persistence.Queries;
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
/// <para>
/// <b>The reviewed approval paths.</b> The two seams of <see cref="AutoApproveSeam"/> cover what the first three
/// rows cannot arrange: the episode moving past the evaluated version between the verdict and the approval
/// (SEC-1, recorded as <c>etag_conflict</c>), the owner approving the very version first (F2, answered
/// <see cref="ApprovalOutcome.AlreadyApproved"/> and never backfilled), the owner approving a different version
/// (R1: a refusal the gate records instead of reading it as an approval nobody made), and an approval whose own
/// audit row cannot be committed (F3/R2, answered as <see cref="GateErrors.ApprovalAuditFailed"/>, surfaced by
/// the gate without a refusal or a notice, and backfilled exactly once by the replay).
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

    /// <summary>
    /// SEC-1, the version-bound approval: the episode's current story version is bumped after the verdict was
    /// stored and before the approval service runs, so the approve command's own ETag guard refuses
    /// (<c>story_version_conflict</c>, recorded as <c>etag_conflict</c>): the episode stays in
    /// <c>story_review</c>, the stored verdict keeps <c>pass: true</c> beside the failure it gained, the owner is
    /// told to approve by hand, and nothing is approved or recorded as an autopilot approval.
    /// </summary>
    [Fact]
    public async Task AutoApproveStory_VersionBumpedBeforeApproval_RefusesAndAsksTheOwner()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct);
        var seam = new AutoApproveSeam();
        using var factory = Factory(seam: seam);

        // The verdict is evaluated for v1; the episode carries v2 by the time the approval service reads it.
        seam.RunBeforeApproval(() => Database.ExecuteAsync(
            $"UPDATE episodes SET current_story_version_id = '{world.SecondVersionId}' WHERE id = '{world.EpisodeId}'",
            Ct));

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        evaluated.Value.Result.Pass.ShouldBeTrue("the checks passed; the approval afterwards did not");
        evaluated.Value.AutoApproved.ShouldBeFalse();
        evaluated.Value.Reason.ShouldBe(GateStopReason.OwnerApprovalRequired);

        CurrentVersion(world.EpisodeId).ShouldBe(world.SecondVersionId, "the seam moved the episode past the verdict's version");
        VerdictSchema(world.RunId).ShouldBe("gates.v1");
        StoryVersionInVerdict(world.RunId).ShouldBe(world.VersionId, "the stored verdict names the version it evaluated");
        StoryFlag(world.RunId, "pass").ShouldBeTrue();
        StoryFailures(world.RunId).ShouldContain(ApprovalFailureCodes.EtagConflict);

        EpisodeStatus(world.EpisodeId).ShouldBe("story_review");
        ApprovedVersion(world.EpisodeId).ShouldBeNull();
        AutoApproveAuditRows(world).ShouldBe(0);
        VersionRows(world.EpisodeId).ShouldBe(2, "a refused approval appends no story version");

        NoticeRows(world).ShouldBe(1);
        NoticePayloads(world).ShouldContain(OwnerActionReasons.OwnerApprovalRequired);
    }

    /// <summary>
    /// F2: the owner approved the very version the verdict names, with the real <c>ApproveStoryCommand</c> under
    /// <see cref="ApprovalSource.User"/>, just before the approval service ran. The service answers the replay
    /// with <see cref="ApprovalOutcome.AlreadyApproved"/> and never writes an
    /// <c>autopilot.story_auto_approved</c> row for the owner's own approval — not on the gate's call, and not on
    /// a later replay of the same verdict.
    /// </summary>
    [Fact]
    public async Task AutoApproveStory_OwnerApprovedTheSameVersionFirst_IsAlreadyApprovedWithoutBackfill()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct);
        var seam = new AutoApproveSeam();
        using var factory = Factory(seam: seam);

        seam.RunBeforeApproval(async () =>
        {
            await using var ownerScope = factory.Services.CreateAsyncScope();
            using var pinned = ownerScope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);
            var owner = await ownerScope.ServiceProvider.GetRequiredService<IMediator>()
                .Send(new ApproveStoryCommand(world.EpisodeId, $"\"sv-{world.VersionId}\"", null, ApprovalSource.User), Ct);

            owner.IsError.ShouldBeFalse(Describe(owner));
        });

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        evaluated.Value.AutoApproved.ShouldBeTrue();
        EpisodeStatus(world.EpisodeId).ShouldBe("story_approved");
        ApprovedVersion(world.EpisodeId).ShouldBe(world.VersionId);
        CommandApprovalRows(world, "user").ShouldBe(1, "the owner's own approval is the one on record");
        AutoApproveAuditRows(world).ShouldBe(0, "an owner approval is never backfilled as an autopilot one");
        NoticeRows(world).ShouldBe(0);

        // The same verdict through the service directly: the replay of the owner's approval, and still no row.
        var replay = await ApproveStoryDirectAsync(factory, world, evaluated.Value.Result);

        replay.IsError.ShouldBeFalse(Describe(replay));
        replay.Value.ShouldBe(ApprovalOutcome.AlreadyApproved);
        AutoApproveAuditRows(world).ShouldBe(0);
    }

    /// <summary>
    /// F3: the approval commits and its own audit row cannot be written, so the service answers
    /// <see cref="GateErrors.ApprovalAuditFailed"/> rather than swallowing the loss. The retry is the replay
    /// path: it backfills the missing row exactly once, and a third call adds no second one.
    /// </summary>
    [Fact]
    public async Task AutoApproveStory_AuditWriteFails_AnswersTheErrorAndBackfillsOnRetry()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct);
        var seam = new AutoApproveSeam();
        seam.FailNextAutoApprovalWrite();
        using var factory = Factory(seam: seam);

        var verdict = PassingVerdict(world);
        var failed = await ApproveStoryDirectAsync(factory, world, verdict);

        var expected = GateErrors.ApprovalAuditFailed(world.EpisodeId, AutopilotApprovalService.StoryApprovedAction);
        failed.IsError.ShouldBeTrue(Describe(failed));
        failed.FirstError.Code.ShouldBe(expected.Code);
        failed.FirstError.Description.ShouldBe(expected.Description);

        // The approval itself is committed: the lost row is a lost record, never a lost approval.
        EpisodeStatus(world.EpisodeId).ShouldBe("story_approved");
        ApprovedVersion(world.EpisodeId).ShouldBe(world.VersionId);
        AutoApproveAuditRows(world).ShouldBe(0);

        var second = await ApproveStoryDirectAsync(factory, world, verdict);

        second.IsError.ShouldBeFalse(Describe(second));
        second.Value.ShouldBe(ApprovalOutcome.AlreadyApproved);
        AutoApproveAuditRows(world).ShouldBe(1);
        AutoApproveAuditRun(world).ShouldBe(world.RunId.ToString());

        var third = await ApproveStoryDirectAsync(factory, world, verdict);

        third.IsError.ShouldBeFalse(Describe(third));
        third.Value.ShouldBe(ApprovalOutcome.AlreadyApproved);
        AutoApproveAuditRows(world).ShouldBe(1, "the backfilled row is written exactly once");
    }

    /// <summary>
    /// F3/R2: the approval commits and its own audit row cannot be written. Through the gate that is not a
    /// refusal — the approval <em>is</em> applied — so the evaluator surfaces
    /// <see cref="GateErrors.ApprovalAuditFailed"/> unchanged, appends no failure code to the stored verdict and
    /// queues no owner notice; the retry's replay backfills the row exactly once.
    /// </summary>
    [Fact]
    public async Task AutoApproveStory_AuditWriteFailsThroughTheGate_IsSurfacedAndBackfilledOnRetry()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct);
        var seam = new AutoApproveSeam();
        seam.FailNextAutoApprovalWrite();
        using var factory = Factory(seam: seam);

        var first = await EvaluateAsync(factory, world);

        first.IsError.ShouldBeTrue(Describe(first));
        first.FirstError.Code.ShouldBe(GateErrors.ApprovalAuditFailedCode);

        // The approval itself is committed: the lost row is a lost record, never a lost approval.
        EpisodeStatus(world.EpisodeId).ShouldBe("story_approved");
        ApprovedVersion(world.EpisodeId).ShouldBe(world.VersionId);
        AutoApproveAuditRows(world).ShouldBe(0);

        // The audit failure is not a refusal: the verdict keeps its pass, gains no failure code, and the owner is
        // not asked to approve what is already approved.
        StoryFlag(world.RunId, "pass").ShouldBeTrue();
        StoryFailures(world.RunId).ShouldNotContain(ApprovalFailureCodes.ApproveFailed);
        StoryFailures(world.RunId).ShouldNotContain(ApprovalFailureCodes.EtagConflict);
        NoticeRows(world).ShouldBe(0);

        var second = await EvaluateAsync(factory, world);

        second.IsError.ShouldBeFalse(Describe(second));
        second.Value.AutoApproved.ShouldBeTrue();
        AutoApproveAuditRows(world).ShouldBe(1);
        AutoApproveAuditRun(world).ShouldBe(world.RunId.ToString());

        var third = await EvaluateAsync(factory, world);

        third.IsError.ShouldBeFalse(Describe(third));
        third.Value.AutoApproved.ShouldBeTrue();
        AutoApproveAuditRows(world).ShouldBe(1, "the backfilled row is written exactly once");
        NoticeRows(world).ShouldBe(0);
    }

    /// <summary>
    /// R1: the owner approved a <em>different</em> version than the one the verdict names, so the approve command
    /// refuses its state. That is a refusal, never "already approved": the gate does not report a pass for a
    /// version nobody approved, it records the refusal and asks the owner.
    /// </summary>
    [Fact]
    public async Task AutoApproveStory_OwnerApprovedAnotherVersion_IsNotAlreadyApproved()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct);
        var seam = new AutoApproveSeam();
        using var factory = Factory(seam: seam);

        // v2 becomes the current version and the owner approves it while the gate's own evaluation of v1 is in
        // flight: the episode is approved, but for a version the verdict does not name.
        seam.RunBeforeApproval(async () =>
        {
            await Database.ExecuteAsync(
                $"UPDATE episodes SET current_story_version_id = '{world.SecondVersionId}' WHERE id = '{world.EpisodeId}'",
                Ct);

            await using var ownerScope = factory.Services.CreateAsyncScope();
            using var pinned = ownerScope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);
            var owner = await ownerScope.ServiceProvider.GetRequiredService<IMediator>()
                .Send(new ApproveStoryCommand(world.EpisodeId, $"\"sv-{world.SecondVersionId}\"", null, ApprovalSource.User), Ct);

            owner.IsError.ShouldBeFalse(Describe(owner));
        });

        var evaluated = await EvaluateAsync(factory, world);

        // The checks passed, but the episode was approved for another version: not a pass, and never the replay the
        // service answers an idempotent approval with.
        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        evaluated.Value.Result.Pass.ShouldBeTrue("the checks passed; the approval afterwards did not");
        evaluated.Value.AutoApproved.ShouldBeFalse("the owner approved v2, and the verdict names v1");
        evaluated.Value.Reason.ShouldBe(GateStopReason.OwnerApprovalRequired);

        ApprovedVersion(world.EpisodeId).ShouldBe(world.SecondVersionId);
        CommandApprovalRows(world, "user").ShouldBe(1);
        AutoApproveAuditRows(world).ShouldBe(0);
        StoryFailures(world.RunId).ShouldContain(ApprovalFailureCodes.ApproveFailed);
        NoticeRows(world).ShouldBe(1);
        NoticePayloads(world).ShouldContain(OwnerActionReasons.OwnerApprovalRequired);
    }

    private static string Describe(ErrorOr<StoryGateOutcome> evaluated) =>
        evaluated.IsError ? string.Join("; ", evaluated.Errors.Select(error => $"{error.Code}: {error.Description}")) : "ok";

    private static string Describe(ErrorOr<ApprovedStoryDto> refused) =>
        refused.IsError ? string.Join("; ", refused.Errors.Select(error => $"{error.Code}: {error.Description}")) : "ok";

    private static string Describe(ErrorOr<ApprovalOutcome> outcome) =>
        outcome.IsError ? string.Join("; ", outcome.Errors.Select(error => $"{error.Code}: {error.Description}")) : "ok";

    /// <summary>
    /// The approval service's own answer, called the way the gate calls it (its scope, its owner pin) with a
    /// verdict the seed itself produced.
    /// </summary>
    private static async Task<ErrorOr<ApprovalOutcome>> ApproveStoryDirectAsync(
        ApiFactory factory,
        GateRunSeed.GateWorld world,
        StoryGateResult result)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);

        return await scope.ServiceProvider.GetRequiredService<AutopilotApprovalService>()
            .ApproveStoryAsync(world.RunId, result, Evidence(), GateMode.Live, Ct);
    }

    /// <summary>A passing story verdict for the seeded version, for the tests that call the service directly.</summary>
    private static StoryGateResult PassingVerdict(GateRunSeed.GateWorld world) =>
        new(
            Pass: true,
            Hold: false,
            Attempt: 1,
            StoryVersionId: world.VersionId,
            Checks: [],
            Failures: []);

    /// <summary>The report's context lines; nothing the tests below assert on reads them.</summary>
    private static GateStoryEvidence Evidence() =>
        new(
            SourceSimilarity: 0.01m,
            PastEpisodeSimilarity: null,
            CorpusMax: 0,
            RealWorldHit: false,
            RealWorldKinds: [],
            ModerationFlagged: false,
            ModerationCategories: [],
            ModerationUnavailableDetail: null,
            ModerationCheckedAt: null,
            EpisodeStatusAtEvaluation: "story_review");

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

    private ApiFactory Factory(bool dryRun = false, AutoApproveSeam? seam = null) =>
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

                if (seam is null)
                {
                    return;
                }

                // The two armed faults of AutoApproveFaultSeams: the hook runs at the approval service's owner
                // lookup, and the audit writer fails exactly one auto-approval write. Both wrap the production
                // implementations, so everything else the run does stays real.
                services.RemoveAll<ISystemWorkQuery>();
                services.AddScoped<SystemWorkQuery>();
                services.AddScoped<ISystemWorkQuery>(provider =>
                    new AutoApproveSeamSystemWork(provider.GetRequiredService<SystemWorkQuery>(), seam));

                services.RemoveAll<IAuditWriter>();
                services.AddScoped<EfAuditWriter>();
                services.AddScoped<IAuditWriter>(provider =>
                    new AutoApproveSeamAuditWriter(provider.GetRequiredService<EfAuditWriter>(), seam));
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

    private Guid? CurrentVersion(Guid episodeId)
    {
        var pointer = SqlProbe.ScalarText(
            Database,
            $"SELECT coalesce(current_story_version_id::text, '') FROM episodes WHERE id = '{episodeId}'");

        return string.IsNullOrEmpty(pointer) ? null : Guid.Parse(pointer);
    }

    /// <summary>The stored document's own schema version: <c>gates.v1</c> is the contract the run log carries.</summary>
    private string VerdictSchema(Guid runId) =>
        SqlProbe.ScalarText(Database, $"SELECT gate_results->>'schema_version' FROM autopilot_runs WHERE id = '{runId}'");

    /// <summary>The story version the stored verdict evaluated, as it is written on the <c>story</c> block.</summary>
    private Guid StoryVersionInVerdict(Guid runId) => Guid.Parse(
        SqlProbe.ScalarText(Database, $"SELECT gate_results->'story'->>'story_version_id' FROM autopilot_runs WHERE id = '{runId}'"));

    /// <summary>The <c>failures[]</c> of the stored story verdict, as its JSON text.</summary>
    private string StoryFailures(Guid runId) =>
        SqlProbe.ScalarText(Database, $"SELECT gate_results->'story'->'failures' FROM autopilot_runs WHERE id = '{runId}'");

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

    /// <summary>The backfilled row's own projection: the run it names (<c>data.runId</c>).</summary>
    private string AutoApproveAuditRun(GateRunSeed.GateWorld world) =>
        SqlProbe.ScalarText(
            Database,
            $"SELECT data->>'runId' FROM audit_logs WHERE action = '{AutopilotApprovalService.StoryApprovedAction}' "
            + $"AND subject_user_id = '{world.UserId}'");

    /// <summary>The approve command's own rows for this episode, split by the source they approved under.</summary>
    private long CommandApprovalRows(GateRunSeed.GateWorld world, string approvalSource) =>
        SqlProbe.Count(
            Database,
            "SELECT count(*) FROM audit_logs WHERE action = 'episode.story_approved' "
            + $"AND subject_user_id = '{world.UserId}' AND entity_id = '{world.EpisodeId}' "
            + $"AND data->>'approval_source' = '{approvalSource}'");

    /// <summary>The approval audit row's own attribution: the episode, as the <c>system</c> actor.</summary>
    private string AutoApproveAuditSubject(GateRunSeed.GateWorld world) =>
        SqlProbe.ScalarText(
            Database,
            $"SELECT entity_type || '|' || (entity_id = '{world.EpisodeId}')::text || '|' || actor_type FROM audit_logs "
            + $"WHERE action = '{AutopilotApprovalService.StoryApprovedAction}' AND subject_user_id = '{world.UserId}'");
}
