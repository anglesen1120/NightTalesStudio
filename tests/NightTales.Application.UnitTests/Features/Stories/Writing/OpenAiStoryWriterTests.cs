using ErrorOr;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
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
            TextCallResult<StoryContentV1>.ProviderError("gpt-6-luna", 400, "r4"),
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
    }

    private sealed class RecordingMeter : IUsageMeter
    {
        internal List<UsageItem> Items { get; } = [];

        public Task RecordAsync(UsageItem item, CancellationToken ct)
        {
            Items.Add(item);

            return Task.CompletedTask;
        }
    }

    private sealed class StubEstimator(decimal? bound) : IStoryCostEstimator
    {
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(bound is { } value ? new UsageBound(value, 1, 1) : Error.Failure("price_unavailable"));
    }

    private sealed class StubCatalog : IPromptCatalog
    {
        public string Release => "test";

        public IReadOnlyDictionary<string, PromptFileInfo> Files => throw new NotSupportedException();

        public ReadOnlyMemory<byte> GetSchema(string name) =>
            name == "story.v1.json" ? System.Text.Encoding.UTF8.GetBytes("{\"schema\":true}") : throw new NotSupportedException(name);

        public string GetSystemPrompt() => throw new NotSupportedException();

        public string GetPreset(Topic topic, Language language) => throw new NotSupportedException();

        public string GetShared(PromptFile file, Language language) => throw new NotSupportedException();

        public string GetStepPrompt(PromptStep promptStep) => throw new NotSupportedException();

        public string BuildPromptVersion(PromptVersionParts parts) => throw new NotSupportedException();
    }
}
