using System.Text.Json.Nodes;
using ErrorOr;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Review;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Review;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Stories;

/// <summary>
/// The single revision pass (02.6.2-T1, 02 README D21 step 5 + D22) against a real <c>AppDbContext</c>:
/// the decision gate, the untrusted-output defences (persistence rules, a new blocking validator finding,
/// review #2 unusable), the one-call bound, cancellation and the provider-funds path. The provider edge is
/// the scripted <see cref="ScriptedRevisionWriter"/>; nothing else is replaced.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RevisionPipelineTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Model = "gpt-test";

    [Fact]
    public async Task RevisionDisabled_NeverCallsReviseAsync_AndWritesNoRevisionBlock()
    {
        var world = await RevisionWorld.CreateAsync(Database, Ct, revisionEnabled: false);
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        world.Writer.RevisionRequests.ShouldBeEmpty("Story:Revision:Enabled is false");
        var version = (await world.VersionsAsync()).ShouldHaveSingleItem();
        version.Source.ShouldBe(StoryVersionSource.Generated);
        version.AuditReport.ShouldNotBeNull().ShouldNotContain("\"revision\"");
        world.Progress.StageCalls.ShouldBe(NoRevisionStages);
    }

    [Fact]
    public async Task MaxRevisionsZero_NeverCallsReviseAsync()
    {
        var world = await RevisionWorld.CreateAsync(Database, Ct, maxRevisions: 0);
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        world.Writer.RevisionRequests.ShouldBeEmpty("D21: MaxRevisions is a hard bound");
        (await world.VersionCountAsync()).ShouldBe(1);
        world.Progress.StageCalls.ShouldBe(NoRevisionStages);
    }

    [Fact]
    public async Task SpendCapAtRevisionGate_KeepsGeneratedRowWithReason()
    {
        // Two gates pass (the writer's and the review's), the third — the one that pays for the revision — is refused.
        var world = await RevisionWorld.CreateAsync(Database, Ct, budget: new StagedRevisionBudgetGuard(allow: 2));
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded, "a refused revision degrades, it never pauses the run");
        world.Writer.RevisionRequests.ShouldBeEmpty("no paid call past the cap");
        var versions = await world.VersionsAsync();
        var version = versions.ShouldHaveSingleItem();
        var block = RevisionBlock(version);
        block["applied"]!.GetValue<bool>().ShouldBeFalse();
        block["reason"]!.GetValue<string>().ShouldBe(AuditReportBuilder.RevisionReasonSpendCapReached);
        world.Progress.StageCalls.ShouldBe(AttemptedNotAppliedStages);
    }

    [Fact]
    public async Task RevisedTextWithControlCharacter_KeepsOriginalDraft()
    {
        var world = await RevisionWorld.CreateAsync(Database, Ct);
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(RevisionOk(Draft() with
        {
            Hook = new StorySegmentV1("Mo dau moi \u0007.", ["k1"], []),
        }));

        await world.RunAsync(Ct);

        await AssertKeptOriginalAsync(world);
    }

    [Fact]
    public async Task RevisedTextWithLoneSurrogate_KeepsOriginalDraft()
    {
        var world = await RevisionWorld.CreateAsync(Database, Ct);
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(RevisionOk(Draft() with
        {
            Hook = new StorySegmentV1("Mo dau moi \ud83d.", ["k1"], []),
        }));

        await world.RunAsync(Ct);

        await AssertKeptOriginalAsync(world);
    }

    [Fact]
    public async Task RevisedDraftWithAnUnapprovedFactRef_KeepsOriginalDraft()
    {
        // The citation the revision invents is not in the approved sheet: a new blocking validator finding, so the
        // revision cannot become current (D21: the revised output is untrusted like a fresh draft).
        var sheetId = Guid.CreateVersion7();
        var sheet = new ApprovedFactSheet(
            sheetId,
            1,
            [new FactForWriter("f1", "Nguon da duoc kiem chung.", ["s1"])],
            new Dictionary<string, Guid>(StringComparer.Ordinal));
        var world = await RevisionWorld.CreateAsync(Database, Ct, topic: Topic.TrueCrime, approvedSheet: sheet);
        world.Writer.EnqueueWrite(WriteOk(Cited("f1")));
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(RevisionOk(Cited("f9")));

        await world.RunAsync(Ct);

        await AssertKeptOriginalAsync(world);
    }

    [Fact]
    public async Task RevisedDraftChangingSegmentCountAndAnUnaddressedSegment_RecordsAdvisoryDrift()
    {
        var world = await RevisionWorld.CreateAsync(Database, Ct);
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(RevisionOk(Draft() with
        {
            Paragraphs =
            [
                new StorySegmentV1("Troi chuyen mua tren con pho vang va chiec dong ho ga tau diem muoi hai tieng.", ["k2"], []),
                new StorySegmentV1("Doan hai duoc them vao ban sua.", ["k4"], []),
            ],
        }));
        world.Writer.EnqueueReview(ReviewOk());

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryReview, "drift is advisory and never blocks the episode");
        var versions = await world.VersionsAsync();
        versions.Count.ShouldBe(2);
        var block = RevisionBlock(versions[1]);
        block["applied"]!.GetValue<bool>().ShouldBeTrue();
        var drift = block["drift"]!;
        drift["segment_count_changed"]!.GetValue<bool>().ShouldBeTrue();
        drift["changed_segments"]!.AsArray().Select(node => node!.GetValue<string>())
            .ShouldContain("p:0", "the untouched paragraph was rewritten");

        var issues = block["review_after"]!["issues"]!.AsArray();
        var driftIssue = issues.Single(issue => issue!["type"]!.GetValue<string>() == AuditReportBuilder.RevisionDriftType);
        driftIssue!["severity"]!.GetValue<string>().ShouldBe("advisory");
        driftIssue!["kind"]!.GetValue<string>().ShouldBe("objective");
    }

    [Fact]
    public async Task ReviewTwoUnusable_KeepsOriginalDraftWithReviewUnavailableReason()
    {
        var world = await RevisionWorld.CreateAsync(Database, Ct);
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(RevisionOk(RevisedDraft()));
        world.Writer.EnqueueReview(TextCallResult<StoryReviewV1>.InvalidOutput(Model, null, null, ["schema_invalid"]));

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        world.Writer.ReviewRequests.Count.ShouldBe(2, "review #2 runs once and is never retried");
        var version = (await world.VersionsAsync()).ShouldHaveSingleItem();
        var block = RevisionBlock(version);
        block["applied"]!.GetValue<bool>().ShouldBeFalse();
        block["reason"]!.GetValue<string>().ShouldBe(AuditReportBuilder.RevisionReasonReviewUnavailable);
    }

    [Fact]
    public async Task ProviderFundsExhaustedOnRevision_StaysQueuedWithPauseReason()
    {
        var world = await RevisionWorld.CreateAsync(Database, Ct);
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(TextCallResult<StoryContentV1>.ProviderFundsExhausted(Model, "req-revision"));

        await world.RunAsync(Ct);

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Queued);
        run.PauseReason.ShouldBe(GenerationPauseReason.ProviderFundsExhausted);
        run.CountsTowardLimit.ShouldBeFalse();
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryGenerating);
        (await world.VersionCountAsync()).ShouldBe(0);
        world.Ledger.Refunds.ShouldBeEmpty();
    }

    [Fact]
    public async Task CancellationDuringRevision_PropagatesAndWritesNoVersionRow()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var world = await RevisionWorld.CreateAsync(Database, Ct);
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevisionCancelling(cancellation);

        await Should.ThrowAsync<OperationCanceledException>(() => world.RunAsync(cancellation.Token));

        world.Writer.RevisionRequests.Count.ShouldBe(1);
        (await world.VersionCountAsync()).ShouldBe(0, "the caller's token cancels the run before the saving stage");
    }

    private static readonly GenerationStage[] NoRevisionStages =
    [
        GenerationStage.Writing,
        GenerationStage.Checking,
        GenerationStage.Checking,
        GenerationStage.Reviewing,
        GenerationStage.Scoring,
        GenerationStage.Saving,
        GenerationStage.Done,
    ];

    private static readonly GenerationStage[] AttemptedNotAppliedStages =
    [
        GenerationStage.Writing,
        GenerationStage.Checking,
        GenerationStage.Checking,
        GenerationStage.Reviewing,
        GenerationStage.Revising,
        GenerationStage.Scoring,
        GenerationStage.Saving,
        GenerationStage.Done,
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task AssertKeptOriginalAsync(RevisionWorld world)
    {
        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded, "an unusable revision output is not a run failure");
        world.Writer.RevisionRequests.Count.ShouldBe(1, "Goal: exactly one revision call, never retried");
        var version = (await world.VersionsAsync()).ShouldHaveSingleItem();
        version.Source.ShouldBe(StoryVersionSource.Generated);
        var block = RevisionBlock(version);
        block["applied"]!.GetValue<bool>().ShouldBeFalse();
        block["reason"]!.GetValue<string>().ShouldBe(AuditReportBuilder.RevisionReasonInvalidOutput);
    }

    private static JsonNode RevisionBlock(StoryVersion version) =>
        JsonNode.Parse(version.AuditReport!)!["revision"]
        ?? throw new InvalidOperationException("the row carries no revision block.");

    private static TextCallResult<StoryContentV1> WriteOk(StoryContentV1? draft = null) =>
        TextCallResult<StoryContentV1>.Ok(draft ?? Draft(), Model, null, "req-writer");

    private static TextCallResult<StoryContentV1> RevisionOk(StoryContentV1 draft) =>
        TextCallResult<StoryContentV1>.Ok(draft, Model, null, "req-revision");

    private static TextCallResult<StoryReviewV1> ReviewOk(params ReviewIssueV1[] issues) =>
        TextCallResult<StoryReviewV1>.Ok(Review(issues), Model, null, "req-review");

    private static ReviewIssueV1 BlockingHook() =>
        new("i1", "hook_missing", "blocking", "hook", "Mo dau.", "Thieu moc.", "Them moc ro rang.");

    private static StoryReviewV1 Review(IReadOnlyList<ReviewIssueV1> issues) => new(
        new ReviewScores(4, 4, 4, 4, 4, null, 4),
        [.. issues],
        [.. EarRuleQuestions.For(Language.Vi).Select(question => new EarRuleAnswer(question.QuestionId, true, "meta", string.Empty))],
        FactualCare: null,
        [],
        "A calm summary.");

    private static StoryContentV1 Draft() => new(
        "Tieu de",
        new StorySegmentV1("Mo dau.", ["k1"], []),
        [new StorySegmentV1("Doan mot.", ["k2"], [])],
        new StorySegmentV1("Ket.", ["k3"], []),
        "Mo ta.",
        ["tag"],
        [],
        new StoryStructureV1("a", "b", "c", "d", "e"));

    /// <summary>A draft whose hook cites <paramref name="factRef"/>; used to prove an unapproved citation is refused.</summary>
    private static StoryContentV1 Cited(string factRef) => Draft() with
    {
        Hook = new StorySegmentV1($"Mo dau {factRef}.", ["k1"], [factRef]),
    };

    private static StoryContentV1 RevisedDraft() => Draft() with
    {
        Hook = new StorySegmentV1("Mo dau moi voi loi dan ro rang hon.", ["k1"], []),
        Paragraphs = [new StorySegmentV1("Doan mot da duoc viet lai cho gon hon.", ["k2"], [])],
    };

    /// <summary>Refuses every check past <paramref name="allow"/>: the writer gate and the review gate pass, the revision gate does not.</summary>
    private sealed class StagedRevisionBudgetGuard(int allow) : IBudgetGuard
    {
        private int _calls;

        public ValueTask<ErrorOr<Success>> CheckAsync(CostBound worstCaseUsd, CancellationToken ct, Guid? excludingRunId = null) =>
            ValueTask.FromResult<ErrorOr<Success>>(
                ++_calls > allow ? Error.Failure(ErrorCodes.SpendCapReached, "cap") : Result.Success);
    }
}
