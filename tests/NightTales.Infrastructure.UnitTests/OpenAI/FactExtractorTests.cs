using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Research.Facts;
using NightTales.Application.Features.Research.Jobs;
using NightTales.Domain.Common;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Prompts;
using NightTales.Infrastructure.OpenAI;
using NightTales.TestKit.Fixtures;
using NightTales.TestKit.Text;
using Shouldly;
using Xunit;

// The namespace carries `Research.Facts` on purpose: `dotnet test --filter FullyQualifiedName~Research.Facts`
// (02.5.3-T1 acceptance) must select these tests, which live in the OpenAI folder beside the adapter they cover.
namespace NightTales.Infrastructure.UnitTests.Research.Facts;

/// <summary>
/// 02.5.3-T1: the fact-extraction step over a scripted <see cref="ITextClient"/> — the data object it builds
/// (alias/title/text only, never a source id or a URL), the <c>facts.v1</c> schema name and the
/// <see cref="TextStep.Facts"/> step, the untouched provider outcomes, and the prompt-injection fixtures.
/// </summary>
public sealed class FactExtractorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid SourceId = Guid.Parse("0197a000-0000-7000-8000-00000000f001");

    [Fact]
    public async Task OpenAiFactExtractor_UsesSchemaNameFactsV1_AndStepFacts()
    {
        var catalog = new StubCatalog();
        var draft = Draft();
        var client = new FakeTextClient().EnqueueOk(TextStep.Facts, draft);
        var extractor = Extractor(client, catalog, FactsOptionsFor());

        var result = await extractor.ExtractAsync(Request(Source()), Ct);

        result.ShouldBeOfType<TextCallOk<FactsV1>>().Draft.ShouldBeSameAs(draft);
        var call = client.Requests.ShouldHaveSingleItem();
        call.Step.ShouldBe(TextStep.Facts);
        call.SchemaName.ShouldBe("facts.v1");
        call.Schema.ToArray().ShouldBe(StubCatalog.SchemaBytes);
        call.SystemPrompt.ShouldBe(StubCatalog.Prompt);
        call.MaxOutputTokens.ShouldBe(4_000);
        call.PromptCacheKey.ShouldBe("facts:vi");
        call.PromptVersion.ShouldBe("rel=test");
        var parts = catalog.Parts.ShouldNotBeNull();
        parts.SystemKey.ShouldBe("facts/v1.md");
        parts.SchemaKey.ShouldBe("schemas/facts.v1.json");
    }

    [Theory]
    [InlineData(Language.Vi, "facts:vi")]
    [InlineData(Language.En, "facts:en")]
    public async Task OpenAiFactExtractor_CacheKeyCarriesTheLanguage(Language language, string expected)
    {
        var client = new FakeTextClient().EnqueueOk(TextStep.Facts, Draft());

        await Extractor(client, new StubCatalog(), FactsOptionsFor())
            .ExtractAsync(Request(Source()) with { Language = language }, Ct);

        client.Requests.Single().PromptCacheKey.ShouldBe(expected);
        JsonDocument.Parse(client.Requests.Single().UserDataJson).RootElement.GetProperty("language").GetString()
            .ShouldBe(expected[(expected.IndexOf(':') + 1)..]);
    }

    [Fact]
    public async Task OpenAiFactExtractor_NeverSerialisesSourceId_OrUrl()
    {
        var client = new FakeTextClient().EnqueueOk(TextStep.Facts, Draft());
        var extractor = Extractor(client, new StubCatalog(), FactsOptionsFor());

        await extractor.ExtractAsync(Request(Source()), Ct);

        var json = client.Requests.Single().UserDataJson;
        json.ShouldNotContain(SourceId.ToString());
        json.ShouldNotContain("source_id");
        json.ShouldNotContain("sourceId");
        json.ShouldNotContain("is_user_note");
        json.ShouldNotContain("user_note");
        json.ShouldNotContain("\"url\"");

        var root = JsonDocument.Parse(json).RootElement;
        root.EnumerateObject().Select(property => property.Name).ShouldBe(["task", "language", "sources"]);
        root.GetProperty("task").GetString().ShouldBe("extract_facts");
        root.GetProperty("language").GetString().ShouldBe("vi");
        var sources = root.GetProperty("sources");
        sources.GetArrayLength().ShouldBe(1);
        var source = sources[0];
        source.EnumerateObject().Select(property => property.Name).ShouldBe(["alias", "title", "text"]);
        source.GetProperty("alias").GetString().ShouldBe("s1");
        source.GetProperty("title").GetString().ShouldBe("Source one");
        source.GetProperty("text").GetString().ShouldBe("The source text.");
    }

    [Fact]
    public async Task OpenAiFactExtractor_ReturnsInvalidOutputUntouched()
    {
        var outcome = TextCallResult<FactsV1>.InvalidOutput("gpt-6-luna", null, "r1", [TextCallInvalidOutput<FactsV1>.SchemaInvalid]);

        var result = await Extractor(new FakeTextClient().Enqueue(TextStep.Facts, outcome), new StubCatalog(), FactsOptionsFor())
            .ExtractAsync(Request(Source()), Ct);

        result.ShouldBeSameAs(outcome);
    }

    [Fact]
    public async Task OpenAiFactExtractor_ReturnsModeratedUntouched()
    {
        var outcome = TextCallResult<FactsV1>.Moderated("gpt-6-luna", null, "r2", ["violence"]);

        var result = await Extractor(new FakeTextClient().Enqueue(TextStep.Facts, outcome), new StubCatalog(), FactsOptionsFor())
            .ExtractAsync(Request(Source()), Ct);

        result.ShouldBeSameAs(outcome);
    }

    [Fact]
    public async Task OpenAiFactExtractor_CancellationIsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await Extractor(new CancellingTextClient(), new StubCatalog(), FactsOptionsFor())
                .ExtractAsync(Request(Source()), cts.Token));
    }

    [Fact]
    public async Task OpenAiFactExtractor_EmptySources_Throws()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await Extractor(new FakeTextClient(), new StubCatalog(), FactsOptionsFor())
                .ExtractAsync(Request(), Ct));
    }

    [Fact]
    public async Task OpenAiFactExtractor_DuplicateAlias_Throws()
    {
        await Should.ThrowAsync<ArgumentException>(
            async () => await Extractor(new FakeTextClient(), new StubCatalog(), FactsOptionsFor())
                .ExtractAsync(Request(Source(), Source() with { Alias = "s1" }), Ct));
    }

    [Theory]
    [InlineData("s0")]
    [InlineData("s11")]
    [InlineData("S1")]
    [InlineData("source1")]
    public async Task OpenAiFactExtractor_AliasOutsideS1ToS10_Throws(string alias)
    {
        await Should.ThrowAsync<ArgumentException>(
            async () => await Extractor(new FakeTextClient(), new StubCatalog(), FactsOptionsFor())
                .ExtractAsync(Request(Source() with { Alias = alias }), Ct));
    }

    [Fact]
    public async Task OpenAiFactExtractor_MoreThanTenSources_Throws()
    {
        var sources = Enumerable.Range(1, 11)
            .Select(n => new FactSourceInput($"s{n}", Guid.NewGuid(), "t", "text", false))
            .ToArray();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await Extractor(new FakeTextClient(), new StubCatalog(), FactsOptionsFor())
                .ExtractAsync(Request(sources), Ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task OpenAiFactExtractor_NonPositiveMaxOutputTokens_Throws(int maxOutputTokens)
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await Extractor(new FakeTextClient(), new StubCatalog(), FactsOptionsFor())
                .ExtractAsync(Request(Source()) with { MaxOutputTokens = maxOutputTokens }, Ct));
    }

    [Fact]
    public async Task FactsInjectionFixture_KeepsHostileTextInsideTheDataObjectStringValue()
    {
        // D6/F21: hostile source text travels only as a value inside the one JSON data object, never as a
        // structure break-out. No facts-specific fixture ships yet (prompts/fixtures/injection/* is
        // writer/review scoped, owned by 02.2.3-T2), so the writer and review payloads stand in as hostile
        // source text and a facts-specific case is requested from 02.2.3-T2.
        var fixtures = InjectionFixture.LoadAll(InjectionFixture.WriterStep)
            .Concat(InjectionFixture.LoadAll(InjectionFixture.ReviewStep))
            .ToArray();
        fixtures.ShouldNotBeEmpty();

        foreach (var fixture in fixtures)
        {
            var client = new FakeTextClient().EnqueueOk(TextStep.Facts, Draft());
            var extractor = Extractor(client, new StubCatalog(), FactsOptionsFor());
            var source = new FactSourceInput("s1", SourceId, "Title", fixture.Value, false);

            await extractor.ExtractAsync(Request(source), Ct);

            var call = client.Requests.ShouldHaveSingleItem();

            // The data object keeps its exact shape whatever the payload held (task not overridden, one
            // source with exactly alias/title/text), and the payload survives verbatim inside the text
            // string value without breaking anything around it.
            var root = JsonDocument.Parse(call.UserDataJson).RootElement;
            root.EnumerateObject().Select(property => property.Name).ShouldBe(["task", "language", "sources"]);
            root.GetProperty("task").GetString().ShouldBe("extract_facts");
            var sources = root.GetProperty("sources");
            sources.GetArrayLength().ShouldBe(1);
            var sent = sources[0];
            sent.EnumerateObject().Select(property => property.Name).ShouldBe(["alias", "title", "text"]);
            sent.GetProperty("text").GetString().ShouldNotBeNull().ShouldContain(fixture.Canary);
            call.UserDataJson.ShouldNotContain(SourceId.ToString());
            call.UserDataJson.ShouldNotContain("\"url\"");
            call.UserDataJson.ShouldNotContain("\"source_id\"");
        }
    }

    [Fact]
    public async Task OpenAiFactExtractor_InputBudget_RejectsAnOverBudgetRequestBeforeTheCall()
    {
        var client = new FakeTextClient().EnqueueOk(TextStep.Facts, Draft());
        var source = Source();
        var estimate = FactInputBudget.EstimateTokens(FactDataObjectJson.Serialize(Language.Vi, [source]));
        var extractor = Extractor(
            client,
            new StubCatalog(),
            FactsOptionsFor(new FactsOptions { MaxInputTokens = estimate - 1 }));

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await extractor.ExtractAsync(Request(source), Ct));

        // F-1: the cap is enforced before the text client is touched, so an over-budget request never bills.
        client.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAiFactExtractor_InputBudget_AcceptsARequestAtExactlyTheCap()
    {
        var client = new FakeTextClient().EnqueueOk(TextStep.Facts, Draft());
        var source = Source();
        var estimate = FactInputBudget.EstimateTokens(FactDataObjectJson.Serialize(Language.Vi, [source]));
        var extractor = Extractor(
            client,
            new StubCatalog(),
            FactsOptionsFor(new FactsOptions { MaxInputTokens = estimate }));

        await extractor.ExtractAsync(Request(source), Ct);

        client.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task OpenAiFactExtractor_MaxSourcesBelowTheAliasCeiling_IsEnforced()
    {
        var sources = new[]
        {
            new FactSourceInput("s1", Guid.NewGuid(), "t1", "one", false),
            new FactSourceInput("s2", Guid.NewGuid(), "t2", "two", false),
            new FactSourceInput("s3", Guid.NewGuid(), "t3", "three", false),
        };

        var rejected = new FakeTextClient();
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await Extractor(rejected, new StubCatalog(), FactsOptionsFor(new FactsOptions { MaxSources = 2 }))
                .ExtractAsync(Request(sources), Ct));
        rejected.Requests.ShouldBeEmpty();

        // Exactly MaxSources is still accepted: the bound tightens the s1..s10 ceiling, it does not ban calls.
        var accepted = new FakeTextClient().EnqueueOk(TextStep.Facts, Draft());
        await Extractor(accepted, new StubCatalog(), FactsOptionsFor(new FactsOptions { MaxSources = 2 }))
            .ExtractAsync(Request(sources[..2]), Ct);
        accepted.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task OpenAiFactExtractor_CancelledAfterDispatch_RecordsTheBound_ThenRethrows()
    {
        var meter = new RecordingMeter();
        var estimator = new StubEstimator(3m);
        var extractor = new OpenAiFactExtractor(new CancellingTextClient(dispatch: true), new StubCatalog(), FactsOptionsFor(), meter, estimator);
        var request = Request(Source());

        await Should.ThrowAsync<OperationCanceledException>(async () => await extractor.ExtractAsync(request, Ct));

        var item = meter.Items.ShouldHaveSingleItem();
        item.ShouldSatisfyAllConditions(
            i => i.Estimated.ShouldBeTrue("the response never arrived, so the row carries the D1 bound"),
            i => i.CostUsd.ShouldBe(3m),
            i => i.Model.ShouldBe("gpt-6-luna"),
            i => i.Operation.ShouldBe(OpenAiFactExtractor.Operation),
            i => i.UserId.ShouldBe(request.Run.UserId),
            i => i.ReservationId.ShouldBe(request.Run.Reservation.Value),
            i => i.EpisodeId.ShouldBe(request.Run.EpisodeId),
            i => i.GenerationRunId.ShouldBe(request.Run.GenerationRunId),
            i => i.InputTokens.ShouldBeNull());
        meter.Tokens.ShouldHaveSingleItem().CanBeCanceled.ShouldBeFalse("the settlement must survive the caller's cancellation");
    }

    [Fact]
    public async Task OpenAiFactExtractor_CancelledBeforeDispatch_RecordsNothing()
    {
        // Nothing was sent, so nothing can have been billed: no meter call and no bound lookup (SEC-2).
        var meter = new RecordingMeter();
        var estimator = new StubEstimator(3m);
        var extractor = new OpenAiFactExtractor(new CancellingTextClient(dispatch: false), new StubCatalog(), FactsOptionsFor(), meter, estimator);

        await Should.ThrowAsync<OperationCanceledException>(async () => await extractor.ExtractAsync(Request(Source()), Ct));

        meter.Items.ShouldBeEmpty();
        estimator.Calls.ShouldBe(0);
    }

    [Fact]
    public void Operation_IsTheOneThePipelineMetersEveryOtherOutcomeUnder() =>
        OpenAiFactExtractor.Operation.ShouldBe(GenerateFactSheetPipeline.Operation);

    private static OpenAiFactExtractor Extractor(ITextClient client, IPromptCatalog catalog, IOptions<FactsOptions> options) =>
        new(client, catalog, options, new RecordingMeter(), new StubEstimator(3m));

    private static IOptions<FactsOptions> FactsOptionsFor(FactsOptions? options = null) =>
        Microsoft.Extensions.Options.Options.Create(options ?? new FactsOptions());

    private static FactSourceInput Source() => new("s1", SourceId, "Source one", "The source text.", false);

    private static FactsV1 Draft() => new([new FactDraftV1("A statement.", ["s1"], FactConfidence.High)]);

    private static FactExtractionRequest Request(params FactSourceInput[] sources) =>
        new(
            new RunContext(
                Guid.Parse("0197a000-0000-7000-8000-000000000001"),
                Guid.Parse("0197a000-0000-7000-8000-000000000002"),
                Guid.Parse("0197a000-0000-7000-8000-000000000003"),
                Guid.Parse("0197a000-0000-7000-8000-000000000004"),
                new ReservationId(Guid.Parse("0197a000-0000-7000-8000-000000000005")),
                GenerationTrigger.User,
                1,
                "1"),
            Language.Vi,
            sources,
            4_000);

    /// <summary>A prompt catalog whose two facts files are canned strings; the real one is 02.2.3-T1's.</summary>
    private sealed class StubCatalog : IPromptCatalog
    {
        internal const string Prompt = "FACTS-SYSTEM-PROMPT";
        internal static readonly byte[] SchemaBytes = Encoding.UTF8.GetBytes("""{"type":"object","additionalProperties":false,"required":["facts"]}""");

        internal PromptVersionParts? Parts { get; private set; }

        public string Release => "test";

        public IReadOnlyDictionary<string, PromptFileInfo> Files { get; } = new Dictionary<string, PromptFileInfo>(StringComparer.Ordinal);

        public string GetSystemPrompt() => Prompt;

        public string GetPreset(Topic topic, Language language) => Prompt;

        public string GetShared(PromptFile file, Language language) => Prompt;

        public string GetStepPrompt(PromptStep promptStep) => Prompt;

        public AttestationText GetAttestation(Language language) => new(string.Empty, string.Empty);

        public ReadOnlyMemory<byte> GetSchema(string name) => SchemaBytes;

        public string BuildPromptVersion(PromptVersionParts parts)
        {
            Parts = parts;

            return "rel=test";
        }
    }

    /// <summary>A client whose call is cancelled by its caller, dispatched or not (the SEC-2 seam).</summary>
    private sealed class CancellingTextClient(bool dispatch = false) : ITextClient
    {
        public ValueTask<TextCallResult<T>> CallAsync<T>(TextCallRequest req, CancellationToken ct)
            where T : class
        {
            if (dispatch)
            {
                req.MarkDispatched("gpt-6-luna");
            }

            return ValueTask.FromException<TextCallResult<T>>(new OperationCanceledException(ct));
        }
    }

    /// <summary>The meter port of the SEC-2 cases: what was written, and on which token.</summary>
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

    private sealed class StubEstimator(decimal bound) : IFactSheetCostEstimator
    {
        internal int Calls { get; private set; }

        public Task<ErrorOr<UsageBound>> WorstCaseAsync(CancellationToken ct)
        {
            Calls++;

            return Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(bound, 1_000, 500));
        }
    }
}
