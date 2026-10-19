using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Similarity;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Gates;
using NightTales.Domain.Stories.Similarity;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Queries;
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
/// The story half of the gate engine over real rows (02.8.2-T1 plan §4 UC1–UC3, UC5; task file "Tests"):
/// dry run, a failing check, an unreachable moderator, a revised story, an unreadable corpus, an approval the
/// approve command refused, and two failing runs in a row.
/// </summary>
/// <remarks>
/// <para>
/// <b>The real host, the real evaluator.</b> Every case drives <see cref="IAutoApprovalGate.EvaluateStoryAsync"/>
/// from a scope pinned to the run's owner (<see cref="ISystemContext"/>, D10/D13) and reads the stored verdict with
/// SQL: the assertion is about what production wrote, not about a fake's log. Only the two dependencies this case
/// is about are replaced (<see cref="IContentModerator"/>, <see cref="ISimilarityCorpusProvider"/>), and the e-mail
/// queue is replaced so a missing template cannot turn a notice assertion into a failure.
/// </para>
/// <para>
/// <b>The absent domain guard.</b> The plan's "autopilot may not approve a true-crime story"
/// (<c>episode_invalid_state</c>) is asserted in <see cref="AutoApproveStoryEndToEndTests"/> through the real
/// <c>ApproveStoryCommand</c>, where the guard that actually fires today is the fact-sheet requirement.
/// </para>
/// </remarks>
/// <param name="database">The class's own migrated database, from <see cref="IntegrationTest"/>.</param>
public sealed class StoryGateEvaluatorTests(TestDatabase database) : IntegrationTest(database)
{
    private const string NoticeType = "autopilot_owner_action_required.v1";

    private static readonly string[] ExpectedFailingChecks = [StoryGateCheckIds.SelfReviewMeanScore];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A dry run records the full passing verdict (13 checks for a fiction series, <c>mode = dry_run</c>) and it
    /// approves nothing: the episode stays in review, no auto-approval audit row exists, and no story text, brief
    /// or source URL reaches the verdict or the notice (D19/D22).
    /// </summary>
    [Fact]
    public async Task StoryGateEvaluator_DryRun_RecordsVerdictAndNotifiesTheOwner()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct);
        using var factory = Factory();

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        evaluated.Value.Result.Pass.ShouldBeTrue();
        evaluated.Value.AutoApproved.ShouldBeFalse();
        evaluated.Value.Reason.ShouldBe(GateStopReason.DryRun);

        // 13 checks: 14 (fact_sheet_current) and 15 (true_crime_hold) run only for a true-crime series.
        CheckCount(world.RunId).ShouldBe(13);
        Json(world.RunId, "mode").ShouldBe("dry_run");
        Json(world.RunId, "profile").ShouldNotBeNullOrWhiteSpace();
        Json(world.RunId, "schema_version").ShouldBe(GateResultWriter.SchemaVersion);
        StoryFlag(world.RunId, "pass").ShouldBeTrue();

        NoticeRows(world).ShouldBe(1);
        NoticePayloads(world).ShouldContain(OwnerActionReasons.DryRun);
        NoticeAuditRows(world, OwnerActionReasons.DryRun).ShouldBe(1);
        StoredStatus(world.EpisodeId).ShouldBe("story_review");
        AutoApproveAuditRows(world).ShouldBe(0);

        AssertNoStoryText(world);
    }

    /// <summary>
    /// One failing check (the mean review score, 3.0 against the 3.5 floor) is recorded as the verdict's only
    /// failure, the run's verdict carries it, and the owner is told which checks failed.
    /// </summary>
    [Fact]
    public async Task StoryGateEvaluator_FailingCheck_RecordsFailedCheckIdsAndNotifiesTheOwner()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct, auditReport: GateRunSeed.FailingAudit());
        using var factory = Factory();

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        evaluated.Value.Result.Pass.ShouldBeFalse();
        evaluated.Value.AutoApproved.ShouldBeFalse();
        evaluated.Value.Reason.ShouldBe(GateStopReason.FailedChecks);

        CheckCount(world.RunId).ShouldBe(13);
        StoryFlag(world.RunId, "pass").ShouldBeFalse();
        FailedCheckIds(world.RunId).ShouldBe(ExpectedFailingChecks);
        StoryFailures(world.RunId).ShouldContain(StoryGateCheckIds.SelfReviewMeanScore);

        NoticeRows(world).ShouldBe(1);
        NoticePayloads(world).ShouldContain(OwnerActionReasons.FailedChecks);
        NoticePayloads(world).ShouldContain(StoryGateCheckIds.SelfReviewMeanScore);
        NoticeAuditRows(world, OwnerActionReasons.FailedChecks).ShouldBe(1);
        NoticeFailureCodes(world).ShouldContain(StoryGateCheckIds.SelfReviewMeanScore);
        AutoApproveAuditRows(world).ShouldBe(0);

        AssertNoStoryText(world);
    }

    /// <summary>
    /// A story with no usable draft-time verdict is moderated by the gate itself; a moderator that is down fails
    /// check <c>moderation_gate</c> closed, and the owner is told the moderator was unreachable.
    /// </summary>
    [Fact]
    public async Task StoryGateEvaluator_ModeratorUnavailable_RecordsTheFailureAndNotifiesTheOwner()
    {
        // The stored report carries no moderation block at all: the draft-time verdict is missing, so the gate
        // calls the moderator itself (decision 1). That call is the one that fails.
        var world = await GateRunSeed.SeedRunAsync(
            Database,
            ct: Ct,
            auditReport: GateRunSeed.AuditReport(GateRunSeed.ReviewJson(5), moderation: null));
        var moderator = new FakeContentModerator().EnqueueUnavailable(ModerationStage.AutopilotGate);
        using var factory = Factory(moderator: moderator);

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        evaluated.Value.Result.Pass.ShouldBeFalse();
        evaluated.Value.Reason.ShouldBe(GateStopReason.ModeratorUnavailable);

        moderator.Calls.ShouldBeGreaterThan(0);
        moderator.Checks[0].Stage.ShouldBe(ModerationStage.AutopilotGate);
        // Check 7 is the one that fails; its stored detail (and the notice's reason) is moderator_unavailable.
        FailedCheckIds(world.RunId).ShouldContain(StoryGateCheckIds.ModerationGate);
        StoryFlag(world.RunId, "pass").ShouldBeFalse();

        NoticeRows(world).ShouldBe(1);
        NoticePayloads(world).ShouldContain(OwnerActionReasons.ModeratorUnavailable);
        NoticeAuditRows(world, OwnerActionReasons.ModeratorUnavailable).ShouldBe(1);
        NoticeFailureCodes(world).ShouldContain(StoryGateCheckIds.ModerationGate);
        AutoApproveAuditRows(world).ShouldBe(0);
        StoredStatus(world.EpisodeId).ShouldBe("story_review");
    }

    /// <summary>
    /// A revised story is scored on <c>revision.review_after</c>: the draft's own review failed (mean 3.0) and the
    /// revision's passes (mean 5.0), so the stored mean score of the check is 5 and the gate passes.
    /// </summary>
    [Fact]
    public async Task StoryGateEvaluator_RevisedStory_ReadsTheReviewAfterScores()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct, auditReport: GateRunSeed.RevisedAudit());
        using var factory = Factory();

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        evaluated.Value.Result.Pass.ShouldBeTrue();
        evaluated.Value.AutoApproved.ShouldBeFalse();
        StoryFlag(world.RunId, "pass").ShouldBeTrue();
        CheckValue(world.RunId, StoryGateCheckIds.SelfReviewMeanScore).ShouldBe(5.0);
    }

    /// <summary>
    /// The past-episode corpus is unavailable (a corpus read that throws), so the gate stops with an error and
    /// writes no verdict: no pass on unknown (fail closed).
    /// </summary>
    [Fact]
    public async Task StoryGateEvaluator_UnreadableCorpus_IsErrorAndWritesNoVerdict()
    {
        // No stored source similarity, so check 10 must be scored from the series' own history and therefore
        // needs the corpus this fake cannot provide.
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct, similarityScore: null);
        using var factory = Factory(corpus: new FailingCorpus());

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeTrue();
        evaluated.FirstError.Code.ShouldNotBeNullOrWhiteSpace();
        VerdictRows(world.RunId).ShouldBe(0);
        NoticeRows(world).ShouldBe(0);
    }

    /// <summary>
    /// A live pass that the approve command refuses (the seam <c>AppendFailureAsync</c> exists for) keeps the
    /// passing verdict, appends the refusal to it, and asks the owner to approve by hand.
    /// </summary>
    /// <remarks>
    /// <b>[OPEN] The refusal is recorded through the recorder.</b> The plan's route to
    /// <c>etag_conflict</c> - the story is edited or regenerated between the evaluation and the approve - cannot
    /// happen today: <c>AutopilotApprovalService</c> builds the If-Match from the freshly read current version
    /// (<c>StoryEtag.For(currentVersionId)</c>, AutopilotApprovalService.cs:168-175) instead of from
    /// <c>result.StoryVersionId</c>, so the approve always matches the version it asks about and a version the
    /// gate never evaluated would be approved. This test drives the recorder directly rather than simulating a
    /// race that production cannot produce; the one-line fix is the service's own.
    /// </remarks>
    [Fact]
    public async Task StoryGateEvaluator_ApprovalFailure_RecordsTheFailureAndNotifiesTheOwner()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct);
        using var factory = Factory(dryRun: false);

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        evaluated.Value.Result.Pass.ShouldBeTrue();

        var recorded = await RecordApprovalFailureAsync(factory, world);
        recorded.IsError.ShouldBeFalse();

        StoryFlag(world.RunId, "pass").ShouldBeTrue();
        StoryFailures(world.RunId).ShouldContain("etag_conflict");
        NoticeRows(world).ShouldBe(1);
        NoticePayloads(world).ShouldContain(OwnerActionReasons.OwnerApprovalRequired);
        NoticeAuditRows(world, OwnerActionReasons.OwnerApprovalRequired).ShouldBe(1);
        NoticeFailureCodes(world).ShouldContain("etag_conflict");

        // The live pass approved once; the refusal it recorded appended to that verdict and asked the owner to
        // decide, and it wrote no second auto-approval row.
        AutoApproveAuditRows(world).ShouldBe(1);
    }

    /// <summary>
    /// Two failing evaluations in a row are a streak of two for the series: the end-to-end variant of
    /// <see cref="GateFailureStreakReaderTests.GateFailure_TwoRunsInARow_StreakIsTwo"/>, which drives the reader
    /// over raw rows. Here both stored verdicts come from the real evaluator.
    /// </summary>
    /// <remarks>
    /// <b>[OPEN] Nothing writes <c>autopilot_runs.outcome</c> yet.</b> The enum has
    /// <c>AutopilotRunOutcome.GateFailed</c> (<c>AutopilotRunOutcome.cs:14</c>) and the reader counts it, but no
    /// producer in 02.8.2-T1 sets it: the gate records the verdict and the notice only. The two <c>UPDATE</c>
    /// statements below stand in for the stop step that 02.8.3-T1 owns, so that T4's pause rule has something to
    /// read; they are the seam, not a production behaviour.
    /// </remarks>
    [Fact]
    public async Task StoryGateEvaluator_TwoFailingRunsInARow_StreakIsTwo()
    {
        var first = await GateRunSeed.SeedRunAsync(Database, ct: Ct, auditReport: GateRunSeed.FailingAudit());
        var second = await GateRunSeed.SeedAdditionalRunAsync(
            Database, first, GateRunSeed.FailingAudit(), GateRunSeed.Start.AddHours(1), ct: Ct);
        using var factory = Factory();

        (await EvaluateAsync(factory, first)).Value.Result.Pass.ShouldBeFalse();
        (await EvaluateAsync(factory, second.RunId, first.UserId)).Value.Result.Pass.ShouldBeFalse();

        StoryFlag(first.RunId, "pass").ShouldBeFalse();
        StoryFlag(second.RunId, "pass").ShouldBeFalse();

        await GateRunSeed.MarkRunGateFailedAsync(Database, first.RunId, Ct);
        await GateRunSeed.MarkRunGateFailedAsync(Database, second.RunId, Ct);

        var reader = new GateFailureStreakReader(Database.CreateAppDbContext(new TestCurrentUser { Id = first.UserId }));

        (await reader.GetStreakAsync(first.SeriesId, Ct)).ShouldBe(2);
    }

    /// <summary>
    /// F8/SEC-9: re-evaluating the same failing run queues the owner's notice once — one outbox row, one audit row
    /// and one e-mail — while a later attempt of the same run is a new verdict and is notified again. F13: an
    /// episode that left <c>story_review</c> is never gated at all, in either mode, so a failing or dry-run verdict
    /// cannot be stored for a state the episode is not in.
    /// </summary>
    [Fact]
    public async Task StoryGateEvaluator_RepeatedFailingVerdict_NotifiesOnce_AndRefusesAnEpisodeOutOfReview()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct, auditReport: GateRunSeed.FailingAudit());
        using var factory = Factory();

        (await EvaluateAsync(factory, world)).Value.Result.Pass.ShouldBeFalse();
        (await EvaluateAsync(factory, world)).Value.Result.Pass.ShouldBeFalse();

        NoticeRows(world).ShouldBe(1, "the same stop is the verdict the run already holds");
        NoticeAuditRows(world, OwnerActionReasons.FailedChecks).ShouldBe(1);
        EmailCount(world).ShouldBe(1);

        // A later story attempt is a different verdict: the owner is told about it too.
        (await EvaluateAsync(factory, world, attempt: 2)).Value.Result.Pass.ShouldBeFalse();
        NoticeRows(world).ShouldBe(2);
        NoticeAuditRows(world, OwnerActionReasons.FailedChecks).ShouldBe(2);
        EmailCount(world).ShouldBe(2);

        // F13: the episode is not under review, so nothing may be recorded or notified for it any more — not even
        // the failing verdict this run carries, and not in the dry run either.
        var draft = await GateRunSeed.SeedRunAsync(
            Database,
            ct: Ct,
            auditReport: GateRunSeed.FailingAudit(),
            status: EpisodeStatus.Draft);
        using var live = Factory(dryRun: false);

        var refusedDryRun = await EvaluateAsync(factory, draft);
        var refusedLive = await EvaluateAsync(live, draft);

        refusedDryRun.IsError.ShouldBeTrue();
        refusedDryRun.FirstError.Code.ShouldBe(StoryGateEvaluator.EpisodeStateErrorCode);
        refusedLive.IsError.ShouldBeTrue();
        refusedLive.FirstError.Code.ShouldBe(StoryGateEvaluator.EpisodeStateErrorCode);

        VerdictRows(draft.RunId).ShouldBe(0, "a verdict stored from a state the episode is not in would claim an approval that cannot happen");
        NoticeRows(draft).ShouldBe(0);
        NoticeAuditRows(draft, OwnerActionReasons.FailedChecks).ShouldBe(0);
        EmailCount(draft).ShouldBe(0);
        StoredStatus(draft.EpisodeId).ShouldBe("draft");
    }

    /// <summary>
    /// The D19/D22 sweep: neither the story body's marker nor the seeded source URL appears in anything the gate
    /// stored — the runs of this episode, the audit rows of this episode, and the notices it queued.
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

    private static string Describe(ErrorOr<StoryGateOutcome> evaluated) =>
        evaluated.IsError ? string.Join("; ", evaluated.Errors.Select(error => $"{error.Code}: {error.Description}")) : "ok";

    private static async Task<ErrorOr<StoryGateOutcome>> EvaluateAsync(
        ApiFactory factory,
        GateRunSeed.GateWorld world,
        int attempt = 1) => await EvaluateAsync(factory, world.RunId, world.UserId, attempt);

    private static async Task<ErrorOr<StoryGateOutcome>> EvaluateAsync(
        ApiFactory factory,
        Guid runId,
        Guid userId,
        int attempt = 1)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(userId);
        var gate = scope.ServiceProvider.GetRequiredService<IAutoApprovalGate>();

        return await gate.EvaluateStoryAsync(runId, attempt, Ct);
    }

    private static async Task<ErrorOr<Success>> RecordApprovalFailureAsync(ApiFactory factory, GateRunSeed.GateWorld world)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);
        var recorder = scope.ServiceProvider.GetRequiredService<GateOutcomeRecorder>();

        return await recorder.RecordApprovalFailureAsync(world.RunId, "story", "etag_conflict", Ct);
    }

    private ApiFactory Factory(
        bool dryRun = true,
        IContentModerator? moderator = null,
        ISimilarityCorpusProvider? corpus = null) =>
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

                if (moderator is not null)
                {
                    services.RemoveAll<IContentModerator>();
                    services.AddSingleton(moderator);
                }

                if (corpus is not null)
                {
                    services.RemoveAll<ISimilarityCorpusProvider>();
                    services.AddSingleton(corpus);
                }
            });

    private string StoryBlock(Guid runId) =>
        SqlProbe.ScalarText(Database, $"SELECT gate_results -> 'story' FROM autopilot_runs WHERE id = '{runId}'");

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

    private string StoryFailures(Guid runId) =>
        SqlProbe.ScalarText(Database, $"SELECT gate_results -> 'story' ->> 'failures' FROM autopilot_runs WHERE id = '{runId}'");

    private List<string> FailedCheckIds(Guid runId) =>
        SqlProbe.QueryStrings(
            Database,
            "SELECT entry ->> 'id' FROM autopilot_runs, jsonb_array_elements(gate_results -> 'story' -> 'checks') AS entry "
            + $"WHERE id = '{runId}' AND (entry ->> 'pass')::boolean = false");

    private double CheckValue(Guid runId, string checkId) =>
        double.Parse(
            SqlProbe.ScalarText(
                Database,
                "SELECT entry ->> 'value' FROM autopilot_runs, jsonb_array_elements(gate_results -> 'story' -> 'checks') AS entry "
                + $"WHERE id = '{runId}' AND entry ->> 'id' = '{checkId}'"),
            System.Globalization.CultureInfo.InvariantCulture);

    private string Json(Guid runId, string key) =>
        SqlProbe.ScalarText(Database, $"SELECT gate_results ->> '{key}' FROM autopilot_runs WHERE id = '{runId}'");

    private long VerdictRows(Guid runId) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM autopilot_runs WHERE id = '{runId}' AND gate_results IS NOT NULL");

    private string StoredStatus(Guid episodeId) =>
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{episodeId}'");

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

    /// <summary>The gate's own notice audit row for this run and reason (one transaction with the verdict).</summary>
    private long NoticeAuditRows(GateRunSeed.GateWorld world, string reason) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM audit_logs WHERE action = '{AutopilotNotificationService.AuditAction}' "
            + $"AND data->>'runId' = '{world.RunId}' AND data->>'reason' = '{reason}'");

    /// <summary>The owner-action e-mails this world queued (the TestKit sender writes real rows).</summary>
    private long EmailCount(GateRunSeed.GateWorld world) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM email_messages WHERE template = '{AutopilotNotificationService.EmailTemplate}' "
            + $"AND user_id = '{world.UserId}'");

    /// <summary>The failure codes the notice audit row carries (check ids for a failed evaluation).</summary>
    private string NoticeFailureCodes(GateRunSeed.GateWorld world) =>
        SqlProbe.ScalarText(
            Database,
            $"SELECT coalesce(data -> 'failures', '[]'::jsonb)::text FROM audit_logs "
            + $"WHERE action = '{AutopilotNotificationService.AuditAction}' AND data->>'runId' = '{world.RunId}'");

    /// <summary>The auto-approval audit rows of this world's owner: zero unless the gate approved.</summary>
    private long AutoApproveAuditRows(GateRunSeed.GateWorld world) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM audit_logs WHERE action = '{AutopilotApprovalService.StoryApprovedAction}' "
            + $"AND subject_user_id = '{world.UserId}'");

    private sealed class FailingCorpus : ISimilarityCorpusProvider
    {
        public ValueTask<IReadOnlyList<CorpusText>> LoadAsync(Guid episodeId, Language lang, CancellationToken ct) =>
            throw new SimilarityCorpusUnavailableException("gate_test_source_corpus_unavailable");

        public ValueTask<IReadOnlyList<CorpusText>> GetPastEpisodesAsync(
            Guid seriesId,
            Guid excludeEpisodeId,
            int max,
            CancellationToken ct) =>
            throw new SimilarityCorpusUnavailableException("gate_test_history_corpus_unavailable");
    }
}
