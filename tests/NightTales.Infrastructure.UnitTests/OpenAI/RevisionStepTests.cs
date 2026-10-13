// The Responses API of OpenAI 2.14.0 is marked experimental (OPENAI001); the adapter under test uses it.
#pragma warning disable OPENAI001
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Review;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Length;
using NightTales.Domain.Stories.Prompts;
using NightTales.Domain.Stories.Review;
using NightTales.Infrastructure.Prompts;
using NightTales.Infrastructure.UnitTests.Prompts;
using NightTales.TestKit.Fixtures;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.OpenAI;

/// <summary>
/// 02.6.2-T1: the revision step over the real adapter, assembler, embedded prompt catalog and <c>story.v1</c> schema.
/// The pipeline (<c>StoryGenerationPipeline</c>) owns the run-level effects of these outcomes (step 2-4 re-run, review #2,
/// the <c>revision</c> audit block); what this task owns is the writer's side: the call, the outcome as data, exactly one
/// meter row per billed call, and that the revision prompt carries the story and the issues as JSON data only.
/// </summary>
public sealed class RevisionStepTests
{
    private const string PromptKey = "revise/v1.md";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RevisionStep_Ok_MetersOnceWithTheRevisionOperation()
    {
        using var host = new OpenAiTestHost();
        var meter = new RecordingMeter();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(StoryJson()), ("x-request-id", "req_revision"));
        var req = Request();

        var result = await Writer(host, meter).ReviseAsync(req, Ct);

        result.ShouldBeOfType<TextCallOk<StoryContentV1>>().Draft.Title.ShouldBe("T");

        var row = meter.Items.ShouldHaveSingleItem();
        row.Operation.ShouldBe("story_revision");
        row.Model.ShouldBe(OpenAiTestHost.Model);
        row.InputTokens.ShouldNotBeNull();
        row.ProviderRequestId.ShouldBe("req_revision");
        row.Estimated.ShouldBeFalse();
        host.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RevisionStep_SendsTheRevisionSchemaStepAndCacheKey()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(StoryJson()));
        var req = Request();

        await Writer(host, new RecordingMeter()).ReviseAsync(req, Ct);

        var sent = host.Handler.Requests.ShouldHaveSingleItem();
        var body = sent.Json;

        body.GetProperty("model").GetString().ShouldBe(OpenAiTestHost.Model);
        body.GetProperty("max_output_tokens").GetInt32().ShouldBe(req.Length.RevisionMaxOutput);
        body.GetProperty("prompt_cache_key").GetString().ShouldBe("revision:ghost.en.v1");

        var format = body.GetProperty("text").GetProperty("format");
        format.GetProperty("type").GetString().ShouldBe("json_schema");
        format.GetProperty("strict").GetBoolean().ShouldBeTrue();
        format.GetProperty("name").GetString().ShouldBe("story_v1");
        format.GetProperty("schema").GetProperty("additionalProperties").ValueKind.ShouldBe(JsonValueKind.False);

        // The step rides in the client request id and the composed prompt version carries the revision key, never the review key.
        sent.Headers["X-Client-Request-Id"].ShouldBe($"{req.Run.GenerationRunId}:revision:1");
        body.GetProperty("input")[0].GetRawText().ShouldNotContain("{{");
    }

    [Fact]
    public async Task RevisionStep_InvalidOutput_ReturnsTheOutcomeAndMeters()
    {
        using var host = new OpenAiTestHost();
        var meter = new RecordingMeter();
        // A closed-schema violation parses as text and is only rejected by the validator (the pipeline treats both as InvalidOutput).
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed("""{"title":"only a title"}"""));

        var result = await Writer(host, meter).ReviseAsync(Request(), Ct);

        result.ShouldBeOfType<TextCallInvalidOutput<StoryContentV1>>();
        meter.Items.ShouldHaveSingleItem().Operation.ShouldBe("story_revision");
    }

    [Fact]
    public async Task RevisionStep_Moderated_ReturnsTheOutcomeWithoutTheRefusalTextAndMeters()
    {
        using var host = new OpenAiTestHost();
        var meter = new RecordingMeter();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Refusal());

        var result = await Writer(host, meter).ReviseAsync(Request(), Ct);

        var moderated = result.ShouldBeOfType<TextCallModerated<StoryContentV1>>();
        moderated.Categories.ShouldNotContain("REFUSAL-TEXT");
        meter.Items.ShouldHaveSingleItem().Operation.ShouldBe("story_revision");
    }

    [Fact]
    public async Task RevisionStep_FundsExhausted_IsNotMetered()
    {
        using var host = new OpenAiTestHost();
        var meter = new RecordingMeter();
        host.Handler.Reply(HttpStatusCode.TooManyRequests, OpenAiTestHost.Error("credit_balance_exhausted"));

        var result = await Writer(host, meter).ReviseAsync(Request(), Ct);

        result.ShouldBeOfType<TextCallFundsExhausted<StoryContentV1>>().MayBeBilled.ShouldBeFalse();
        meter.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task RevisionStep_Unavailable_MetersAnEstimatedRow()
    {
        using var host = new OpenAiTestHost();
        var meter = new RecordingMeter();
        host.Handler
            .Reply(HttpStatusCode.ServiceUnavailable, OpenAiTestHost.Error("server_error"))
            .Reply(HttpStatusCode.ServiceUnavailable, OpenAiTestHost.Error("server_error"));

        var result = await Writer(host, meter).ReviseAsync(Request(), Ct);

        result.ShouldBeOfType<TextCallUnavailable<StoryContentV1>>().MayBeBilled.ShouldBeTrue();
        var row = meter.Items.ShouldHaveSingleItem();
        row.Operation.ShouldBe("story_revision");
        row.Estimated.ShouldBeTrue();
        row.CostUsd.ShouldBe(0.25m);
    }

    [Fact]
    public async Task RevisionStep_DataObjectOverTheBudget_NeverCallsTheProviderOrMeters()
    {
        using var host = new OpenAiTestHost();
        var meter = new RecordingMeter();
        var catalog = new PromptCatalog(new EmbeddedPromptResourceSource());
        var assembler = new PromptAssembler(catalog, NullLogger<PromptAssembler>.Instance, new PromptDataBudget(500));
        var writer = new OpenAiStoryWriter(assembler, host.Client, catalog, meter, new FixedEstimator());

        var error = await Should.ThrowAsync<InvalidOperationException>(
            async () => await writer.ReviseAsync(Request(text: new string('a', 5_000)), Ct));

        error.Message.ShouldContain(ErrorCodes.PromptTooLarge);
        host.Handler.Requests.ShouldBeEmpty();
        meter.Items.ShouldBeEmpty();
    }

    // ---- the assembler side: only blocking objective issues, story and issues as JSON data only ----

    [Fact]
    public void RevisionStep_OnlyBlockingObjectiveIssuesAreSent()
    {
        var catalog = new PromptCatalog(new EmbeddedPromptResourceSource());
        var assembler = new PromptAssembler(catalog, NullLogger<PromptAssembler>.Instance);

        var prompt = assembler.Revise(Request(issues: MixedIssues())).Value;
        var issues = JsonDocument.Parse(prompt.UserDataJson).RootElement.GetProperty("issues");

        issues.EnumerateArray().Select(issue => issue.GetProperty("id").GetString())
            .ShouldBe(["i1", "i4"]);
        prompt.UserDataJson.ShouldNotContain("severity");
    }

    [Fact]
    public void RevisionStep_IssueStringsAreCleanedAndClampedOnCodePointBoundaries()
    {
        var catalog = new PromptCatalog(new EmbeddedPromptResourceSource());
        var assembler = new PromptAssembler(catalog, NullLogger<PromptAssembler>.Instance);
        // A C0 control character, a valid surrogate pair straddling the evidence cap and an over-long repair direction.
        var issues = new[]
        {
            new ReviewIssueV1("i1", "hook_missing", "blocking", "hook", "a\u0001b", "impact", new string('x', 400)),
            new ReviewIssueV1("i2", "hook_missing", "blocking", "hook", new string('a', 199) + "\uD83D\uDE00", "impact", "shorten"),
        };

        var data = JsonDocument.Parse(assembler.Revise(Request(issues: issues)).Value.UserDataJson).RootElement.GetProperty("issues");

        var first = data[0];
        first.GetProperty("evidence").GetString().ShouldBe("a b");
        first.GetProperty("repair_direction").GetString()!.Length.ShouldBe(ReviewResultValidator.MaxFieldChars);

        // The pair straddles the 200-code-unit cap, so the clamp backs off to 199 rather than splitting it.
        var second = data[1].GetProperty("evidence").GetString()!;
        second.Length.ShouldBe(199);
        second.ShouldNotContain("\uD83D");
        assembler.Revise(Request(issues: issues)).Value.UserDataJson.ShouldNotContain("\uFFFD");
    }

    [Fact]
    public void RevisionStep_HostileStoryAndIssues_ReachOnlyTheUserMessageAndNeverTheSystemPrompt()
    {
        // D6: the story, the issue evidence and the repair direction travel only as values inside the one JSON data object.
        var catalog = new PromptCatalog(new EmbeddedPromptResourceSource());
        var assembler = new PromptAssembler(catalog, NullLogger<PromptAssembler>.Instance);
        var baseline = assembler.Revise(Request()).Value;
        var baselineData = JsonDocument.Parse(baseline.UserDataJson).RootElement;
        var fixtures = InjectionFixture.LoadAll(InjectionFixture.RevisionStep);
        fixtures.ShouldNotBeEmpty();

        foreach (var fixture in fixtures)
        {
            var hostile = fixture.Field.StartsWith("issues", StringComparison.Ordinal)
                ? Request(issues: [new ReviewIssueV1("i1", "hook_missing", "blocking", "hook", fixture.Value, "impact", "shorten")])
                : Request(text: fixture.Value);
            var prompt = assembler.Revise(hostile).Value;
            var data = JsonDocument.Parse(prompt.UserDataJson).RootElement;

            prompt.SystemPrompt.ShouldNotContain(fixture.Canary);
            prompt.SystemPrompt.ShouldBe(baseline.SystemPrompt, fixture.Id);
            foreach (var control in new[] { "task", "language", "tier", "length", "facts" })
            {
                data.GetProperty(control).GetRawText().ShouldBe(baselineData.GetProperty(control).GetRawText(), $"{fixture.Id}: {control}");
            }

            data.GetProperty("task").GetString().ShouldBe("revise_story");
            data.GetRawText().ShouldContain(fixture.Canary);
        }
    }

    [Fact]
    public void RevisionStep_DataObjectOverThreeTimesTheBudget_IsATypedErrorAndNothingIsSent()
    {
        // S4: the worst-case estimate bounds the revision input at three times Story:Prompt:MaxDataObjectBytes.
        var catalog = new PromptCatalog(new EmbeddedPromptResourceSource());
        var fitting = new PromptAssembler(catalog, NullLogger<PromptAssembler>.Instance)
            .Revise(Request(text: new string('a', 1_000))).Value.UserDataJson;
        var budget = new PromptDataBudget((Encoding.UTF8.GetByteCount(fitting) + 2) / 3);
        var assembler = new PromptAssembler(catalog, NullLogger<PromptAssembler>.Instance, budget);

        var atBound = assembler.Revise(Request(text: new string('a', 1_000)));
        var over = assembler.Revise(Request(text: new string('a', 1_100)));

        atBound.IsError.ShouldBeFalse();
        Encoding.UTF8.GetByteCount(atBound.Value.UserDataJson).ShouldBeLessThanOrEqualTo(3 * budget.MaxDataObjectBytes);
        over.IsError.ShouldBeTrue();
        over.FirstError.Code.ShouldBe(ErrorCodes.PromptTooLarge);
    }

    [Theory]
    [InlineData(Language.En)]
    [InlineData(Language.Vi)]
    public void RevisionStep_SystemPrompt_CarriesTheEarRulesOfItsLanguage(Language language)
    {
        var catalog = new PromptCatalog(new EmbeddedPromptResourceSource());
        var assembler = new PromptAssembler(catalog, NullLogger<PromptAssembler>.Instance);
        var list = catalog.GetShared(PromptFile.EarRules, language);
        list.Trim().ShouldNotBeEmpty();

        var system = assembler.Revise(Request(language: language)).Value.SystemPrompt;

        system.ShouldContain(list.Trim());
        system.ShouldNotContain("{{");
        var other = language == Language.En ? Language.Vi : Language.En;
        system.ShouldNotContain(catalog.GetShared(PromptFile.EarRules, other).Trim());
    }

    // ---- manifest binding of the revision prompt (task acceptance check 1) ----

    [Fact]
    public void RevisionManifest_ListsThePromptWithItsRealSha256()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(PromptsTree.Root, "manifest.json")));
        var entry = manifest.RootElement.GetProperty("files").GetProperty(PromptKey);

        entry.GetProperty("version").GetString().ShouldBe("v1");
        entry.GetProperty("sha256").GetString()
            .ShouldBe(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(PromptsTree.Root, PromptKey)))));
    }

    [Fact]
    public void RevisionManifest_OneChangedByteFailsStartUpNamingTheFile()
    {
        var tree = PromptsTree.Read();
        var original = tree[PromptKey].ToArray();
        tree[PromptKey][original.Length / 2] = (byte)(original[original.Length / 2] == (byte)'x' ? 'y' : 'x');
        SHA256.HashData(tree[PromptKey]).ShouldNotBe(SHA256.HashData(original));

        PromptsTree.Failure(tree).Message.ShouldContain($"sha256 mismatch for {PromptKey}");
    }

    [Fact]
    public void RevisionManifest_EmbeddedCatalogServesTheRevisionPrompt()
    {
        var catalog = new PromptCatalog(new EmbeddedPromptResourceSource());

        catalog.GetStepPrompt(PromptStep.Revision).ShouldNotBeNullOrWhiteSpace();
    }

    // ---- helpers ----

    private static OpenAiStoryWriter Writer(OpenAiTestHost host, RecordingMeter meter)
    {
        var catalog = new PromptCatalog(new EmbeddedPromptResourceSource());

        return new OpenAiStoryWriter(new PromptAssembler(catalog, NullLogger<PromptAssembler>.Instance), host.Client, catalog, meter, new FixedEstimator());
    }

    private static StoryRevisionRequest Request(
        IReadOnlyList<ReviewIssueV1>? issues = null,
        string text = "The corridor was quiet.",
        Language language = Language.En)
    {
        var segment = new StorySegmentV1(text, ["hall"], []);
        var draft = new StoryContentV1(
            "T",
            segment,
            [segment, segment],
            segment,
            "d",
            ["ghost"],
            [],
            new StoryStructureV1("haunted_house", "house", "cold", "reveal", "first"));
        var toRepair = issues ?? [new ReviewIssueV1("i1", "hook_missing", "blocking", "hook", "the hook is flat", "the listener waits", "open on the fear")];
        var review = new StoryReviewV1(new ReviewScores(4, 4, 4, 4, 4, null, 4), toRepair, [], null, [], "solid draft");

        return new StoryRevisionRequest(
            new RunContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new ReservationId(Guid.NewGuid()), GenerationTrigger.User, 1, "1"),
            draft,
            review,
            toRepair,
            language,
            LengthTier.Standard,
            new LengthTarget(1300, 2080, 20, 32, 6000, 2500, 6000),
            Topic.Ghost,
            [new FactForWriter("f1", "A statement.", [])],
            null,
            "story/ghost.en.v1.md");
    }

    /// <summary>The five D22 issue kinds the caller might pass: two blocking objective, one advisory objective, one blocking taste, one advisory taste.</summary>
    private static IReadOnlyList<ReviewIssueV1> MixedIssues() =>
    [
        new ReviewIssueV1("i1", "hook_missing", "blocking", "hook", "the hook is flat", "the listener waits", "open on the fear"),
        new ReviewIssueV1("i2", "length_out_of_range", "advisory", "meta", "short", "too short", "add a beat"),
        new ReviewIssueV1("i3", "pacing_issue", "blocking", "p:1", "slow", "the middle drags", "cut a beat"),
        new ReviewIssueV1("i4", ReviewIssueTypes.FactualCareMissing, "blocking", "meta", "no fact sheet", "unverified", "add the source"),
        new ReviewIssueV1("i5", "tone_violation", "advisory", "p:0", "flat line", "breaks the mood", "shorten"),
    ];

    private static string StoryJson() =>
        """
        {"title":"T","hook":{"text":"h","keywords":["door"],"fact_refs":[]},
         "paragraphs":[{"text":"p","keywords":["hall"],"fact_refs":[],"beat":"setup"}],
         "ending":{"text":"e","keywords":["night"],"fact_refs":[]},
         "description":"d","tags":["ghost"],"content_warnings":[],
         "structure":{"sub_genre":"haunted_house","setting":"house","opening":"cold","twist_type":"reveal","narrator_mode":"first"}}
        """;

    private sealed class RecordingMeter : IUsageMeter
    {
        internal List<UsageItem> Items { get; } = [];

        public Task RecordAsync(UsageItem item, CancellationToken ct)
        {
            Items.Add(item);

            return Task.CompletedTask;
        }
    }

    private sealed class FixedEstimator : IStoryCostEstimator
    {
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            throw new InvalidOperationException("The revision step must not use the whole-pipeline bound.");

        public Task<ErrorOr<UsageBound>> WriterWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            throw new InvalidOperationException("The revision step must not use the writer bound.");

        public Task<ErrorOr<UsageBound>> ReviewWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            throw new InvalidOperationException("The revision step must not use the review bound.");

        public Task<ErrorOr<UsageBound>> RevisionWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(0.25m, 1, 1));
    }
}
