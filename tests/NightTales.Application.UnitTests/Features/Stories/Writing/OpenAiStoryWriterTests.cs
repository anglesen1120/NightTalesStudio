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
using NightTales.TestKit.Text;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories.Writing;

/// <summary>02.2.1-T2: the writer assembles, calls, meters every outcome and returns the outcome untouched.</summary>
public sealed class OpenAiStoryWriterTests
{
    private static readonly LengthTarget Length = new(1300, 2080, 20, 32, 6000, 2500, 6000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TokenUsage Usage = new(1000, 200, 0, 500, 100);

    [Fact]
    public async Task OpenAiStoryWriter_WriteAsync_RecordsUsageForEveryOutcome()
    {
        var req = Request();
        var outcomes = new TextCallResult<StoryContentV1>[]
        {
            TextCallResult<StoryContentV1>.Ok(Draft(), "gpt-6-luna", Usage, "r1"),
            TextCallResult<StoryContentV1>.InvalidOutput("gpt-6-luna", Usage, "r2", ["max_output_tokens_exceeded"]),
            TextCallResult<StoryContentV1>.Moderated("gpt-6-luna", Usage, "r3", ["violence"]),
            TextCallResult<StoryContentV1>.ProviderError("gpt-6-luna", 0, "r4"),
            TextCallResult<StoryContentV1>.ProviderUnavailable("gpt-6-luna", null),
        };
        var client = new FakeTextClient();
        foreach (var outcome in outcomes)
        {
            client.Enqueue(TextStep.Writer, outcome);
        }

        var meter = new RecordingMeter();
        var writer = new OpenAiStoryWriter(new StubAssembler(), client, new StubCatalog(), meter, new StubEstimator(1.25m));

        foreach (var outcome in outcomes)
        {
            (await writer.WriteAsync(req, Ct)).ShouldBeSameAs(outcome);
        }

        meter.Items.Count.ShouldBe(outcomes.Length);
        meter.Items.ShouldAllBe(item => item.Operation == "story" && item.Provider == "openai" && item.Model == "gpt-6-luna");
        meter.Items.ShouldAllBe(item => item.UserId == req.Run.UserId && item.GenerationRunId == req.Run.GenerationRunId
            && item.EpisodeId == req.Episode.EpisodeId && item.ReservationId == req.Run.Reservation.Value && item.PriceVersion == "1");

        // Outcomes that carried usage are metered from it; the rest are written estimated at the D1 bound.
        meter.Items[0].ShouldSatisfyAllConditions(
            i => i.InputTokens.ShouldBe(1000),
            i => i.CachedInputTokens.ShouldBe(200),
            i => i.OutputTokens.ShouldBe(500),
            i => i.Estimated.ShouldBeFalse(),
            i => i.ProviderRequestId.ShouldBe("r1"));
        meter.Items[1].Estimated.ShouldBeFalse();
        meter.Items[2].Estimated.ShouldBeFalse();
        meter.Items[3].ShouldSatisfyAllConditions(i => i.Estimated.ShouldBeTrue(), i => i.CostUsd.ShouldBe(1.25m), i => i.InputTokens.ShouldBeNull());
        meter.Items[4].ShouldSatisfyAllConditions(i => i.Estimated.ShouldBeTrue(), i => i.CostUsd.ShouldBe(1.25m));
    }

    [Fact]
    public async Task OpenAiStoryWriter_WriteAsync_SendsTheAssembledPromptAndSchemaOnTheWriterStep()
    {
        var req = Request();
        var client = new FakeTextClient().EnqueueOk(TextStep.Writer, Draft(), Usage);
        var writer = new OpenAiStoryWriter(new StubAssembler(), client, new StubCatalog(), new RecordingMeter(), new StubEstimator(1m));

        await writer.WriteAsync(req, Ct);

        var call = client.Requests.Single();
        call.Step.ShouldBe(TextStep.Writer);
        call.SystemPrompt.ShouldBe("SYSTEM");
        call.UserDataJson.ShouldBe("{\"data\":1}");
        call.SchemaName.ShouldBe("story.v1");
        System.Text.Encoding.UTF8.GetString(call.Schema.Span).ShouldBe("{\"schema\":true}");
        call.MaxOutputTokens.ShouldBe(Length.WriterMaxOutput);
        call.PromptCacheKey.ShouldBe("writer:ghost.vi.v1");
        call.PromptVersion.ShouldBe("rel=test");
        call.Run.ShouldBe(req.Run);
    }

    [Fact]
    public async Task OpenAiStoryWriter_WriteAsync_AssemblesWithTheSeriesPresetKey()
    {
        var assembler = new StubAssembler();
        var client = new FakeTextClient().EnqueueOk(TextStep.Writer, Draft(), Usage);
        var writer = new OpenAiStoryWriter(assembler, client, new StubCatalog(), new RecordingMeter(), new StubEstimator(1m));

        await writer.WriteAsync(Request(), Ct);

        assembler.Context.ShouldNotBeNull();
        assembler.Context.PresetKey.ShouldBe("story/ghost.vi.v1.md");
        assembler.Context.WriterMaxOutputTokens.ShouldBe(Length.WriterMaxOutput);
    }

    [Fact]
    public async Task OpenAiStoryWriter_WriteAsync_NoUsageAndNoBound_Throws()
    {
        var client = new FakeTextClient().Enqueue(TextStep.Writer, TextCallResult<StoryContentV1>.ProviderUnavailable("gpt-6-luna", null));
        var writer = new OpenAiStoryWriter(new StubAssembler(), client, new StubCatalog(), new RecordingMeter(), new StubEstimator(null));

        await Should.ThrowAsync<InvalidOperationException>(async () => await writer.WriteAsync(Request(), Ct));
    }

    [Fact]
    public async Task OpenAiStoryWriter_WriteAsync_CancelledDuringTheBoundLookup_StillMetersTheBilledCall()
    {
        // D-1: the response is in; a caller cancelling while the bound is looked up must not lose the row.
        using var cts = new CancellationTokenSource();
        var meter = new RecordingMeter();
        var client = new FakeTextClient().Enqueue(TextStep.Writer, TextCallResult<StoryContentV1>.ProviderUnavailable("gpt-6-luna", "r1"));
        var writer = new OpenAiStoryWriter(new StubAssembler(), client, new StubCatalog(), meter, new CancellingEstimator(cts, 4m));

        var result = await writer.WriteAsync(Request(), cts.Token);

        result.ShouldBeOfType<TextCallUnavailable<StoryContentV1>>();
        meter.Items.Single().ShouldSatisfyAllConditions(i => i.Estimated.ShouldBeTrue(), i => i.CostUsd.ShouldBe(4m));
        meter.Tokens.Single().CanBeCanceled.ShouldBeFalse();
    }

    private sealed class CancellingEstimator(CancellationTokenSource source, decimal bound) : IStoryCostEstimator
    {
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            throw new InvalidOperationException("The writer must not use the whole-pipeline bound.");

        public Task<ErrorOr<UsageBound>> ReviewWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            WriterWorstCaseAsync(tier, language, ct);

        public async Task<ErrorOr<UsageBound>> WriterWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct)
        {
            await source.CancelAsync();
            ct.ThrowIfCancellationRequested();

            return new UsageBound(bound, 1, 1);
        }
    }

    [Fact]
    public async Task OpenAiStoryWriter_WriteAsync_UsageWithTheBound_KeepsTheBoundAsTheMeterFallback()
    {
        // CR-3: a call with reported usage still carries the writer bound, so the meter can fall back to it when
        // the usage cannot be priced (cached > input, model absent from the pinned price version).
        var miscounted = new TokenUsage(10, 50, 0, 5, 0);
        var client = new FakeTextClient().EnqueueOk(TextStep.Writer, Draft(), miscounted);
        var meter = new RecordingMeter();
        var writer = new OpenAiStoryWriter(new StubAssembler(), client, new StubCatalog(), meter, new StubEstimator(2.5m));

        await writer.WriteAsync(Request(), Ct);

        meter.Items.Single().ShouldSatisfyAllConditions(
            i => i.Estimated.ShouldBeFalse(),
            i => i.InputTokens.ShouldBe(10),
            i => i.CostUsd.ShouldBe(2.5m));
    }

    [Fact]
    public async Task OpenAiStoryWriter_WriteAsync_UsageWithoutABound_IsStillMetered()
    {
        var client = new FakeTextClient().EnqueueOk(TextStep.Writer, Draft(), Usage);
        var meter = new RecordingMeter();
        var writer = new OpenAiStoryWriter(new StubAssembler(), client, new StubCatalog(), meter, new StubEstimator(null));

        await writer.WriteAsync(Request(), Ct);

        meter.Items.Single().ShouldSatisfyAllConditions(i => i.Estimated.ShouldBeFalse(), i => i.CostUsd.ShouldBeNull());
    }

    [Fact]
    public async Task OpenAiStoryWriter_WriteAsync_UnavailableWithUsage_MetersTheReportedUsage()
    {
        // CR-2: a failed Responses body that reported usage is metered from it, not at the bound.
        var outcome = TextCallResult<StoryContentV1>.ProviderUnavailable("gpt-6-luna", "r9", Usage);
        var client = new FakeTextClient().Enqueue(TextStep.Writer, outcome);
        var meter = new RecordingMeter();
        var writer = new OpenAiStoryWriter(new StubAssembler(), client, new StubCatalog(), meter, new StubEstimator(1m));

        (await writer.WriteAsync(Request(), Ct)).ShouldBeSameAs(outcome);

        meter.Items.Single().ShouldSatisfyAllConditions(i => i.Estimated.ShouldBeFalse(), i => i.InputTokens.ShouldBe(1000), i => i.ProviderRequestId.ShouldBe("r9"));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    [InlineData(307)]
    public async Task OpenAiStoryWriter_WriteAsync_DefinitiveRejection_IsNotMetered(int status)
    {
        var outcome = TextCallResult<StoryContentV1>.ProviderError("gpt-6-luna", status, "r1");
        var meter = new RecordingMeter();
        var writer = new OpenAiStoryWriter(new StubAssembler(), new FakeTextClient().Enqueue(TextStep.Writer, outcome), new StubCatalog(), meter, new StubEstimator(1m));

        (await writer.WriteAsync(Request(), Ct)).ShouldBeSameAs(outcome);

        outcome.MayBeBilled.ShouldBeFalse();
        meter.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAiStoryWriter_WriteAsync_FundsExhausted_IsNotMetered()
    {
        var outcome = TextCallResult<StoryContentV1>.ProviderFundsExhausted("gpt-6-luna", null);
        var meter = new RecordingMeter();
        var writer = new OpenAiStoryWriter(new StubAssembler(), new FakeTextClient().Enqueue(TextStep.Writer, outcome), new StubCatalog(), meter, new StubEstimator(1m));

        (await writer.WriteAsync(Request(), Ct)).ShouldBeSameAs(outcome);

        meter.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(409)]
    [InlineData(500)]
    public async Task OpenAiStoryWriter_WriteAsync_BillablePlausibleProviderError_IsMeteredAtTheBound(int status)
    {
        var outcome = TextCallResult<StoryContentV1>.ProviderError("gpt-6-luna", status, null);
        var meter = new RecordingMeter();
        var writer = new OpenAiStoryWriter(new StubAssembler(), new FakeTextClient().Enqueue(TextStep.Writer, outcome), new StubCatalog(), meter, new StubEstimator(1m));

        await writer.WriteAsync(Request(), Ct);

        outcome.MayBeBilled.ShouldBeTrue();
        meter.Items.Single().ShouldSatisfyAllConditions(i => i.Estimated.ShouldBeTrue(), i => i.CostUsd.ShouldBe(1m));
    }

    [Fact]
    public async Task OpenAiStoryWriter_WriteAsync_CancelledAfterDispatch_RecordsAnEstimatedRowAndRethrows()
    {
        var req = Request();
        var meter = new RecordingMeter();
        var writer = new OpenAiStoryWriter(new StubAssembler(), new CancellingClient(dispatch: true), new StubCatalog(), meter, new StubEstimator(3m));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await writer.WriteAsync(req, cts.Token));

        var item = meter.Items.Single();
        item.ShouldSatisfyAllConditions(
            i => i.Estimated.ShouldBeTrue(),
            i => i.CostUsd.ShouldBe(3m),
            i => i.Model.ShouldBe("gpt-6-luna"),
            i => i.Operation.ShouldBe("story"),
            i => i.UserId.ShouldBe(req.Run.UserId),
            i => i.ReservationId.ShouldBe(req.Run.Reservation.Value));
        meter.Tokens.Single().CanBeCanceled.ShouldBeFalse("the settlement must not be cancelled with the caller");
    }

    [Fact]
    public async Task OpenAiStoryWriter_WriteAsync_CancelledBeforeDispatch_RecordsNothing()
    {
        var meter = new RecordingMeter();
        var writer = new OpenAiStoryWriter(new StubAssembler(), new CancellingClient(dispatch: false), new StubCatalog(), meter, new StubEstimator(3m));

        await Should.ThrowAsync<OperationCanceledException>(async () => await writer.WriteAsync(Request(), Ct));

        meter.Items.ShouldBeEmpty();
    }

    private sealed class CancellingClient(bool dispatch) : ITextClient
    {
        public ValueTask<TextCallResult<T>> CallAsync<T>(TextCallRequest req, CancellationToken ct)
            where T : class
        {
            if (dispatch)
            {
                req.MarkDispatched("gpt-6-luna");
            }

            throw new OperationCanceledException(ct);
        }
    }

    private static StoryContentV1 Draft() =>
        new("T", new StorySegmentV1("h", [], []), [], new StorySegmentV1("e", [], []), "d", [], [], new StoryStructureV1("a", "b", "c", "d", "e"));

    private static StoryWriteRequest Request() =>
        new(
            new RunContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new ReservationId(Guid.NewGuid()), GenerationTrigger.User, 1, "1"),
            new SeriesSnapshot(Guid.NewGuid(), Topic.Ghost, Language.Vi),
            new EpisodeSnapshot(Guid.NewGuid(), "brief", LengthTier.Standard, null),
            Length,
            null,
            null,
            [],
            null);

    private sealed class StubAssembler : IPromptAssembler
    {
        internal AssemblerContext? Context { get; private set; }

        public AssembledPrompt Assemble(StoryWriteRequest req, AssemblerContext ctx)
        {
            Context = ctx;

            return new AssembledPrompt("SYSTEM", "{\"data\":1}", "rel=test", "writer:ghost.vi.v1");
        }

        public AssembledPrompt Review(StoryReviewRequest req) =>
            new("REVIEW-SYSTEM", "{\"data\":2}", "rel=test;review=v1", "review:ghost.vi.v1");
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
            throw new InvalidOperationException("The writer must not depend on the whole-pipeline bound (CR-1).");

        public Task<ErrorOr<UsageBound>> ReviewWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            WriterWorstCaseAsync(tier, language, ct);

        public Task<ErrorOr<UsageBound>> WriterWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(bound is { } value ? new UsageBound(value, 1, 1) : Error.Failure("price_unavailable"));
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
