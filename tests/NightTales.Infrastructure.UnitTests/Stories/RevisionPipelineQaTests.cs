using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.Stories.Review;
using NightTales.Domain.Common;
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
/// Independent QA probes for 02.6.2-T1 (the single revision pass). These were written by an agent that did not
/// write the production code, from the task file's Acceptance checks and Failure-handling table, and they target
/// the rows a green suite can still hide: a revision that silently drops an approved citation, a structurally
/// broken revision output, and the unchanged "fix" a model can return. The harness is
/// <see cref="RevisionWorld"/>'s (same assembly).
/// </summary>
[Trait("Category", "Integration")]
public sealed class RevisionPipelineQaTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Model = "gpt-test";

    /// <summary>
    /// Failure-handling table row "Revision drops a <c>fact_ref</c> or invents a fact": dropping an approved
    /// citation must be "treated as invalid output (original kept)". The revision returns the *same* hook text
    /// with the approved <c>f1</c> reference removed, so the draft keeps the claim without its citation.
    /// </summary>
    [Fact]
    public async Task QA_RevisionDroppingAnApprovedFactRef_KeepsTheOriginalDraft()
    {
        var sheetId = Guid.CreateVersion7();
        var sheet = new ApprovedFactSheet(
            sheetId,
            1,
            [new FactForWriter("f1", "Nguon da duoc kiem chung.", ["s1"])],
            new Dictionary<string, Guid>(StringComparer.Ordinal));
        var world = await RevisionWorld.CreateAsync(Database, Ct, topic: Topic.TrueCrime, approvedSheet: sheet);
        var original = Cited("f1");
        world.Writer.EnqueueWrite(WriteOk(original));
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        // Same sentences, but the hook no longer cites the approved fact: the claim lost its citation.
        world.Writer.EnqueueRevision(RevisionOk(original with
        {
            Hook = new StorySegmentV1(original.Hook.Text, ["k1"], []),
        }));
        world.Writer.EnqueueReview(ReviewOk());

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        var versions = await world.VersionsAsync();
        versions.Count.ShouldBe(1, "a revision that drops an approved fact_ref is invalid output, so the original stays current");
        versions[0].Source.ShouldBe(StoryVersionSource.Generated);
    }

    /// <summary>
    /// T3 probe: a revision whose paragraph list is missing altogether is structurally broken output. Whatever the
    /// pipeline decides, the run must not fail with an unhandled error and nothing unreadable may become current.
    /// </summary>
    [Fact]
    public async Task QA_RevisionWithNullParagraphList_NeverFailsTheRun()
    {
        var world = await RevisionWorld.CreateAsync(Database, Ct);
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(RevisionOk(Draft() with { Paragraphs = null! }));
        world.Writer.EnqueueReview(ReviewOk());

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded, "a broken revision output is not a run failure");
        var versions = await world.VersionsAsync();
        versions.Count.ShouldBe(1, "a revision with no paragraph list is structurally invalid, so the original stays current");
        foreach (var version in versions)
        {
            version.Content.ShouldNotBeNullOrWhiteSpace();
            Should.NotThrow(() => System.Text.Json.JsonDocument.Parse(version.Content));
        }
    }

    /// <summary>
    /// A3: a model that returns the draft unchanged fixes nothing. The revision is invalid output — no second row is
    /// written for content the generated row already holds, and review #2 is never paid for.
    /// </summary>
    [Fact]
    public async Task QA_UnchangedRevisionOutput_KeepsTheOriginalDraft()
    {
        var world = await RevisionWorld.CreateAsync(Database, Ct);
        var draft = Draft();
        world.Writer.EnqueueWrite(WriteOk(draft));
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(RevisionOk(draft));

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        var version = (await world.VersionsAsync()).ShouldHaveSingleItem();
        version.Source.ShouldBe(StoryVersionSource.Generated, "an unchanged revision never becomes a row of its own");

        var block = System.Text.Json.Nodes.JsonNode.Parse(version.AuditReport!)!["revision"]!;
        block["applied"]!.GetValue<bool>().ShouldBeFalse();
        block["reason"]!.GetValue<string>().ShouldBe(AuditReportBuilder.RevisionReasonInvalidOutput);
        world.Writer.RevisionRequests.Count.ShouldBe(1, "never a second revision");
        world.Writer.ReviewRequests.Count.ShouldBe(1, "review #2 is not paid for a revision that fixes nothing");
    }

    /// <summary>
    /// Fail-open probe: a review with no issues at all must never reach the revision stage, even when the revision
    /// stage is enabled with a positive bound.
    /// </summary>
    [Fact]
    public async Task QA_EmptyReviewIssueList_NeverRevises()
    {
        var world = await RevisionWorld.CreateAsync(Database, Ct, maxRevisions: 1);
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk());

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        world.Writer.RevisionRequests.ShouldBeEmpty();
        (await world.VersionCountAsync()).ShouldBe(1);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    /// <summary>A draft whose hook cites <paramref name="factRef"/>.</summary>
    private static StoryContentV1 Cited(string factRef) => Draft() with
    {
        Hook = new StorySegmentV1($"Mo dau {factRef}.", ["k1"], [factRef]),
    };
}
