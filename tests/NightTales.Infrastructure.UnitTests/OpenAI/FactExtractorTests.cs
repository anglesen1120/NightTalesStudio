using System.Text;
using System.Text.Json;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.Research.Facts;
using NightTales.Domain.Common;
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
        var extractor = new OpenAiFactExtractor(client, catalog);

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

        await new OpenAiFactExtractor(client, new StubCatalog())
            .ExtractAsync(Request(Source()) with { Language = language }, Ct);

        client.Requests.Single().PromptCacheKey.ShouldBe(expected);
        JsonDocument.Parse(client.Requests.Single().UserDataJson).RootElement.GetProperty("language").GetString()
            .ShouldBe(expected[(expected.IndexOf(':') + 1)..]);
    }

    [Fact]
    public async Task OpenAiFactExtractor_NeverSerialisesSourceId_OrUrl()
    {
        var client = new FakeTextClient().EnqueueOk(TextStep.Facts, Draft());
        var extractor = new OpenAiFactExtractor(client, new StubCatalog());

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

        var result = await new OpenAiFactExtractor(new FakeTextClient().Enqueue(TextStep.Facts, outcome), new StubCatalog())
            .ExtractAsync(Request(Source()), Ct);

        result.ShouldBeSameAs(outcome);
    }

    [Fact]
    public async Task OpenAiFactExtractor_ReturnsModeratedUntouched()
    {
        var outcome = TextCallResult<FactsV1>.Moderated("gpt-6-luna", null, "r2", ["violence"]);

        var result = await new OpenAiFactExtractor(new FakeTextClient().Enqueue(TextStep.Facts, outcome), new StubCatalog())
            .ExtractAsync(Request(Source()), Ct);

        result.ShouldBeSameAs(outcome);
    }

    [Fact]
    public async Task OpenAiFactExtractor_CancellationIsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await new OpenAiFactExtractor(new CancellingTextClient(), new StubCatalog())
                .ExtractAsync(Request(Source()), cts.Token));
    }

    [Fact]
    public async Task OpenAiFactExtractor_EmptySources_Throws()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await new OpenAiFactExtractor(new FakeTextClient(), new StubCatalog())
                .ExtractAsync(Request(), Ct));
    }

    [Fact]
    public async Task OpenAiFactExtractor_DuplicateAlias_Throws()
    {
        await Should.ThrowAsync<ArgumentException>(
            async () => await new OpenAiFactExtractor(new FakeTextClient(), new StubCatalog())
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
            async () => await new OpenAiFactExtractor(new FakeTextClient(), new StubCatalog())
                .ExtractAsync(Request(Source() with { Alias = alias }), Ct));
    }

    [Fact]
    public async Task OpenAiFactExtractor_MoreThanTenSources_Throws()
    {
        var sources = Enumerable.Range(1, 11)
            .Select(n => new FactSourceInput($"s{n}", Guid.NewGuid(), "t", "text", false))
            .ToArray();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await new OpenAiFactExtractor(new FakeTextClient(), new StubCatalog())
                .ExtractAsync(Request(sources), Ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task OpenAiFactExtractor_NonPositiveMaxOutputTokens_Throws(int maxOutputTokens)
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await new OpenAiFactExtractor(new FakeTextClient(), new StubCatalog())
                .ExtractAsync(Request(Source()) with { MaxOutputTokens = maxOutputTokens }, Ct));
    }

    [Fact]
    public async Task FactsInjectionFixture_ProducesSchemaValidOutput_IgnoringInstruction()
    {
        // D6/F21: hostile source text travels only as a value inside the one JSON data object, never into the
        // static system prompt and never as a structure break-out. No facts-specific fixture ships yet
        // (prompts/fixtures/injection/* is writer/review scoped, owned by 02.2.3-T2), so the writer and review
        // payloads stand in as hostile source text and a facts-specific case is requested from 02.2.3-T2.
        var fixtures = InjectionFixture.LoadAll(InjectionFixture.WriterStep)
            .Concat(InjectionFixture.LoadAll(InjectionFixture.ReviewStep))
            .ToArray();
        fixtures.ShouldNotBeEmpty();

        foreach (var fixture in fixtures)
        {
            var client = new FakeTextClient().EnqueueOk(TextStep.Facts, Draft());
            var extractor = new OpenAiFactExtractor(client, new StubCatalog());
            var source = new FactSourceInput("s1", SourceId, "Title", fixture.Value, false);

            var result = await extractor.ExtractAsync(Request(source), Ct);

            var call = client.Requests.ShouldHaveSingleItem();
            call.SystemPrompt.ShouldNotContain(fixture.Canary);

            var root = JsonDocument.Parse(call.UserDataJson).RootElement;
            root.EnumerateObject().Select(property => property.Name).ShouldBe(["task", "language", "sources"]);
            root.GetProperty("task").GetString().ShouldBe("extract_facts");
            var sources = root.GetProperty("sources");
            sources.GetArrayLength().ShouldBe(1);
            var sent = sources[0];
            sent.EnumerateObject().Select(property => property.Name).ShouldBe(["alias", "title", "text"]);
            // The payload survives verbatim inside the text string value and breaks nothing around it.
            sent.GetProperty("text").GetString().ShouldNotBeNull().ShouldContain(fixture.Canary);
            call.UserDataJson.ShouldNotContain(SourceId.ToString());
            call.UserDataJson.ShouldNotContain("\"url\"");
            call.UserDataJson.ShouldNotContain("\"source_id\"");

            // The model ignored the instruction: schema-valid facts, and the canary is nowhere in the output.
            var ok = result.ShouldBeOfType<TextCallOk<FactsV1>>();
            ok.Draft.Facts.ShouldAllBe(fact => !fact.Statement.Contains(fixture.Canary, StringComparison.Ordinal));
        }
    }

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

        public ReadOnlyMemory<byte> GetSchema(string name) => SchemaBytes;

        public string BuildPromptVersion(PromptVersionParts parts)
        {
            Parts = parts;

            return "rel=test";
        }
    }

    private sealed class CancellingTextClient : ITextClient
    {
        public ValueTask<TextCallResult<T>> CallAsync<T>(TextCallRequest req, CancellationToken ct)
            where T : class =>
            ValueTask.FromException<TextCallResult<T>>(new OperationCanceledException(ct));
    }
}
