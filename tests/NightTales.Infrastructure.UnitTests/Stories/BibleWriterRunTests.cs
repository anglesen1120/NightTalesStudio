using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.SeriesBible.Abstractions;
using NightTales.Application.Features.Stories.Review;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Review;
using NightTales.Infrastructure.Prompts;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Stories;

/// <summary>
/// The series bible's place in one generation run (02.6.3-T3), through the real <see cref="StoryGenerationPipeline"/>
/// and a real <c>AppDbContext</c>. Only the bible provider and the writer are replaced: an oversized bible fails the
/// run before the writer is reached, an absent bible reaches the writer as null, one snapshot is read once and shared by
/// the writer, the reviews and the revision, and <c>avoid_plots</c> reaches the writer prompt without a local block.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BibleWriterRunTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Model = "gpt-test";
    private const string AvoidedPlot = "Tham tu tim thay manh moi trong ngoi nha bo hoang.";

    [Fact]
    public async Task GenerateStoryJob_BibleTooLarge_FailsWithBibleTooLargeNotCounted()
    {
        var world = await RevisionWorld.CreateAsync(
            Database,
            Ct,
            reviewEnabled: true,
            revisionEnabled: true,
            maxRevisions: 1,
            minTextSimilarity: 0.5,
            budget: null,
            bibles: new FailingBibleProvider(new BibleSnapshotTooLargeException(90_000, 60_000)));

        await world.RunAsync(Ct);

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.ErrorCode.ShouldBe(ErrorCodes.StoryGenerationFailed);
        run.ErrorDetail.ShouldBe("bible_too_large");
        run.CountsTowardLimit.ShouldBeFalse("an oversized bible is a refusal of the system, not a generated story");
        world.Writer.WriteRequests.ShouldBeEmpty("an oversized bible is refused before the writer is reached");
        world.Ledger.Refunds.ShouldBe([("story_generation_failed", world.Reservation)]);
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.Draft);
        (await world.VersionCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task BibleSnapshot_NoBible_RequestAssembledWithoutBible()
    {
        var world = await RevisionWorld.CreateAsync(
            Database,
            Ct,
            reviewEnabled: true,
            revisionEnabled: true,
            maxRevisions: 1,
            minTextSimilarity: 0.5,
            budget: null);
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk());

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        var write = world.Writer.WriteRequests.ShouldHaveSingleItem();
        write.Bible.ShouldBeNull("an empty bible provider answers with no snapshot");
        write.AvoidPlots.ShouldBeEmpty();
        world.Writer.ReviewRequests.ShouldHaveSingleItem().Bible.ShouldBeNull();

        var prompt = Assemble(write);
        JsonNode.Parse(prompt.UserDataJson)!["bible"].ShouldBeNull("a run without a bible sends no bible to the writer");
    }

    [Fact]
    public async Task BibleSnapshot_ReadOncePerRun_SameSnapshotReachesWriterAndReview()
    {
        var snapshot = Snapshot(version: 7, rule: "rule-x");
        var provider = new ScriptedBibleProvider(new BibleForWriter(snapshot, [], 7));
        var world = await RevisionWorld.CreateAsync(
            Database,
            Ct,
            reviewEnabled: true,
            revisionEnabled: true,
            maxRevisions: 1,
            minTextSimilarity: 0.5,
            budget: null,
            bibles: provider);
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(RevisionOk(RevisedDraft()));
        world.Writer.EnqueueReview(ReviewOk());

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        provider.Reads.ShouldBe(1, "one bible read per run, reused by the writer, both reviews and the revision");
        world.Writer.WriteRequests.ShouldHaveSingleItem().Bible.ShouldBeSameAs(snapshot);
        world.Writer.ReviewRequests.Count.ShouldBe(2);
        foreach (var review in world.Writer.ReviewRequests)
        {
            review.Bible.ShouldBeSameAs(snapshot);
        }

        world.Writer.RevisionRequests.ShouldHaveSingleItem().Bible.ShouldBeSameAs(snapshot);
    }

    [Fact]
    public async Task AvoidPlots_ReachWriterPrompt_NotLocallyBlocked()
    {
        var provider = new ScriptedBibleProvider(new BibleForWriter(Snapshot(version: 3, rule: "rule-y"), [AvoidedPlot], 3));
        var world = await RevisionWorld.CreateAsync(
            Database,
            Ct,
            reviewEnabled: true,
            revisionEnabled: false,
            maxRevisions: 1,
            minTextSimilarity: 0.5,
            budget: null,
            bibles: provider);
        var draftRepeatingPlot = Draft() with
        {
            Hook = new StorySegmentV1($"{AvoidedPlot} Mo dau.", ["k1"], []),
        };
        world.Writer.EnqueueWrite(WriteOk(draftRepeatingPlot));
        world.Writer.EnqueueReview(ReviewOk());

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(
            GenerationRunStatus.Succeeded,
            "avoid_plots is a prompt instruction; the pipeline must not block a draft that repeats the plot locally");
        (await world.VersionCountAsync()).ShouldBe(1);
        var write = world.Writer.WriteRequests.ShouldHaveSingleItem();
        write.AvoidPlots.ShouldBe([AvoidedPlot]);
        var avoidPlots = JsonNode.Parse(Assemble(write).UserDataJson)!["avoid_plots"]!.AsArray();
        avoidPlots.Select(node => node!.GetValue<string>()).ShouldContain(AvoidedPlot);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BibleSnapshot Snapshot(int version, string rule) =>
        new(version, null, [], [], [rule], [], false);

    private static AssembledPrompt Assemble(StoryWriteRequest request) =>
        new PromptAssembler(new PromptCatalog(new EmbeddedPromptResourceSource()), NullLogger<PromptAssembler>.Instance)
            .Assemble(
                request,
                new AssemblerContext(
                    request.Series.Topic,
                    request.Series.Language,
                    StoryPresetKeys.For(request.Series.Topic, request.Series.Language),
                    6000));

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

    private static StoryContentV1 RevisedDraft() => Draft() with
    {
        Hook = new StorySegmentV1("Mo dau moi voi loi dan ro rang hon.", ["k1"], []),
        Paragraphs = [new StorySegmentV1("Doan mot da duoc viet lai cho gon hon.", ["k2"], [])],
    };

    /// <summary>Serves one fixed bible and counts how many reads the pipeline made.</summary>
    private sealed class ScriptedBibleProvider(BibleForWriter bible) : IBibleSnapshotProvider
    {
        public int Reads { get; private set; }

        public ValueTask<BibleForWriter> GetForWriterAsync(Guid seriesId, CancellationToken ct)
        {
            Reads++;
            return ValueTask.FromResult(bible);
        }
    }

    /// <summary>Fails every read the way the real provider does for a bible over the writer budget.</summary>
    private sealed class FailingBibleProvider(Exception failure) : IBibleSnapshotProvider
    {
        public ValueTask<BibleForWriter> GetForWriterAsync(Guid seriesId, CancellationToken ct) =>
            ValueTask.FromException<BibleForWriter>(failure);
    }
}
