// The Responses API of OpenAI 2.14.0 is marked experimental (OPENAI001); the adapter under test uses it.
#pragma warning disable OPENAI001
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Money;
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
using NightTales.Domain.Stories.Review;
using NightTales.Infrastructure.Prompts;
using NightTales.Infrastructure.UnitTests.Prompts;
using NightTales.TestKit.Fixtures;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.OpenAI;

/// <summary>
/// 02.6.1-T1 QA: the shipped <c>review.v1</c> schema document, the manifest binding of the review prompt and schema, the
/// request the adapter puts on the wire, and what the whole pipeline (adapter + validator) does with malformed model output.
/// </summary>
public sealed class ReviewQaTests
{
    private const string PromptKey = "review/v1.md";
    private const string SchemaKey = "schemas/review.v1.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonDocument Schema() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(PromptsTree.Root, SchemaKey)));

    private static IEnumerable<(string Path, JsonElement Node)> Walk(JsonElement node, string path = "$")
    {
        yield return (path, node);

        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                foreach (var child in Walk(property.Value, path + "." + property.Name))
                {
                    yield return child;
                }
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in node.EnumerateArray())
            {
                foreach (var child in Walk(item, $"{path}[{index++}]"))
                {
                    yield return child;
                }
            }
        }
    }

    private static bool IsObjectSchema(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("type", out var type))
        {
            return false;
        }

        return type.ValueKind == JsonValueKind.String
            ? type.GetString() == "object"
            : type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(item => item.GetString() == "object");
    }

    [Fact]
    public void ReviewSchema_EveryObjectIsClosedAndRequiresEveryOfItsProperties()
    {
        using var schema = Schema();
        var objects = Walk(schema.RootElement).Where(entry => IsObjectSchema(entry.Node)).ToList();

        objects.Count.ShouldBeGreaterThanOrEqualTo(7); // root, scores, issue, answer, factual_care, claim, real-world claim
        foreach (var (path, node) in objects)
        {
            node.TryGetProperty("additionalProperties", out var extra).ShouldBeTrue(path);
            extra.ValueKind.ShouldBe(JsonValueKind.False, path);

            var properties = node.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
            var required = node.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal).ToArray();
            required.ShouldBe(properties, path);
        }
    }

    [Fact]
    public void ReviewSchema_TopLevelKeysAreExactlyTheD21Six()
    {
        using var schema = Schema();

        schema.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name)
            .ShouldBe(["scores", "issues", "ear_rule_answers", "factual_care", "real_world_claims", "summary"], ignoreOrder: true);
    }

    [Fact]
    public void ReviewSchema_IssueTypeEnumIsExactlyTheSeventeenWireNames()
    {
        using var schema = Schema();
        var type = schema.RootElement.GetProperty("properties").GetProperty("issues").GetProperty("items").GetProperty("properties").GetProperty("type");

        var enumValues = type.GetProperty("enum").EnumerateArray().Select(item => item.GetString()!).ToArray();

        enumValues.Length.ShouldBe(17);
        enumValues.ShouldBe([.. Enum.GetValues<ReviewIssueType>().Select(ReviewIssueTypes.Wire)], ignoreOrder: true);
        enumValues.ShouldNotContain(ReviewIssueTypes.FactualCareMissing);
    }

    [Fact]
    public void ReviewSchema_SeverityIsBlockingOrAdvisory_AndScoresAreOneToFiveWithOnlyFactualCareNullable()
    {
        using var schema = Schema();
        var root = schema.RootElement.GetProperty("properties");

        var severity = root.GetProperty("issues").GetProperty("items").GetProperty("properties").GetProperty("severity");
        severity.GetProperty("enum").EnumerateArray().Select(item => item.GetString()).ShouldBe(["blocking", "advisory"], ignoreOrder: true);

        var scores = root.GetProperty("scores").GetProperty("properties");
        scores.EnumerateObject().Select(p => p.Name).ShouldBe(ReviewScores.Dimensions, ignoreOrder: true);
        foreach (var score in scores.EnumerateObject())
        {
            var values = score.Value.GetProperty("enum").EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Null ? "null" : item.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var nullable = values.Contains("null");

            nullable.ShouldBe(score.Name == "factual_care", score.Name);
            values.Where(value => value != "null").ShouldBe(["1", "2", "3", "4", "5"], score.Name);
        }

        // factual_care (the object) is nullable too: type ["object","null"].
        root.GetProperty("factual_care").GetProperty("type").EnumerateArray().Select(item => item.GetString()).ShouldBe(["object", "null"], ignoreOrder: true);
    }

    [Fact]
    public async Task ReviewStep_SendsTheSchemaAsAStrictJsonSchemaFormat()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(Valid()));

        await Writer(host).ReviewAsync(Request(Topic.Ghost), Ct);

        var format = host.Handler.Requests.ShouldHaveSingleItem().Json.GetProperty("text").GetProperty("format");
        format.GetProperty("type").GetString().ShouldBe("json_schema");
        format.GetProperty("strict").GetBoolean().ShouldBeTrue();
        format.GetProperty("name").GetString().ShouldBe("review_v1");
        format.GetProperty("schema").GetProperty("additionalProperties").ValueKind.ShouldBe(JsonValueKind.False);
        format.GetProperty("schema").GetProperty("properties").GetProperty("issues").GetProperty("items")
            .GetProperty("properties").GetProperty("type").GetProperty("enum").GetArrayLength().ShouldBe(17);
    }

    // Pipeline outcome of one model reply: adapter parse, then the validator. Accepted only when both pass.
    private static async Task<(TextCallResult<StoryReviewV1> Call, ValidatedReview? Verdict)> RunAsync(string modelJson, Topic topic = Topic.Ghost)
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(modelJson));
        var req = Request(topic);

        var call = await Writer(host).ReviewAsync(req, Ct);
        var verdict = call is TextCallOk<StoryReviewV1> ok
            ? ReviewResultValidator.Validate(ok.Draft, topic, req.Draft.Paragraphs.Count, [.. req.EarRuleQuestions.Select(q => q.QuestionId)])
            : null;

        return (call, verdict);
    }

    private static bool Accepted((TextCallResult<StoryReviewV1> Call, ValidatedReview? Verdict) outcome) => outcome.Verdict is { IsAccepted: true };

    [Fact]
    public async Task ReviewStep_ValidReply_IsAccepted_SoTheNegativeCasesBelowProveSomething()
    {
        Accepted(await RunAsync(Valid())).ShouldBeTrue();
    }

    [Theory]
    [InlineData("plot_hole")]
    [InlineData("factual_care_missing")]
    [InlineData("Weak_Hook")]
    [InlineData("")]
    [InlineData("tone_violation ")]
    public async Task ReviewStep_IssueTypeOutsideTheSeventeen_IsNeverAccepted(string type)
    {
        var outcome = await RunAsync(Valid().Replace("\"type\":\"tone_violation\"", $"\"type\":\"{type}\"", StringComparison.Ordinal));

        Accepted(outcome).ShouldBeFalse();
        if (outcome.Call is TextCallOk<StoryReviewV1>)
        {
            outcome.Verdict!.Rejections.Select(v => v.RuleId).ShouldBe([ReviewResultValidator.UnknownIssueType]);
        }
    }

    [Theory]
    [InlineData("\"severity\":\"advisory\"", "\"severity\":\"critical\"")]
    [InlineData("\"hook\":4", "\"hook\":6")]
    [InlineData("\"hook\":4", "\"hook\":0")]
    [InlineData("\"hook\":4", "\"hook\":\"4\"")]
    [InlineData("\"hook\":4", "\"hook\":4.5")]
    [InlineData("\"hook\":4", "\"hook\":null")]
    [InlineData("\"segment\":\"p:1\"", "\"segment\":\"paragraph 3\"")]
    [InlineData("\"segment\":\"p:1\"", "\"segment\":\"p:2000\"")]
    [InlineData("\"question_id\":\"Q1\"", "\"question_id\":\"Q99\"")]
    public async Task ReviewStep_MalformedReply_IsNeverAccepted(string from, string to)
    {
        Valid().ShouldContain(from);

        Accepted(await RunAsync(Valid().Replace(from, to, StringComparison.Ordinal))).ShouldBeFalse(to);
    }

    [Fact]
    public async Task ReviewStep_MissingRequiredKeys_AreNeverAccepted()
    {
        foreach (var key in new[] { "\"issues\":[", "\"ear_rule_answers\":[", "\"real_world_claims\":[", "\"summary\":", "\"scores\":{", "\"hook\":4,", "\"length_fit\":4" })
        {
            var removed = Valid().Replace(key, "\"zz_" + key.TrimStart('"'), StringComparison.Ordinal);

            removed.ShouldNotBe(Valid(), key);
            Accepted(await RunAsync(removed)).ShouldBeFalse(key);
        }
    }

    [Fact]
    public async Task ReviewStep_FactualCareNull_IsAcceptedButAbsent_IsNeverAccepted_ForEveryTopic()
    {
        // The wire contract is "required and nullable": an explicit null is the answer for fiction, a missing key is a slip.
        var absentObject = Valid().Replace("\"factual_care\":null,\n", string.Empty, StringComparison.Ordinal);
        var absentScore = Valid().Replace("\"factual_care\":null,\"length_fit\"", "\"length_fit\"", StringComparison.Ordinal);
        absentObject.ShouldNotBe(Valid());
        absentScore.ShouldNotBe(Valid());

        foreach (var topic in new[] { Topic.Ghost, Topic.Spiritual, Topic.TrueCrime })
        {
            Accepted(await RunAsync(Valid(), topic)).ShouldBeTrue(topic + " null");
            Accepted(await RunAsync(absentObject, topic)).ShouldBeFalse(topic + " absent factual_care object");
            Accepted(await RunAsync(absentScore, topic)).ShouldBeFalse(topic + " absent scores.factual_care");
        }
    }

    [Fact]
    public async Task ReviewStep_AdditionalProperties_AreIgnoredLocallyAndNeverReachTheAuditDocument()
    {
        // The provider enforces additionalProperties:false server-side; locally an extra key is dropped by the parse and
        // AuditReportBuilder only writes the D22 keys, so nothing the model invents can be stored.
        var json = Valid()
            .Replace("\"summary\":", "\"debug\":\"forged\",\"summary\":", StringComparison.Ordinal)
            .Replace("\"id\":\"r1\",", "\"id\":\"r1\",\"extra\":1,", StringComparison.Ordinal);

        var (call, verdict) = await RunAsync(json);

        call.ShouldBeOfType<TextCallOk<StoryReviewV1>>();
        var merged = AuditReportBuilder.MergeModelReview(string.Empty, verdict!, "gpt-6-luna", "rel=test");
        merged.ShouldNotContain("forged");
        merged.ShouldNotContain("\"extra\"");
        merged.ShouldNotContain("\"debug\"");
    }

    [Fact]
    public async Task ReviewStep_TrueCrimeWithFactualCareNull_KeepsNullAndAddsTheAdvisoryIssue()
    {
        var (call, verdict) = await RunAsync(Valid(), Topic.TrueCrime);

        call.ShouldBeOfType<TextCallOk<StoryReviewV1>>();
        verdict!.IsAccepted.ShouldBeTrue();
        verdict.Review.FactualCare.ShouldBeNull();
        verdict.Review.Issues.ShouldContain(issue => issue.Type == ReviewIssueTypes.FactualCareMissing && issue.Severity == "advisory");
    }

    [Fact]
    public async Task ReviewStep_FictionWithFactualCareObject_ClearsItWithAWarning()
    {
        const string factual = """{"claims_checked":1,"unsupported_claims":[],"notes":"n"}""";
        var json = Valid().Replace("\"factual_care\":null,\n", $"\"factual_care\":{factual},\n", StringComparison.Ordinal);
        json.ShouldContain("claims_checked");

        var (_, verdict) = await RunAsync(json, Topic.Spiritual);

        verdict!.IsAccepted.ShouldBeTrue();
        verdict.Warnings.Select(v => v.RuleId).ShouldBe([ReviewResultValidator.FactualCareForFiction]);
        verdict.Review.FactualCare.ShouldBeNull();
    }

    [Fact]
    public void ReviewStep_HostileStory_CannotMoveTheControlKeysOrTheSystemPrompt()
    {
        var catalog = new PromptCatalog(new EmbeddedPromptResourceSource());
        var assembler = new PromptAssembler(catalog, NullLogger<PromptAssembler>.Instance);
        var baseline = assembler.Review(Request(Topic.TrueCrime)).Value;
        var baselineData = JsonDocument.Parse(baseline.UserDataJson).RootElement;

        foreach (var fixture in InjectionFixture.LoadAll(InjectionFixture.ReviewStep))
        {
            // Forged control keys, a "new instruction" and an 'all fives' demand, in every segment of the draft.
            var prompt = assembler.Review(Request(Topic.TrueCrime, text: fixture.Value)).Value;
            var data = JsonDocument.Parse(prompt.UserDataJson).RootElement;

            prompt.SystemPrompt.ShouldBe(baseline.SystemPrompt, fixture.Id);
            data.EnumerateObject().Select(p => p.Name).ShouldBe(baselineData.EnumerateObject().Select(p => p.Name), fixture.Id);
            foreach (var control in new[] { "task", "language", "topic", "tier", "length", "facts", "ear_rules" })
            {
                data.GetProperty(control).GetRawText().ShouldBe(baselineData.GetProperty(control).GetRawText(), $"{fixture.Id}: {control}");
            }

            data.GetProperty("task").GetString().ShouldBe("review_story");
            data.GetProperty("topic").GetString().ShouldBe("true_crime");
        }
    }

    [Fact]
    public async Task ReviewStep_HostileStory_OnTheWire_ReachesOnlyTheUserMessageAndTheSchemaStaysStrict()
    {
        var fixtures = InjectionFixture.LoadAll(InjectionFixture.ReviewStep);
        fixtures.ShouldNotBeEmpty();
        string? cleanSystem = null;

        foreach (var fixture in new[] { (InjectionFixture?)null }.Concat(fixtures))
        {
            using var host = new OpenAiTestHost();
            host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(Valid()));

            await Writer(host).ReviewAsync(fixture is null ? Request(Topic.Ghost) : Request(Topic.Ghost, text: fixture.Value), Ct);

            var body = host.Handler.Requests.ShouldHaveSingleItem().Json;
            var input = body.GetProperty("input").EnumerateArray().ToList();
            input.Count.ShouldBe(2);
            var system = input[0].GetRawText();
            var user = input[1].GetRawText();

            system.ShouldContain("system");
            user.ShouldContain("user");
            if (fixture is null)
            {
                cleanSystem = system;
                continue;
            }

            system.ShouldBe(cleanSystem, fixture.Id);
            system.ShouldNotContain(fixture.Canary);
            user.ShouldContain(fixture.Canary);
            body.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean().ShouldBeTrue();
            body.GetProperty("text").GetProperty("format").GetProperty("schema").GetProperty("additionalProperties").ValueKind.ShouldBe(JsonValueKind.False);
        }
    }

    // ---- manifest binding of the review prompt and schema (task acceptance check 1) ----

    private static JsonElement ManifestFile(JsonDocument manifest, string key) => manifest.RootElement.GetProperty("files").GetProperty(key);

    [Theory]
    [InlineData(PromptKey)]
    [InlineData(SchemaKey)]
    public void ReviewManifest_ListsTheFileWithItsRealSha256(string key)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(PromptsTree.Root, "manifest.json")));
        var entry = ManifestFile(manifest, key);

        entry.GetProperty("version").GetString().ShouldBe("v1");
        entry.GetProperty("sha256").GetString().ShouldBe(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(PromptsTree.Root, key)))));
    }

    [Theory]
    [InlineData(PromptKey)]
    [InlineData(SchemaKey)]
    public void ReviewManifest_OneChangedByteFailsStartUpNamingTheFile(string key)
    {
        var tree = PromptsTree.Read();
        var original = tree[key].ToArray();
        // One byte replaced in place: same length, different digest.
        tree[key][original.Length / 2] = (byte)(original[original.Length / 2] == (byte)'x' ? 'y' : 'x');
        SHA256.HashData(tree[key]).ShouldNotBe(SHA256.HashData(original));

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain($"sha256 mismatch for {key}");
    }

    [Theory]
    [InlineData(PromptKey)]
    [InlineData(SchemaKey)]
    public void ReviewManifest_MissingFileFailsStartUpNamingTheFile(string key)
    {
        var tree = PromptsTree.Read();
        tree.Remove(key);

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain(key);
    }

    [Theory]
    [InlineData(PromptKey)]
    [InlineData(SchemaKey)]
    public void ReviewManifest_DroppingTheEntryWhileTheFileShipsFailsStartUp(string key)
    {
        var tree = PromptsTree.Read();
        PromptsTree.RemoveManifestEntry(tree, key);

        var failure = PromptsTree.Failure(tree);

        failure.Message.ShouldContain("not tracked by the manifest");
    }

    [Fact]
    public void ReviewManifest_EmbeddedCatalogServesTheReviewPromptAndSchema()
    {
        var catalog = new PromptCatalog(new EmbeddedPromptResourceSource());

        catalog.GetStepPrompt(NightTales.Domain.Stories.Prompts.PromptStep.Review).ShouldNotBeNullOrWhiteSpace();
        catalog.GetSchema("review.v1.json").Length.ShouldBeGreaterThan(100);
    }

    // ---- helpers ----

    private static OpenAiStoryWriter Writer(OpenAiTestHost host)
    {
        var catalog = new PromptCatalog(new EmbeddedPromptResourceSource());

        return new OpenAiStoryWriter(new PromptAssembler(catalog, NullLogger<PromptAssembler>.Instance), host.Client, catalog, new NoMeter(), new FixedEstimator());
    }

    private static StoryReviewRequest Request(Topic topic, string text = "The corridor was quiet.")
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

        return new StoryReviewRequest(
            new RunContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new ReservationId(Guid.NewGuid()), GenerationTrigger.User, 1, "1"),
            draft,
            Language.En,
            LengthTier.Standard,
            new LengthTarget(1300, 2080, 20, 32, 6000, 2500, 6000),
            topic,
            [new FactForWriter("f1", "A statement.", [])],
            null,
            EarRuleQuestions.For(Language.En),
            "story/ghost.en.v1.md");
    }

    // A fully valid reply for the two-paragraph draft above (p:0 and p:1 exist, Q1-Q5 are asked and all answered).
    private static string Valid() =>
        """
        {"scores":{"hook":4,"pacing":3,"atmosphere":4,"natural_language":4,"originality":3,"factual_care":null,"length_fit":4},
         "issues":[{"id":"r1","type":"tone_violation","severity":"advisory","segment":"p:1","evidence":"a flat line","impact":"breaks the mood","repair_direction":"shorten"}],
         "ear_rule_answers":[{"question_id":"Q1","passed":true,"segment":"p:0","evidence":"moves the fear"},
           {"question_id":"Q2","passed":true,"segment":"meta","evidence":""},{"question_id":"Q3","passed":true,"segment":"meta","evidence":""},
           {"question_id":"Q4","passed":true,"segment":"meta","evidence":""},{"question_id":"Q5","passed":true,"segment":"meta","evidence":""}],
         "factual_care":null,
         "real_world_claims":[{"segment":"p:0","evidence":"a named city"}],
         "summary":"solid draft"}
        """;

    private sealed class NoMeter : IUsageMeter
    {
        public Task RecordAsync(UsageItem item, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FixedEstimator : IStoryCostEstimator
    {
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) => throw new InvalidOperationException();

        public Task<ErrorOr<UsageBound>> WriterWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) => throw new InvalidOperationException();

        public Task<ErrorOr<UsageBound>> ReviewWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(0.25m, 1, 1));

        public Task<ErrorOr<UsageBound>> RevisionWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(0.25m, 1, 1));
    }
}
