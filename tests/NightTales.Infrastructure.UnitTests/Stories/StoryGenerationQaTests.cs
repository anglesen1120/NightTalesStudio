using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Application.Features.Stories.Jobs;
using NightTales.Application.Features.Stories.Review;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Common;
using NightTales.Domain.Compliance;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Quality;
using NightTales.Domain.Stories.Review;
using NightTales.Domain.Stories.Similarity;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Queries;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Stories;
using NightTales.TestKit.Storage;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Stories;

/// <summary>QA (02.2.2-T3): negative, boundary and exploratory cases on top of <c>StoryGenerationPipelineTests</c>.</summary>
[Trait("Category", "Integration")]
public sealed class StoryGenerationQaTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Marker = "QAMARKERNARWHAL";

    private static readonly DateTimeOffset Now = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("title")]
    [InlineData("paragraph")]
    [InlineData("hook")]
    public async Task QA_LoneSurrogateInDraft_FailsClosed_NothingStored_NoContentLeak(string field)
    {
        var world = await NewWorldAsync();
        var bad = Mutate(field, $"x\uD800y{Marker}");
        world.Writer.EnqueueOk(bad);
        world.Writer.EnqueueOk(bad);

        await world.RunAsync();

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.CountsTowardLimit.ShouldBeFalse();
        (await world.VersionCountAsync()).ShouldBe(0);
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.Draft);
        world.Ledger.Refunds.Count.ShouldBe(1);
        (run.ErrorDetail ?? string.Empty).ShouldNotContain(Marker);
        world.Logs.All().ShouldNotContain(Marker);
    }

    // QA-1: a lone surrogate is invalid output (one correction retry, then story_invalid_output), never an insert failure.
    [Theory]
    [InlineData("title")]
    [InlineData("paragraph")]
    [InlineData("hook")]
    public async Task QA_LoneSurrogateInDraft_IsInvalidOutput_WithCorrectionRetry(string field)
    {
        var world = await NewWorldAsync();
        var bad = Mutate(field, "x\uD800y");
        world.Writer.EnqueueOk(bad);
        world.Writer.EnqueueOk(bad);

        await world.RunAsync();

        (await world.ReadRunAsync()).ErrorCode.ShouldBe(ErrorCodes.StoryInvalidOutput);
        world.Writer.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task QA_LoneSurrogateInTag_IsInvalidOutput_NeverStored()
    {
        var world = await NewWorldAsync();
        world.Writer.EnqueueOk(Mutate("tag", "x\uD800y"));
        world.Writer.EnqueueOk(Mutate("tag", "x\uD800y"));

        await world.RunAsync();

        (await world.ReadRunAsync()).ErrorCode.ShouldBe(ErrorCodes.StoryInvalidOutput);
        world.Writer.Requests[1].ViolatedRules.ShouldBe([StoryDraftValidator.ControlCharactersInvalid]);
        (await world.VersionCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task QA_LoneSurrogateThenCleanDraft_RetriesAndSucceeds()
    {
        var world = await NewWorldAsync();
        world.Writer.EnqueueOk(Mutate("paragraph", "a\uDC00b"));
        world.Writer.EnqueueOk();

        await world.RunAsync();

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        world.Writer.Requests[1].ViolatedRules.ShouldBe([StoryDraftValidator.ControlCharactersInvalid]);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("paragraph")]
    [InlineData("tag")]
    public async Task QA_NulInDraft_IsInvalidOutput_NeverReachesTheDatabase(string field)
    {
        var world = await NewWorldAsync();
        var bad = Mutate(field, $"x\0y{Marker}");
        world.Writer.EnqueueOk(bad);
        world.Writer.EnqueueOk(bad);

        await world.RunAsync();

        (await world.ReadRunAsync()).ErrorCode.ShouldBe(ErrorCodes.StoryInvalidOutput);
        (await world.VersionCountAsync()).ShouldBe(0);
        world.Logs.All().ShouldNotContain(Marker);
    }

    [Fact]
    public async Task QA_NulThenCleanDraft_RetriesAndSucceeds()
    {
        var world = await NewWorldAsync();
        world.Writer.EnqueueOk(Mutate("title", "a\0b"));
        world.Writer.EnqueueOk();

        await world.RunAsync();

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        world.Writer.Requests[1].ViolatedRules.ShouldBe([StoryDraftValidator.ControlCharactersInvalid]);
    }

    [Theory]
    [InlineData("evidence")]
    [InlineData("impact")]
    [InlineData("repair")]
    public async Task QA_ReviewTextWithNulAndLoneSurrogate_IsCleanedAndPersisted(string field)
    {
        var world = await NewWorldAsync(reviewEnabled: true);
        world.Writer.EnqueueOk();
        var text = "a\0b\uD800c\uDC00d";
        var issue = new ReviewIssueV1(
            "i1",
            "pacing_issue",
            "advisory",
            "p:0",
            field == "evidence" ? text : "ev",
            field == "impact" ? text : "im",
            field == "repair" ? text : "rd");
        world.Writer.EnqueueReviewOk([issue]);

        await world.RunAsync();

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        var audit = (await world.OnlyVersionAsync()).AuditReport.ShouldNotBeNull();
        audit.Contains('\0').ShouldBeFalse();
        audit.Contains("\\u0000", StringComparison.Ordinal).ShouldBeFalse();
        audit.Contains("\\ud800", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    [Fact]
    public async Task QA_ReviewWithNulInModelName_IsCleaned_RunSucceeds()
    {
        var world = await NewWorldAsync(reviewEnabled: true);
        world.Writer.EnqueueOk();
        world.Writer.EnqueueReview(TextCallResult<StoryReviewV1>.Ok(CleanReview(), "gpt\0review\uD800", null, "r"));

        await world.RunAsync();

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Succeeded);
        JsonNode.Parse(run.Models!)!["review"]!.GetValue<string>().ShouldBe("gpt review\uFFFD");
        var audit = JsonNode.Parse((await world.OnlyVersionAsync()).AuditReport!)!;
        audit["model_review"]!["model"]!.GetValue<string>().ShouldBe("gpt review\uFFFD");
    }

    [Fact]
    public async Task QA_WriterModelName_IsCleanedAndCapped_BeforeStorage()
    {
        var world = await NewWorldAsync();
        world.Writer.Enqueue(TextCallResult<StoryContentV1>.Ok(Draft(), "gpt\0w" + new string('x', 500), null, "req-1"));

        await world.RunAsync();

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        var model = (await world.OnlyVersionAsync()).Model.ShouldNotBeNull();
        model.Length.ShouldBe(128);
        model.ShouldStartWith("gpt wx");
        model.Contains('\0').ShouldBeFalse();
    }

    [Fact]
    public async Task QA_ReviewEvidenceOver200Chars_IsRejected_ThenInvalidOutput()
    {
        var world = await NewWorldAsync(reviewEnabled: true);
        world.Writer.EnqueueOk();
        var big = new ReviewIssueV1("i1", "pacing_issue", "advisory", "p:0", new string('e', 5000), "im", "rd");
        world.Writer.EnqueueReview(TextCallResult<StoryReviewV1>.Ok(CleanReview([big]), "m", null, "r"));
        world.Writer.EnqueueReview(TextCallResult<StoryReviewV1>.Ok(CleanReview([big]), "m", null, "r"));

        await world.RunAsync();

        (await world.ReadRunAsync()).ErrorCode.ShouldBe(ErrorCodes.StoryInvalidOutput);
        (await world.VersionCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task QA_ReviewWithThousandsOfIssues_IsRejected_NotPersisted()
    {
        var world = await NewWorldAsync(reviewEnabled: true);
        world.Writer.EnqueueOk();
        var many = Enumerable.Range(0, 5000)
            .Select(i => new ReviewIssueV1($"i{i}", "pacing_issue", "advisory", "p:0", "e", "im", "rd"))
            .ToArray();
        world.Writer.EnqueueReview(TextCallResult<StoryReviewV1>.Ok(CleanReview(many), "m", null, "r"));
        world.Writer.EnqueueReview(TextCallResult<StoryReviewV1>.Ok(CleanReview(many), "m", null, "r"));

        await world.RunAsync();

        (await world.ReadRunAsync()).ErrorCode.ShouldBe(ErrorCodes.StoryInvalidOutput);
        (await world.VersionCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task QA_ReviewScoreOutOfRange_IsRejected()
    {
        var world = await NewWorldAsync(reviewEnabled: true);
        world.Writer.EnqueueOk();
        var bad = CleanReview() with { Scores = new ReviewScores(0, 9, 4, 4, 4, null, 4) };
        world.Writer.EnqueueReview(TextCallResult<StoryReviewV1>.Ok(bad, "m", null, "r"));
        world.Writer.EnqueueReview(TextCallResult<StoryReviewV1>.Ok(bad, "m", null, "r"));

        await world.RunAsync();

        (await world.ReadRunAsync()).ErrorCode.ShouldBe(ErrorCodes.StoryInvalidOutput);
    }

    [Fact]
    public async Task QA_ReviewWithoutEarRuleAnswers_IsRejected_FailsClosed()
    {
        var world = await NewWorldAsync(reviewEnabled: true);
        world.Writer.EnqueueOk();
        var bad = CleanReview() with { EarRuleAnswers = [] };
        world.Writer.EnqueueReview(TextCallResult<StoryReviewV1>.Ok(bad, "m", null, "r"));
        world.Writer.EnqueueReview(TextCallResult<StoryReviewV1>.Ok(bad, "m", null, "r"));

        await world.RunAsync();

        (await world.ReadRunAsync()).ErrorCode.ShouldBe(ErrorCodes.StoryInvalidOutput);
        (await world.VersionCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task QA_ReviewModeratedOutcome_FailsModerated_Counted_Refunded()
    {
        var world = await NewWorldAsync(reviewEnabled: true);
        world.Writer.EnqueueOk();
        world.Writer.EnqueueReview(TextCallResult<StoryReviewV1>.Moderated("m", null, null, ["violence"]));

        await world.RunAsync();

        var run = await world.ReadRunAsync();
        run.ErrorCode.ShouldBe(ErrorCodes.StoryModerated);
        run.CountsTowardLimit.ShouldBeTrue();
        world.Ledger.Refunds.ShouldBe([("story_moderated", world.Reservation)]);
    }

    [Fact]
    public async Task QA_BriefFlaggedWithoutCategory_FailsClosed()
    {
        var world = await NewWorldAsync();
        world.Moderator.OnCheck = (_, stage) => stage == ModerationStage.Brief
            ? new ModerationResult(true, [], new Dictionary<string, double>())
            : ModerationResult.Clean;

        await world.RunAsync();

        var run = await world.ReadRunAsync();
        run.ErrorCode.ShouldBe(ErrorCodes.StoryModerated);
        world.Writer.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task QA_BriefCategoriesWithoutFlag_FailsClosed()
    {
        var world = await NewWorldAsync();
        world.Moderator.OnCheck = (_, stage) => stage == ModerationStage.Brief
            ? new ModerationResult(false, ["violence"], new Dictionary<string, double>())
            : ModerationResult.Clean;

        await world.RunAsync();

        (await world.ReadRunAsync()).ErrorCode.ShouldBe(ErrorCodes.StoryModerated);
        world.Writer.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task QA_DraftFlaggedWithoutCategory_RecordsBlockingUnspecified_NotAClean()
    {
        var world = await NewWorldAsync();
        world.Writer.EnqueueOk();
        world.Moderator.OnCheck = (_, stage) => stage == ModerationStage.Draft
            ? new ModerationResult(true, [], new Dictionary<string, double>())
            : ModerationResult.Clean;

        await world.RunAsync();

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        var audit = JsonNode.Parse((await world.OnlyVersionAsync()).AuditReport!)!;
        audit["moderation"]!["flagged"]!.GetValue<bool>().ShouldBeTrue();
        audit["moderation"]!["blocking"]!.AsArray().Count.ShouldBeGreaterThan(0, "approval must stay blocked");
        audit["local"]!["issues"]!.AsArray().ShouldContain(
            issue => issue!["rule"]!.GetValue<string>() == StoryQualityChecker.ModerationBlocked
                && issue["severity"]!.GetValue<string>() == "blocking",
            "an uncategorised flag must reach the checker as a blocking issue");
    }

    [Fact]
    public async Task QA_ModeratorThrowsUnexpectedException_FailsClosed_Refunded()
    {
        var world = await NewWorldAsync();
        world.Moderator.OnCheck = (_, _) => throw new HttpRequestException($"boom {Marker}");

        await world.RunAsync();

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.ErrorCode.ShouldBe(ErrorCodes.StoryGenerationFailed);
        run.ErrorDetail.ShouldBe("unexpected_error");
        run.ErrorDetail!.ShouldNotContain(Marker);
        world.Logs.All().ShouldNotContain(Marker);
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.Draft);
        world.Ledger.Refunds.Count.ShouldBe(1);
    }

    [Fact]
    public async Task QA_WriterThrowsWithContentInMessage_NoContentInErrorDetailOrLogs()
    {
        var world = await NewWorldAsync();
        world.Writer.EnqueueThrow(new InvalidOperationException($"echo {Marker}"));

        await world.RunAsync();

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        (run.ErrorDetail ?? string.Empty).ShouldNotContain(Marker);
        world.Logs.All().ShouldNotContain(Marker);
    }

    [Fact]
    public async Task QA_DeadlineDuringBriefModeration_FailsGenerationFailed_Refunded_TerminalWriteLands()
    {
        var world = await NewWorldAsync(timeoutSeconds: 1);
        world.Moderator.Block = true;

        await world.RunAsync();

        await AssertDeadlineFailureAsync(world);
        world.Writer.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task QA_DeadlineDuringDraftModeration_FailsGenerationFailed_Refunded()
    {
        var world = await NewWorldAsync(timeoutSeconds: 1);
        world.Writer.EnqueueOk();
        world.Moderator.OnCheck = (_, stage) =>
        {
            if (stage == ModerationStage.Draft)
            {
                world.Moderator.Block = true;
            }

            return ModerationResult.Clean;
        };
        world.Moderator.BlockStage = ModerationStage.Draft;

        await world.RunAsync();

        await AssertDeadlineFailureAsync(world);
    }

    [Fact]
    public async Task QA_DeadlineDuringReview_FailsGenerationFailed_Refunded()
    {
        var world = await NewWorldAsync(timeoutSeconds: 1, reviewEnabled: true);
        world.Writer.EnqueueOk();
        world.Writer.BlockReview = true;

        await world.RunAsync();

        await AssertDeadlineFailureAsync(world);
        (await world.VersionCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task QA_JobTokenCancelled_RethrowsAndLeavesRunRunning_NoRefund()
    {
        var world = await NewWorldAsync();
        world.Writer.EnqueueBlocking();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Should.ThrowAsync<OperationCanceledException>(() => world.RunWithTokenAsync(cts.Token));

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Running);
        world.Ledger.Refunds.ShouldBeEmpty();
    }

    [Fact]
    public async Task QA_ProviderFundsExhausted_AtWrite_NoSettleNoRefund_AtReview_NoSettleNoRefund()
    {
        var write = await NewWorldAsync();
        write.Writer.Enqueue(TextCallResult<StoryContentV1>.ProviderFundsExhausted("m", null));
        await write.RunAsync();
        (await write.ReadRunAsync()).PauseReason.ShouldBe(GenerationPauseReason.ProviderFundsExhausted);
        write.Ledger.Settled.ShouldBeEmpty();
        write.Ledger.Refunds.ShouldBeEmpty();

        var review = await NewWorldAsync(reviewEnabled: true);
        review.Writer.EnqueueOk();
        review.Writer.EnqueueReview(TextCallResult<StoryReviewV1>.ProviderFundsExhausted("m", null));
        await review.RunAsync();
        (await review.ReadRunAsync()).PauseReason.ShouldBe(GenerationPauseReason.ProviderFundsExhausted);
        review.Ledger.Settled.ShouldBeEmpty();
        review.Ledger.Refunds.ShouldBeEmpty();
        (await review.VersionCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task QA_DoubleDelivery_SameRunId_SettlesOnce_OneVersion()
    {
        var world = await NewWorldAsync();
        world.Writer.EnqueueOk();

        await world.RunAsync();
        await world.RunAsync();

        world.Ledger.Settled.Count.ShouldBe(1);
        world.Ledger.Refunds.ShouldBeEmpty();
        (await world.VersionCountAsync()).ShouldBe(1);
        world.Writer.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task QA_DoubleDelivery_AfterFailure_RefundsOnce()
    {
        var world = await NewWorldAsync();
        world.Writer.EnqueueInvalid("schema_invalid");
        world.Writer.EnqueueInvalid("schema_invalid");

        await world.RunAsync();
        await world.RunAsync();

        world.Ledger.Refunds.Count.ShouldBe(1);
    }

    [Fact]
    public async Task QA_LateRedeliveryOfRunA_AfterRunBStarted_DoesNotTouchEpisodeOrRunB()
    {
        var world = await NewWorldAsync();
        world.Writer.EnqueueInvalid("schema_invalid");
        world.Writer.EnqueueInvalid("schema_invalid");
        await world.RunAsync(); // run A failed, episode back to draft

        var runB = await world.SeedSecondRunAsync(); // episode story_generating again, run B queued

        await world.RunAsync(); // redelivery of A

        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryGenerating);
        (await world.ReadRunAsync(runB)).Status.ShouldBe(GenerationRunStatus.Queued);
        world.Ledger.Refunds.Count.ShouldBe(1);
        var lateFail = await world.Progress.FailAsync(
            world.RunId,
            new StoryFailure(ErrorCodes.StoryGenerationFailed, "x", false, "story_generation_failed"),
            Ct);
        lateFail.IsError.ShouldBeTrue();
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryGenerating);
    }

    [Fact]
    public async Task QA_SweeperFailsRunMidFlight_LiveJobCompletion_IsRejected_NoSettle_NoSecondRefund()
    {
        var world = await NewWorldAsync();
        world.Writer.EnqueueAction(async () =>
        {
            // The sweeper (its own context and progress writer) takes the run while the writer call is in flight.
            await using var sweeperDb = Database.CreateAppDbContext(world.Current, new FakeClock(Now));
            var sweeper = new StoryGenerationProgress(sweeperDb, world.Ledger, new FakeClock(Now), new StoryGenerationMetrics(), world.Logs.For<StoryGenerationProgress>());
            var ended = await sweeper.FailAsync(
                world.RunId,
                new StoryFailure(ErrorCodes.StoryGenerationFailed, "stale_run_sweep", false, "stale_run"),
                Ct,
                recovery: true);
            ended.IsError.ShouldBeFalse();
        });

        await world.RunAsync();

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.ErrorDetail.ShouldBe("stale_run_sweep");
        (await world.VersionCountAsync()).ShouldBe(0);
        world.Ledger.Settled.ShouldBeEmpty();
        world.Ledger.Refunds.ShouldBe([("stale_run", world.Reservation)]);
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.Draft);
    }

    [Fact]
    public async Task QA_RefundThrows_RunStillTerminalAndEpisodeReturned()
    {
        var world = await NewWorldAsync();
        world.Ledger.ThrowOnRefund = true;
        world.Writer.EnqueueInvalid("schema_invalid");
        world.Writer.EnqueueInvalid("schema_invalid");

        await world.RunAsync();

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Failed);
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.Draft);
    }

    [Fact]
    public async Task QA_SettleThrows_RunStaysSucceeded()
    {
        var world = await NewWorldAsync();
        world.Ledger.ThrowOnSettle = true;
        world.Writer.EnqueueOk();

        await world.RunAsync();

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryReview);
    }

    [Fact]
    public async Task QA_InvalidOutputRuleIds_AreFilteredBeforeTheCorrection()
    {
        var world = await NewWorldAsync();
        world.Writer.EnqueueInvalid("good_rule", $"BAD TEXT {Marker}", "another_ok");
        world.Writer.EnqueueOk();

        await world.RunAsync();

        world.Writer.Requests[1].ViolatedRules.ShouldBe(["good_rule", "another_ok"]);
    }

    [Fact]
    public async Task QA_WriterMarkerText_NeverInLogsOnHappyPath()
    {
        var world = await NewWorldAsync(reviewEnabled: true);
        world.Writer.EnqueueOk(Mutate("title", $"Tieu {Marker}"));
        world.Writer.EnqueueReviewOk([new ReviewIssueV1("i1", "pacing_issue", "advisory", "p:0", Marker, "im", "rd")]);

        await world.RunAsync();

        world.Logs.All().ShouldNotContain(Marker);
    }

    private static async Task AssertDeadlineFailureAsync(World world)
    {
        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.ErrorCode.ShouldBe(ErrorCodes.StoryGenerationFailed);
        run.ErrorDetail.ShouldBe("deadline_exceeded");
        run.CountsTowardLimit.ShouldBeFalse();
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.Draft);
        world.Ledger.Refunds.ShouldBe([("story_generation_failed", world.Reservation)]);
        world.Ledger.Settled.ShouldBeEmpty();
    }

    private static StoryContentV1 Mutate(string field, string value)
    {
        var draft = Draft();

        return field switch
        {
            "title" => draft with { Title = value },
            "paragraph" => draft with { Paragraphs = [new StorySegmentV1(value, ["k2"], [])] },
            "tag" => draft with { Tags = [value] },
            "hook" => draft with { Hook = new StorySegmentV1(value, ["k1"], []) },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
    }

    private static StoryReviewV1 CleanReview(IReadOnlyList<ReviewIssueV1>? issues = null) => new(
        new ReviewScores(4, 4, 4, 4, 4, null, 4),
        issues ?? [],
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

    private async Task<World> NewWorldAsync(int timeoutSeconds = 360, bool reviewEnabled = false)
    {
        var setup = new TestCurrentUser();
        var user = await new UserBuilder(Database, setup).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, setup).CreateAsync(user.Id, topic: Topic.Ghost, ct: Ct);
        var episode = await new EpisodeBuilder(Database, setup)
            .CreateAsync(user.Id, series.Id, status: EpisodeStatus.StoryGenerating, ct: Ct);
        var current = new TestCurrentUser { Id = user.Id };
        var world = new World(Database, current, user.Id, series.Id, episode.Id, timeoutSeconds, reviewEnabled);
        await world.SeedRunAsync(first: true);

        return world;
    }

    private sealed class World
    {
        private readonly TestDatabase _database;
        private readonly AppDbContext _db;
        private readonly StoryGenerationPipeline _pipeline;
        private readonly Guid _userId;
        private readonly Guid _seriesId;

        public World(TestDatabase database, TestCurrentUser current, Guid userId, Guid seriesId, Guid episodeId, int timeoutSeconds, bool reviewEnabled)
        {
            _database = database;
            Current = current;
            _userId = userId;
            _seriesId = seriesId;
            EpisodeId = episodeId;
            var clock = new FakeClock(Now);
            _db = database.CreateAppDbContext(current, clock);
            Progress = new StoryGenerationProgress(_db, Ledger, clock, new StoryGenerationMetrics(), Logs.For<StoryGenerationProgress>());
            _pipeline = new StoryGenerationPipeline(
                _db,
                Progress,
                new StoryWriteRequestFactory(new FixedCatalog()),
                new FakeFactSheetGuard(),
                Moderator,
                new NullEvents(),
                Writer,
                new StubAssembler(),
                new FakeBudgetGuard(),
                new FixedEstimator(),
                new StoryQualityChecker(),
                new StoryJobOptions { TimeoutSeconds = timeoutSeconds, TimeoutSecondsLong = timeoutSeconds },
                new StoryReviewOptions { Enabled = reviewEnabled },
                new SimilarityCorpusProvider(_db, new FakeExtractedTextStore(), Options.Create(new SimilaritySettings())),
                new SimilaritySettings(),
                new StoryGenerationMetrics(),
                clock,
                Logs.For<StoryGenerationPipeline>());
        }

        public TestCurrentUser Current { get; }

        public Guid RunId { get; private set; }

        public Guid EpisodeId { get; }

        public ReservationId Reservation { get; private set; }

        public StoryGenerationProgress Progress { get; }

        public Ledger Ledger { get; } = new();

        public Writer Writer { get; } = new();

        public Moderator Moderator { get; } = new();

        public LogCapture Logs { get; } = new();

        public Task RunAsync() => _pipeline.RunAsync(RunId, Ct);

        public Task RunWithTokenAsync(CancellationToken token) => _pipeline.RunAsync(RunId, token);

        public async Task SeedRunAsync(bool first)
        {
            var reservation = Guid.CreateVersion7();
            await using var seedDb = _database.CreateAppDbContext(Current, new FakeClock(Now));
            var run = GenerationRun.Queue(
                    _userId, _seriesId, EpisodeId, GenerationKind.Story, GenerationTrigger.User, null, EpisodeStatus.Draft, null, reservation, 0.5m, Now)
                .Value;
            seedDb.GenerationRuns.Add(run);
            seedDb.UsageRecords.Add(UsageRecord.Record(
                _userId, UsageOperation.Story, "openai", 0.25m, Now, episodeId: EpisodeId, seriesId: _seriesId, generationRunId: run.Id, reservationId: reservation));
            await seedDb.SaveChangesAsync(Ct);
            if (first)
            {
                RunId = run.Id;
                Reservation = new ReservationId(reservation);
            }
            else
            {
                SecondRunId = run.Id;
            }
        }

        public Guid SecondRunId { get; private set; }

        public async Task<Guid> SeedSecondRunAsync()
        {
            await using (var db = _database.CreateAppDbContext(Current, new FakeClock(Now)))
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE episodes SET status = 'story_generating' WHERE id = {EpisodeId}", Ct);
            }

            await SeedRunAsync(first: false);

            return SecondRunId;
        }

        public Task<GenerationRun> ReadRunAsync() => ReadRunAsync(RunId);

        public async Task<GenerationRun> ReadRunAsync(Guid id)
        {
            await using var db = _database.CreateAppDbContext(new TestCurrentUser { Id = Current.Id });

            return await db.GenerationRuns.AsNoTracking().SingleAsync(run => run.Id == id, Ct);
        }

        public async Task<EpisodeStatus> EpisodeStatusAsync()
        {
            await using var db = _database.CreateAppDbContext(new TestCurrentUser { Id = Current.Id });

            return await db.Episodes.AsNoTracking().Where(e => e.Id == EpisodeId).Select(e => e.Status).SingleAsync(Ct);
        }

        public async Task<StoryVersion> OnlyVersionAsync()
        {
            await using var db = _database.CreateAppDbContext(new TestCurrentUser { Id = Current.Id });

            return await db.StoryVersions.AsNoTracking().SingleAsync(v => v.EpisodeId == EpisodeId, Ct);
        }

        public async Task<int> VersionCountAsync()
        {
            await using var db = _database.CreateAppDbContext(new TestCurrentUser { Id = Current.Id });

            return await db.StoryVersions.CountAsync(v => v.EpisodeId == EpisodeId, Ct);
        }
    }

    private sealed class LogCapture
    {
        private readonly List<string> _lines = [];

        public ILogger<T> For<T>() => new Capturing<T>(_lines);

        public string All()
        {
            lock (_lines)
            {
                return string.Join('\n', _lines);
            }
        }

        private sealed class Capturing<T>(List<string> lines) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (lines)
                {
                    lines.Add(formatter(state, exception) + (exception?.ToString() ?? string.Empty));
                }
            }
        }
    }

    private sealed class Moderator : IContentModerator
    {
        public Func<IReadOnlyList<string>, ModerationStage, ModerationResult>? OnCheck { get; set; }

        public bool Block { get; set; }

        public ModerationStage? BlockStage { get; set; }

        public async ValueTask<ModerationResult> CheckAsync(IReadOnlyList<string> texts, ModerationStage stage, CancellationToken ct)
        {
            var result = OnCheck?.Invoke(texts, stage) ?? ModerationResult.Clean;
            if (Block && (BlockStage is null || BlockStage == stage))
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return result;
        }
    }

    private sealed class Writer : IStoryWriter
    {
        private readonly Queue<Func<CancellationToken, ValueTask<TextCallResult<StoryContentV1>>>> _script = [];
        private readonly Queue<TextCallResult<StoryReviewV1>> _reviews = [];

        public List<StoryWriteRequest> Requests { get; } = [];

        public bool BlockReview { get; set; }

        public void Enqueue(TextCallResult<StoryContentV1> result) => _script.Enqueue(_ => ValueTask.FromResult(result));

        public void EnqueueOk(StoryContentV1? draft = null) =>
            Enqueue(TextCallResult<StoryContentV1>.Ok(draft ?? Draft(), "gpt-test", null, "req-1"));

        public void EnqueueInvalid(params string[] ruleIds) =>
            Enqueue(TextCallResult<StoryContentV1>.InvalidOutput("gpt-test", null, null, ruleIds));

        public void EnqueueAction(Func<Task> before, StoryContentV1? draft = null) =>
            _script.Enqueue(async _ =>
            {
                await before();

                return TextCallResult<StoryContentV1>.Ok(draft ?? Draft(), "gpt-test", null, "req-1");
            });

        public void EnqueueThrow(Exception exception) => _script.Enqueue(_ => throw exception);

        public void EnqueueBlocking() =>
            _script.Enqueue(async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);

                throw new InvalidOperationException("unreachable");
            });

        public void EnqueueReview(TextCallResult<StoryReviewV1> result) => _reviews.Enqueue(result);

        public void EnqueueReviewOk(IReadOnlyList<ReviewIssueV1>? issues = null) =>
            EnqueueReview(TextCallResult<StoryReviewV1>.Ok(CleanReview(issues), "gpt-review", null, "req-2"));

        public ValueTask<TextCallResult<StoryContentV1>> WriteAsync(StoryWriteRequest req, CancellationToken ct)
        {
            Requests.Add(req);

            return _script.Count == 0 ? throw new InvalidOperationException("The writer script is empty.") : _script.Dequeue()(ct);
        }

        public async ValueTask<TextCallResult<StoryReviewV1>> ReviewAsync(StoryReviewRequest req, CancellationToken ct)
        {
            if (BlockReview)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return _reviews.Count == 0 ? throw new InvalidOperationException("The review script is empty.") : _reviews.Dequeue();
        }

        public ValueTask<TextCallResult<StoryContentV1>> ReviseAsync(StoryRevisionRequest req, CancellationToken ct) =>
            throw new InvalidOperationException("The revision stage is disabled in these tests.");
    }

    private sealed class StubAssembler : IPromptAssembler
    {
        public AssembledPrompt Assemble(StoryWriteRequest req, AssemblerContext ctx) => new("system", "{}", "pv-1", "cache");

        public ErrorOr<AssembledPrompt> Review(StoryReviewRequest req) => new AssembledPrompt("system", "{}", "pv-review", "cache");

        public ErrorOr<AssembledPrompt> Revise(StoryRevisionRequest req) => new AssembledPrompt("system", "{}", "pv-revise", "cache");
    }

    private sealed class FixedCatalog : IPriceCatalog
    {
        public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct) =>
            Task.FromResult<ErrorOr<PriceVersion>>(Version());

        public Task<PriceVersion> GetAsync(int requested, CancellationToken ct) => Task.FromResult(Version());

        private static PriceVersion Version() =>
            PriceVersion.Create(1, Now, "{}", PriceVersionJson.WriteLimits(PriceLimits.D4Defaults), "fake", Now);
    }

    private sealed class FixedEstimator : IStoryCostEstimator
    {
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(0.5m, 1000, 500));

        public Task<ErrorOr<UsageBound>> ReviewWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            WorstCaseAsync(tier, language, ct);

        public Task<ErrorOr<UsageBound>> WriterWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            WorstCaseAsync(tier, language, ct);

        public Task<ErrorOr<UsageBound>> RevisionWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            WorstCaseAsync(tier, language, ct);
    }

    private sealed class NullEvents : IModerationEventWriter
    {
        public Task WriteAsync(
            Guid userId,
            Guid? episodeId,
            ModerationSource source,
            ModerationStage stage,
            IReadOnlyCollection<string> categories,
            string? ownText,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Ledger : ICreditLedger
    {
        public List<(string Reason, ReservationId Reservation)> Refunds { get; } = [];

        public List<ReservationId> Settled { get; } = [];

        public bool ThrowOnRefund { get; set; }

        public bool ThrowOnSettle { get; set; }

        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(Guid userId, CreditOperation op, Guid referenceId, UsageBound bound, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct)
        {
            if (ThrowOnSettle)
            {
                throw new InvalidOperationException("settle down");
            }

            Settled.Add(reservationId);

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }

        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct)
        {
            if (ThrowOnRefund)
            {
                throw new InvalidOperationException("refund down");
            }

            Refunds.Add((reason, reservationId));

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }
    }
}
