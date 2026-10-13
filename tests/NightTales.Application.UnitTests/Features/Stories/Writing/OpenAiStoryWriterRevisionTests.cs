using ErrorOr;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Stories.Review;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Length;
using NightTales.Domain.Stories.Prompts;
using NightTales.Domain.Stories.Review;
using NightTales.TestKit.Text;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories.Writing;

/// <summary>
/// 02.6.2-T1: <c>ReviseAsync</c> against the scripted <see cref="FakeTextClient"/> — every outcome the pipeline
/// switches on, the meter row it records, and the call log (one Revision and two Review entries for one run, D20).
/// </summary>
public sealed class OpenAiStoryWriterRevisionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly LengthTarget Length = new(1300, 2080, 20, 32, 6000, 2500, 6000);

    [Fact]
    public async Task ReviseAsync_ScriptedOutcomes_AreReturnedAsData()
    {
        var usage = new TokenUsage(1000, 200, 0, 500, 100);
        var cases = new (TextCallResult<StoryContentV1> Outcome, Type Expected)[]
        {
            (TextCallResult<StoryContentV1>.Ok(Draft(), FakeTextClient.Model, usage, "r1"), typeof(TextCallOk<StoryContentV1>)),
            (TextCallResult<StoryContentV1>.InvalidOutput(FakeTextClient.Model, usage, "r2", ["max_output_tokens_exceeded"]), typeof(TextCallInvalidOutput<StoryContentV1>)),
            (TextCallResult<StoryContentV1>.Moderated(FakeTextClient.Model, usage, "r3", ["violence"]), typeof(TextCallModerated<StoryContentV1>)),
            (TextCallResult<StoryContentV1>.ProviderFundsExhausted(FakeTextClient.Model, "r4"), typeof(TextCallFundsExhausted<StoryContentV1>)),
            (TextCallResult<StoryContentV1>.ProviderUnavailable(FakeTextClient.Model, null), typeof(TextCallUnavailable<StoryContentV1>)),
        };

        foreach (var (outcome, expected) in cases)
        {
            var client = new FakeTextClient().Enqueue(TextStep.Revision, outcome);
            var writer = new OpenAiStoryWriter(new StubAssembler(), client, new StubCatalog(), new RecordingMeter(), new StubEstimator(1m));

            var result = await writer.ReviseAsync(Request(), Ct);

            result.GetType().ShouldBe(expected);
        }
    }

    [Fact]
    public async Task ReviseAsync_Ok_RecordsOneRowWithTheRevisionOperation()
    {
        var meter = new RecordingMeter();
        var req = Request();
        var client = new FakeTextClient().Enqueue(TextStep.Revision, TextCallResult<StoryContentV1>.Ok(Draft(), FakeTextClient.Model, new TokenUsage(1000, 200, 0, 500, 100), "req-1"));
        var writer = new OpenAiStoryWriter(new StubAssembler(), client, new StubCatalog(), meter, new StubEstimator(2m));

        await writer.ReviseAsync(req, Ct);

        meter.Items.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            item => item.Operation.ShouldBe("story_revision"),
            item => item.Model.ShouldBe(FakeTextClient.Model),
            item => item.UserId.ShouldBe(req.Run.UserId),
            item => item.ReservationId.ShouldBe(req.Run.Reservation.Value),
            item => item.Estimated.ShouldBeFalse());
    }

    [Fact]
    public async Task ReviseAsync_FundsExhausted_RecordsNothing()
    {
        var meter = new RecordingMeter();
        var client = new FakeTextClient().Enqueue(TextStep.Revision, TextCallResult<StoryContentV1>.ProviderFundsExhausted(FakeTextClient.Model, null));
        var writer = new OpenAiStoryWriter(new StubAssembler(), client, new StubCatalog(), meter, new StubEstimator(2m));

        var result = await writer.ReviseAsync(Request(), Ct);

        result.ShouldBeOfType<TextCallFundsExhausted<StoryContentV1>>();
        meter.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReviseAsync_CancelledAfterDispatch_RecordsAnEstimatedRowAndRethrows()
    {
        var req = Request();
        var meter = new RecordingMeter();
        var writer = new OpenAiStoryWriter(new StubAssembler(), new CancellingClient(dispatch: true), new StubCatalog(), meter, new StubEstimator(3m));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await writer.ReviseAsync(req, cts.Token));

        var item = meter.Items.ShouldHaveSingleItem();
        item.ShouldSatisfyAllConditions(
            i => i.Estimated.ShouldBeTrue(),
            i => i.CostUsd.ShouldBe(3m),
            i => i.Operation.ShouldBe("story_revision"));
        meter.Tokens.Single().CanBeCanceled.ShouldBeFalse("the settlement must not be cancelled with the caller");
    }

    [Fact]
    public async Task ReviseAsync_UsesTheRevisionStepTheRequestRevisionCapAndTheStorySchema()
    {
        var client = new FakeTextClient().Enqueue(TextStep.Revision, TextCallResult<StoryContentV1>.Ok(Draft(), FakeTextClient.Model, null, "r1"));
        var writer = new OpenAiStoryWriter(new StubAssembler(), client, new StubCatalog(), new RecordingMeter(), new StubEstimator(1m));
        var req = Request();

        await writer.ReviseAsync(req, Ct);

        var call = client.Requests.ShouldHaveSingleItem();
        call.Step.ShouldBe(TextStep.Revision);
        call.MaxOutputTokens.ShouldBe(req.Length.RevisionMaxOutput);
        call.SchemaName.ShouldBe("story.v1");
        call.PromptCacheKey.ShouldBe("revision:ghost.vi.v1");
        call.SystemPrompt.ShouldBe("REVISE-SYSTEM");
    }

    [Fact]
    public async Task CallLog_OneRun_TwoReviewsAndOneRevision()
    {
        // D20: the run makes exactly two review calls (review #1 and review #2) and one revision, never more.
        var review = new StoryReviewV1(new ReviewScores(4, 4, 4, 4, 4, null, 4), [], [], null, [], "solid draft");
        var client = new FakeTextClient()
            .Enqueue(TextStep.Review, TextCallResult<StoryReviewV1>.Ok(review, FakeTextClient.Model, null, "rv1"))
            .Enqueue(TextStep.Review, TextCallResult<StoryReviewV1>.Ok(review, FakeTextClient.Model, null, "rv2"))
            .Enqueue(TextStep.Revision, TextCallResult<StoryContentV1>.Ok(Draft(), FakeTextClient.Model, null, "r1"));
        var writer = new OpenAiStoryWriter(new StubAssembler(), client, new StubCatalog(), new RecordingMeter(), new StubEstimator(1m));

        await writer.ReviewAsync(ReviewRequest(), Ct);
        await writer.ReviseAsync(Request(), Ct);
        await writer.ReviewAsync(ReviewRequest(), Ct);

        client.Requests.Select(call => call.Step).ShouldBe([TextStep.Review, TextStep.Revision, TextStep.Review]);
    }

    private static StoryContentV1 Draft() =>
        new("T", new StorySegmentV1("h", [], []), [], new StorySegmentV1("e", [], []), "d", [], [], new StoryStructureV1("a", "b", "c", "d", "e"));

    private static StoryRevisionRequest Request() =>
        new(
            Run(),
            Draft(),
            new StoryReviewV1(new ReviewScores(4, 4, 4, 4, 4, null, 4), [new ReviewIssueV1("i1", "hook_missing", "blocking", "hook", "flat", "waits", "open on the fear")], [], null, [], "solid draft"),
            [new ReviewIssueV1("i1", "hook_missing", "blocking", "hook", "flat", "waits", "open on the fear")],
            Language.Vi,
            LengthTier.Standard,
            Length,
            Topic.Ghost,
            [],
            null,
            "story/ghost.vi.v1.md");

    private static StoryReviewRequest ReviewRequest() =>
        new(
            Run(),
            Draft(),
            Language.Vi,
            LengthTier.Standard,
            Length,
            Topic.Ghost,
            [],
            null,
            EarRuleQuestions.For(Language.Vi),
            "story/ghost.vi.v1.md");

    private static RunContext Run() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new ReservationId(Guid.NewGuid()), GenerationTrigger.User, 1, "1");

    private sealed class CancellingClient(bool dispatch) : ITextClient
    {
        public ValueTask<TextCallResult<T>> CallAsync<T>(TextCallRequest req, CancellationToken ct)
            where T : class
        {
            if (dispatch)
            {
                req.MarkDispatched(FakeTextClient.Model);
            }

            throw new OperationCanceledException(ct);
        }
    }

    private sealed class StubAssembler : IPromptAssembler
    {
        public AssembledPrompt Assemble(StoryWriteRequest req, AssemblerContext ctx) => new("SYSTEM", "{\"data\":1}", "rel=test", "writer:ghost.vi.v1");

        public ErrorOr<AssembledPrompt> Review(StoryReviewRequest req) => new AssembledPrompt("REVIEW-SYSTEM", "{\"data\":2}", "rel=test;review=v1", "review:ghost.vi.v1");

        public ErrorOr<AssembledPrompt> Revise(StoryRevisionRequest req) => new AssembledPrompt("REVISE-SYSTEM", "{\"data\":3}", "rel=test;revise=v1", "revision:ghost.vi.v1");
    }

    private sealed class RecordingMeter : IUsageMeter
    {
        internal List<UsageItem> Items { get; } = [];

        internal List<CancellationToken> Tokens { get; } = [];

        public Task RecordAsync(UsageItem item, CancellationToken ct)
        {
            Items.Add(item);
            Tokens.Add(ct);

            return Task.CompletedTask;
        }
    }

    private sealed class StubEstimator(decimal? bound) : IStoryCostEstimator
    {
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            throw new InvalidOperationException("The revision must not depend on the whole-pipeline bound (CR-1).");

        public Task<ErrorOr<UsageBound>> WriterWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(bound is { } value ? new UsageBound(value, 1, 1) : Error.Failure("price_unavailable"));

        public Task<ErrorOr<UsageBound>> ReviewWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            WriterWorstCaseAsync(tier, language, ct);

        public Task<ErrorOr<UsageBound>> RevisionWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            WriterWorstCaseAsync(tier, language, ct);
    }

    private sealed class StubCatalog : IPromptCatalog
    {
        public string Release => "test";

        public IReadOnlyDictionary<string, PromptFileInfo> Files => throw new NotSupportedException();

        public ReadOnlyMemory<byte> GetSchema(string name) =>
            name is "story.v1.json" or "review.v1.json" ? System.Text.Encoding.UTF8.GetBytes("{\"schema\":true}") : throw new NotSupportedException(name);

        public string GetSystemPrompt() => throw new NotSupportedException();

        public string GetPreset(Topic topic, Language language) => throw new NotSupportedException();

        public string GetShared(PromptFile file, Language language) => throw new NotSupportedException();

        public string GetStepPrompt(PromptStep promptStep) => throw new NotSupportedException();

        public string BuildPromptVersion(PromptVersionParts parts) => throw new NotSupportedException();
    }
}
