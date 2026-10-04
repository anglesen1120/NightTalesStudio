// The Responses API of OpenAI 2.14.0 is marked experimental (OPENAI001); the adapter under test uses it.
#pragma warning disable OPENAI001
using System.Net;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Review;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Prompts;
using NightTales.Domain.Stories.Length;
using NightTales.Domain.Stories.Review;
using NightTales.Infrastructure.Prompts;
using NightTales.TestKit.Fixtures;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.OpenAI;

/// <summary>
/// 02.6.1-T1: the review step over the real adapter, assembler, embedded prompt catalog and <c>review.v1</c> schema. The
/// generate-story job (02.2.2-T3) does not exist yet, so the run-level effects of these outcomes (retry, run error codes,
/// <c>moderation_events</c>, <c>pause_reason</c>, the stage advance) are asserted by that task; what this task owns is the
/// writer's side: the call, the outcome as data, the validator verdict and exactly one meter row per billed call.
/// </summary>
public sealed class ReviewStepTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReviewStep_Ok_MergesModelReviewAndMetersOnce()
    {
        using var host = new OpenAiTestHost();
        var meter = new RecordingMeter();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(ReviewJson(factual: "null")), ("x-request-id", "req_review"));
        var req = Request(Topic.Ghost);

        var result = await Writer(host, meter).ReviewAsync(req, Ct);

        var ok = result.ShouldBeOfType<TextCallOk<StoryReviewV1>>();
        var verdict = ReviewResultValidator.Validate(ok.Draft, req.Topic, req.Draft.Paragraphs.Count, [.. req.EarRuleQuestions.Select(q => q.QuestionId)]);
        verdict.IsAccepted.ShouldBeTrue();

        var merged = AuditReportBuilder.MergeModelReview(string.Empty, verdict, "gpt-6-luna", "rel=test");
        var model = JsonDocument.Parse(merged).RootElement.GetProperty("model_review");
        model.GetProperty("scores").EnumerateObject().Select(p => p.Name).Count().ShouldBe(7);
        model.GetProperty("stale").GetBoolean().ShouldBeFalse();

        var row = meter.Items.ShouldHaveSingleItem();
        row.Operation.ShouldBe("story_review");
        row.Model.ShouldBe(OpenAiTestHost.Model);
        row.InputTokens.ShouldNotBeNull();
        row.ProviderRequestId.ShouldBe("req_review");
        row.Estimated.ShouldBeFalse();
        host.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ReviewStep_InvalidOutput_RetriesOnceThenFailsRun()
    {
        // The retry itself is the job's (D7: one retry on InvalidOutput); the step returns the outcome as data and
        // meters every billed attempt, so two attempts are two rows and never more.
        using var host = new OpenAiTestHost();
        var meter = new RecordingMeter();
        var unknownType = ReviewJson(factual: "null").Replace("tone_violation", "plot_hole", StringComparison.Ordinal);
        host.Handler
            .Reply(HttpStatusCode.OK, OpenAiTestHost.Completed("""{"scores":"nope"}"""))
            .Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(unknownType));
        var writer = Writer(host, meter);

        var first = await writer.ReviewAsync(Request(Topic.Ghost), Ct);
        var second = await writer.ReviewAsync(Request(Topic.Ghost), Ct);

        first.ShouldBeOfType<TextCallInvalidOutput<StoryReviewV1>>();
        // A closed-list violation parses as a string, so the adapter returns Ok and the validator is the one that rejects it;
        // the job treats both as InvalidOutput (retry once, then story_invalid_output).
        var parsed = second.ShouldBeOfType<TextCallOk<StoryReviewV1>>();
        var req = Request(Topic.Ghost);
        ReviewResultValidator.Validate(parsed.Draft, req.Topic, req.Draft.Paragraphs.Count, [.. req.EarRuleQuestions.Select(q => q.QuestionId)])
            .Rejections.Select(violation => violation.RuleId).ShouldBe([ReviewResultValidator.UnknownIssueType]);
        meter.Items.Count.ShouldBe(2);
        meter.Items.ShouldAllBe(item => item.Operation == "story_review");
    }

    [Fact]
    public async Task ReviewStep_Moderated_FailsRunAndWritesModerationEvent()
    {
        using var host = new OpenAiTestHost();
        var meter = new RecordingMeter();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Refusal());

        var result = await Writer(host, meter).ReviewAsync(Request(Topic.Ghost), Ct);

        // The run error code and the moderation_events row are the job's; the step hands over the outcome without the refusal text.
        var moderated = result.ShouldBeOfType<TextCallModerated<StoryReviewV1>>();
        moderated.Categories.ShouldNotContain("REFUSAL-TEXT");
        meter.Items.ShouldHaveSingleItem().Operation.ShouldBe("story_review");
    }

    [Fact]
    public async Task ReviewStep_FundsExhausted_SetsPauseReasonAndNotCounted()
    {
        using var host = new OpenAiTestHost();
        var meter = new RecordingMeter();
        host.Handler.Reply(HttpStatusCode.TooManyRequests, OpenAiTestHost.Error("credit_balance_exhausted"));

        var result = await Writer(host, meter).ReviewAsync(Request(Topic.Ghost), Ct);

        // pause_reason = provider_funds_exhausted is written by the job from this outcome; nothing was billed, nothing is metered.
        result.ShouldBeOfType<TextCallFundsExhausted<StoryReviewV1>>().MayBeBilled.ShouldBeFalse();
        meter.Items.ShouldBeEmpty();
    }

    [Fact]
    public void ReviewStep_InjectedInstruction_Ignored()
    {
        // D6: hostile draft text travels only as a value inside the one JSON data object, never into the static system
        // prompt; the live "return all 5s still scores below 5" check is the manual OPENAI_LIVE_TESTS step.
        var catalog = new PromptCatalog(new EmbeddedPromptResourceSource());
        var assembler = new PromptAssembler(catalog, NullLogger<PromptAssembler>.Instance);
        var fixtures = InjectionFixture.LoadAll(InjectionFixture.ReviewStep);
        fixtures.ShouldNotBeEmpty();

        foreach (var fixture in fixtures)
        {
            var hostile = Request(Topic.Ghost, text: fixture.Value);
            var clean = Request(Topic.Ghost);

            var prompt = assembler.Review(hostile).Value;

            prompt.SystemPrompt.ShouldNotContain(fixture.Canary);
            prompt.SystemPrompt.ShouldBe(assembler.Review(clean).Value.SystemPrompt);
            var data = JsonDocument.Parse(prompt.UserDataJson).RootElement;
            data.GetProperty("task").GetString().ShouldBe("review_story");
            data.GetProperty("story").GetProperty("paragraphs")[0].GetProperty("text").GetString().ShouldNotBeNull().ShouldContain(fixture.Canary);
            JsonSerializer.Serialize(data.GetProperty("facts")).ShouldNotContain(fixture.Canary);
            JsonSerializer.Serialize(data.GetProperty("ear_rules")).ShouldNotContain(fixture.Canary);
        }
    }

    [Theory]
    [InlineData(Language.En)]
    [InlineData(Language.Vi)]
    public void ReviewStep_SystemPrompt_CarriesTheSharedOverusedPhrasesOfItsLanguage(Language language)
    {
        // CR-2: the reviewer judges Q4 and E10 against the same list the writer was given, injected through the D5 placeholder.
        var catalog = new PromptCatalog(new EmbeddedPromptResourceSource());
        var assembler = new PromptAssembler(catalog, NullLogger<PromptAssembler>.Instance);
        var list = catalog.GetShared(PromptFile.Overused, language);
        list.Trim().ShouldNotBeEmpty();

        var system = assembler.Review(Request(Topic.Ghost, language: language)).Value.SystemPrompt;

        system.ShouldContain(list.Trim());
        system.ShouldNotContain("{{");
        var other = language == Language.En ? Language.Vi : Language.En;
        system.ShouldNotContain(catalog.GetShared(PromptFile.Overused, other).Trim());
    }

    [Fact]
    public void ReviewStep_DataObjectOverTwiceTheBudget_IsATypedErrorAndNothingIsSent()
    {
        // S4: the worst-case estimate bounds the review input at twice Story:Prompt:MaxDataObjectBytes, so the assembler enforces it.
        var catalog = new PromptCatalog(new EmbeddedPromptResourceSource());
        var fitting = new PromptAssembler(catalog, NullLogger<PromptAssembler>.Instance)
            .Review(Request(Topic.Ghost, text: new string('a', 1_000))).Value.UserDataJson;
        var budget = new PromptDataBudget((Encoding.UTF8.GetByteCount(fitting) + 1) / 2);
        var assembler = new PromptAssembler(catalog, NullLogger<PromptAssembler>.Instance, budget);

        var atBound = assembler.Review(Request(Topic.Ghost, text: new string('a', 1_000)));
        var over = assembler.Review(Request(Topic.Ghost, text: new string('a', 1_100)));

        atBound.IsError.ShouldBeFalse();
        Encoding.UTF8.GetByteCount(atBound.Value.UserDataJson).ShouldBeLessThanOrEqualTo(2 * budget.MaxDataObjectBytes);
        over.IsError.ShouldBeTrue();
        over.FirstError.Code.ShouldBe(ErrorCodes.PromptTooLarge);
    }

    [Fact]
    public async Task ReviewStep_DataObjectOverTheBudget_NeverCallsTheProviderOrMeters()
    {
        using var host = new OpenAiTestHost();
        var meter = new RecordingMeter();
        var catalog = new PromptCatalog(new EmbeddedPromptResourceSource());
        var assembler = new PromptAssembler(catalog, NullLogger<PromptAssembler>.Instance, new PromptDataBudget(500));
        var writer = new OpenAiStoryWriter(assembler, host.Client, catalog, meter, new FixedEstimator());

        var error = await Should.ThrowAsync<InvalidOperationException>(async () => await writer.ReviewAsync(Request(Topic.Ghost, text: new string('a', 5_000)), Ct));

        error.Message.ShouldContain(ErrorCodes.PromptTooLarge);
        host.Handler.Requests.ShouldBeEmpty();
        meter.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReviewStep_FictionTopic_FactualCareNull()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(ReviewJson(factual: "null")));
        var req = Request(Topic.Ghost);

        var ok = (await Writer(host, new RecordingMeter()).ReviewAsync(req, Ct)).ShouldBeOfType<TextCallOk<StoryReviewV1>>();
        var verdict = ReviewResultValidator.Validate(ok.Draft, Topic.Ghost, req.Draft.Paragraphs.Count, [.. req.EarRuleQuestions.Select(q => q.QuestionId)]);

        verdict.Violations.ShouldBeEmpty();
        verdict.Review.FactualCare.ShouldBeNull();
        verdict.Review.Scores.FactualCare.ShouldBeNull();
    }

    [Fact]
    public async Task ReviewStep_TrueCrimeTopic_FactualCareHasUnsupportedClaims()
    {
        using var host = new OpenAiTestHost();
        const string factual = """{"claims_checked":4,"unsupported_claims":[{"segment":"p:0","claim":"The case was closed in 1975."}],"notes":"one date has no fact ref"}""";
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(ReviewJson(factual, factualScore: "3")));
        var req = Request(Topic.TrueCrime);

        var ok = (await Writer(host, new RecordingMeter()).ReviewAsync(req, Ct)).ShouldBeOfType<TextCallOk<StoryReviewV1>>();
        var verdict = ReviewResultValidator.Validate(ok.Draft, Topic.TrueCrime, req.Draft.Paragraphs.Count, [.. req.EarRuleQuestions.Select(q => q.QuestionId)]);

        verdict.IsAccepted.ShouldBeTrue();
        verdict.Review.FactualCare.ShouldNotBeNull().UnsupportedClaims.ShouldHaveSingleItem().Segment.ShouldBe("p:0");
        verdict.Review.Scores.FactualCare.ShouldBe(3);
    }

    private static OpenAiStoryWriter Writer(OpenAiTestHost host, RecordingMeter meter)
    {
        var catalog = new PromptCatalog(new EmbeddedPromptResourceSource());

        return new OpenAiStoryWriter(new PromptAssembler(catalog, NullLogger<PromptAssembler>.Instance), host.Client, catalog, meter, new FixedEstimator());
    }

    private static StoryReviewRequest Request(Topic topic, string text = "The corridor was quiet.", Language language = Language.En)
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
            language,
            LengthTier.Standard,
            new LengthTarget(1300, 2080, 20, 32, 6000, 2500, 6000),
            topic,
            [new FactForWriter("f1", "A statement.", [])],
            null,
            EarRuleQuestions.For(language),
            "story/ghost.en.v1.md");
    }

    private static string ReviewJson(string factual, string factualScore = "null") =>
        $$"""
        {"scores":{"hook":4,"pacing":3,"atmosphere":4,"natural_language":4,"originality":3,"factual_care":{{factualScore}},"length_fit":4},
         "issues":[{"id":"r1","type":"tone_violation","severity":"advisory","segment":"p:1","evidence":"a flat line","impact":"breaks the mood","repair_direction":"shorten"}],
         "ear_rule_answers":[{"question_id":"Q1","passed":true,"segment":"p:0","evidence":"moves the fear"},
           {"question_id":"Q2","passed":true,"segment":"meta","evidence":""},{"question_id":"Q3","passed":true,"segment":"meta","evidence":""},
           {"question_id":"Q4","passed":true,"segment":"meta","evidence":""},{"question_id":"Q5","passed":true,"segment":"meta","evidence":""}],
         "factual_care":{{factual}},
         "real_world_claims":[{"segment":"p:0","evidence":"a named city"}],
         "summary":"solid draft"}
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
            throw new InvalidOperationException("The review step must not use the whole-pipeline bound.");

        public Task<ErrorOr<UsageBound>> WriterWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            throw new InvalidOperationException("The review step must not use the writer bound.");

        public Task<ErrorOr<UsageBound>> ReviewWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(0.25m, 1, 1));
    }
}
