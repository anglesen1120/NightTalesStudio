using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.SeriesBible.Abstractions;
using NightTales.Application.Features.Stories;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Application.Features.Stories.Jobs;
using NightTales.Application.Features.Stories.Review;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Common;
using NightTales.Domain.Compliance;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Quality;
using NightTales.Domain.Stories.Review;
using NightTales.Domain.Stories.Similarity;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Pricing;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Queries;
using NightTales.Infrastructure.Prompts;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Moderation;
using NightTales.TestKit.Research;
using NightTales.TestKit.Stories;
using NightTales.TestKit.Storage;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Stories;

/// <summary>
/// <c>StoryGenerationPipeline</c> (02.2.2-T3) against a real <c>AppDbContext</c>, with hand-written fakes for the writer,
/// the moderator, the guards and the ledger. Covers the review-disabled paths and the <c>Story:Review:Enabled</c> review
/// stage (02 §10 T113: no <c>ReviseAsync</c> yet, so a blocking-and-objective issue keeps the generated version).
/// </summary>
[Trait("Category", "Integration")]
public sealed class StoryGenerationPipelineTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string WriterVersion = "rel=2026.10.1;sys=writer.v1;preset=ghost.vi.v1;schema=story.v1";
    private const string ReviewedVersion = "rel=2026.10.1;sys=writer.v1;preset=ghost.vi.v1;review=v1;schema=story.v1";
    private const string ReviewOwnVersion = "rel=2026.10.1;sys=writer.v1;preset=ghost.vi.v1;review=v1;schema=review.v1";
    private const string RevisionOwnVersion = "rel=2026.10.1;sys=writer.v1;preset=ghost.vi.v1;revise=v1;schema=story.v1";

    [Fact]
    public async Task Pipeline_HappyPath_StagesInD21Order_AndSavesOneGeneratedVersion()
    {
        var world = await NewWorldAsync();
        world.Writer.EnqueueOk();

        await world.RunAsync();

        world.Progress.StageCalls.ShouldBe(
            [
                GenerationStage.Writing,
                GenerationStage.Checking,
                GenerationStage.Checking,
                GenerationStage.Scoring,
                GenerationStage.Saving,
                GenerationStage.Done,
            ],
            "review is disabled, so reviewing is skipped");
        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Succeeded);
        run.CostUsd.ShouldBe(0.25m, "the sum of the reservation's usage records");
        run.PromptVersion.ShouldBe(WriterVersion);
        Compact(run.Models).ShouldContain("\"review_enabled\":false");
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryReview);

        var version = await world.OnlyVersionAsync();
        version.Source.ShouldBe(StoryVersionSource.Generated);
        version.GenerationRunId.ShouldBe(world.RunId);
        version.AuditReport.ShouldNotBeNull().ShouldNotContain("similarity_unavailable");
        Compact(version.AuditReport).ShouldContain("\"schema_version\":\"audit.v1\"");
        world.Moderator.Checks.Select(check => check.Stage).ShouldBe([ModerationStage.Brief, ModerationStage.Draft]);
        world.Ledger.Settled.ShouldBe([world.Reservation]);
    }

    [Fact]
    public async Task Pipeline_InvalidOutputOnce_RetriesWithCorrectionAndSucceeds()
    {
        var world = await NewWorldAsync();
        world.Writer.EnqueueInvalid("schema_invalid");
        world.Writer.EnqueueOk();

        await world.RunAsync();

        world.Writer.Requests.Count.ShouldBe(2);
        world.Writer.Requests[0].ViolatedRules.ShouldBeNull();
        world.Writer.Requests[0].Run.Attempt.ShouldBe(0);
        world.Writer.Requests[1].ViolatedRules.ShouldBe(["schema_invalid"], "rule ids only, never the invalid text");
        world.Writer.Requests[1].Run.Attempt.ShouldBe(1);
        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Succeeded);
        run.Attempt.ShouldBe(1);
    }

    [Fact]
    public async Task Pipeline_InvalidOutputTwice_FailsInvalidOutput_NotCounted_Refunded()
    {
        var world = await NewWorldAsync();
        world.Writer.EnqueueInvalid("schema_invalid");
        world.Writer.EnqueueInvalid("schema_invalid");

        await world.RunAsync();

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.ErrorCode.ShouldBe(ErrorCodes.StoryInvalidOutput);
        run.CountsTowardLimit.ShouldBeFalse();
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.Draft);
        world.Ledger.Refunds.ShouldBe([("story_invalid_output", world.Reservation)]);
    }

    [Fact]
    public async Task Pipeline_ModeratedDraft_FailsModerated_Counted_Refunded()
    {
        var world = await NewWorldAsync();
        world.Writer.Enqueue(TextCallResult<StoryContentV1>.Moderated("m", null, null, ["violence"]));

        await world.RunAsync();

        var run = await world.ReadRunAsync();
        run.ErrorCode.ShouldBe(ErrorCodes.StoryModerated);
        run.CountsTowardLimit.ShouldBeTrue();
        world.Events.Stages.ShouldBe([ModerationStage.Draft]);
        world.Ledger.Refunds.ShouldBe([("story_moderated", world.Reservation)]);
    }

    [Fact]
    public async Task Pipeline_BriefFlagged_FailsBeforeAnyPaidCall()
    {
        var world = await NewWorldAsync();
        world.Moderator.EnqueueFlagged(ModerationStage.Brief, "violence");

        await world.RunAsync();

        world.Writer.Requests.ShouldBeEmpty("no paid call before the brief passed");
        var run = await world.ReadRunAsync();
        run.ErrorCode.ShouldBe(ErrorCodes.StoryModerated);
        run.CountsTowardLimit.ShouldBeTrue();
        world.Events.Stages.ShouldBe([ModerationStage.Brief]);
    }

    [Fact]
    public async Task Pipeline_ModerationUnavailable_FailsClosed_NotCounted()
    {
        var world = await NewWorldAsync();
        world.Moderator.EnqueueUnavailable(ModerationStage.Brief);

        await world.RunAsync();

        var run = await world.ReadRunAsync();
        run.ErrorCode.ShouldBe(ErrorCodes.StoryGenerationFailed);
        run.ErrorDetail.ShouldBe("moderation_unavailable");
        run.CountsTowardLimit.ShouldBeFalse();
        world.Writer.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Pipeline_BlockingCategoryInDraft_DoesNotFailTheRun_FlagsApproval()
    {
        var world = await NewWorldAsync();
        var category = StoryQualityChecker.BlockingModerationCategories.First();
        world.Moderator.EnqueueFlagged(ModerationStage.Draft, category);
        world.Writer.EnqueueOk();

        await world.RunAsync();

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        var audit = (await world.OnlyVersionAsync()).AuditReport.ShouldNotBeNull();
        audit.ShouldContain(category);
        Compact(audit).ShouldContain("\"flagged\":true");
    }

    [Fact]
    public async Task Pipeline_ProviderFundsExhausted_StaysQueued_WithPauseReason()
    {
        var world = await NewWorldAsync();
        world.Writer.Enqueue(TextCallResult<StoryContentV1>.ProviderFundsExhausted("m", null));

        await world.RunAsync();

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Queued);
        run.PauseReason.ShouldBe(GenerationPauseReason.ProviderFundsExhausted);
        run.CountsTowardLimit.ShouldBeFalse();
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryGenerating);
        world.Ledger.Refunds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Pipeline_BudgetGuardDenies_PausesQueued_NoRefund_NoPaidCall()
    {
        var world = await NewWorldAsync();
        world.Budget.Deny = true;

        await world.RunAsync();

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Queued);
        run.PauseReason.ShouldBe(GenerationPauseReason.SpendCapReached);
        world.Writer.Requests.ShouldBeEmpty();
        world.Ledger.Refunds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Pipeline_ReviewRan_PromptVersionIsTheD5Composite_OnRunAndVersion()
    {
        var world = await NewWorldAsync(reviewEnabled: true);
        world.Writer.EnqueueOk();
        world.Writer.EnqueueReviewOk();

        await world.RunAsync();

        (await world.ReadRunAsync()).PromptVersion.ShouldBe(ReviewedVersion);
        var version = await world.OnlyVersionAsync();
        version.PromptVersion.ShouldBe(ReviewedVersion);
        JsonNode.Parse(version.AuditReport!)!["model_review"]!["prompt_version"]!.GetValue<string>().ShouldBe(ReviewOwnVersion);
    }

    [Fact]
    public async Task Pipeline_ReviewDisabled_PromptVersionIsTheWriterComposite_OnRunAndVersion()
    {
        var world = await NewWorldAsync();
        world.Writer.EnqueueOk();

        await world.RunAsync();

        (await world.ReadRunAsync()).PromptVersion.ShouldBe(WriterVersion);
        (await world.OnlyVersionAsync()).PromptVersion.ShouldBe(WriterVersion);
    }

    [Fact]
    public async Task Pipeline_EveryGate_AsksTheGuardToLeaveOutTheRunsOwnReservation()
    {
        var world = await NewWorldAsync(reviewEnabled: true);
        world.Writer.EnqueueOk();
        world.Writer.EnqueueReviewOk();

        await world.RunAsync();

        world.Budget.ExcludedRunIds.Count.ShouldBe(2, "one gate before the writer, one before the review");
        world.Budget.ExcludedRunIds.ShouldAllBe(excluded => excluded == world.RunId);
    }

    [Fact]
    public async Task Pipeline_NearTheCap_AdmittedRunsOwnReservationDoesNotSelfDeny_WriteAndReviewGatesPass()
    {
        // The platform totals before this test's run exists. Seeding the run adds its worst case (0.5) to the open bounds and a
        // 0.25 usage record to today's spend; each gate then asks for 0.5 (FixedEstimator). The caps leave room for exactly
        // spent + 0.25 + open(others) + 0.5, so only the run's own 0.5 reservation would push a gate over.
        await using var read = Database.CreateReadDbContext();
        var dayStart = new DateTimeOffset(Now.UtcDateTime.Date, TimeSpan.Zero);
        var monthStart = new DateTimeOffset(Now.Year, Now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var spentDay = await read.Set<UsageRecord>().IgnoreQueryFilters(["tenant"]).Where(r => r.CreatedAt >= dayStart).SumAsync(r => (decimal?)r.CostUsd, Ct) ?? 0m;
        var spentMonth = await read.Set<UsageRecord>().IgnoreQueryFilters(["tenant"]).Where(r => r.CreatedAt >= monthStart).SumAsync(r => (decimal?)r.CostUsd, Ct) ?? 0m;
        var otherOpen = await new GenerationRunOpenBoundsReader(read).SumOpenWorstCaseUsdAsync(Ct);
        var options = new BudgetOptions
        {
            Enabled = true,
            MaxPlatformCostUsdPerDay = spentDay + 0.25m + otherOpen + 0.5m,
            MaxPlatformCostUsdPerMonth = spentMonth + 0.25m + otherOpen + 0.5m,
        };
        await using var guardDb = Database.CreateAppDbContext(new TestCurrentUser { Id = Guid.CreateVersion7() });
        var guard = new UsageBudgetGuard(read, new GenerationRunOpenBoundsReader(read), guardDb, options, new FakeClock(Now));

        var world = await NewWorldAsync(reviewEnabled: true, budget: guard);
        world.Writer.EnqueueOk();
        world.Writer.EnqueueReviewOk();

        (await guard.CheckAsync(new CostBound(0.5m), Ct)).FirstError.Code.ShouldBe(
            ErrorCodes.SpendCapReached,
            "without the exclusion the run's own reservation counts and the same step would be denied");

        await world.RunAsync();

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Succeeded);
        run.PauseReason.ShouldBeNull();
        world.Writer.ReviewCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Pipeline_DeadlineExceeded_FailsGenerationFailed_Refunded()
    {
        var world = await NewWorldAsync(timeoutSeconds: 1);
        world.Writer.EnqueueBlocking();

        await world.RunAsync();

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.ErrorCode.ShouldBe(ErrorCodes.StoryGenerationFailed);
        run.ErrorDetail.ShouldBe("deadline_exceeded");
        run.CountsTowardLimit.ShouldBeFalse();
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.Draft);
        world.Ledger.Refunds.ShouldBe([("story_generation_failed", world.Reservation)]);
    }

    [Fact]
    public async Task Pipeline_ControlCharactersInDraft_AreInvalidOutput_NeverReachTheDatabase()
    {
        var world = await NewWorldAsync();
        world.Writer.EnqueueOk(Draft() with { Title = "Tieu\0de" });
        world.Writer.EnqueueOk(Draft() with { Title = "Tieu\0de" });

        await world.RunAsync();

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.ErrorCode.ShouldBe(ErrorCodes.StoryInvalidOutput);
        (await world.VersionCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Pipeline_FactSheetChangedBetweenRequestAndDispatch_FailsOutdated()
    {
        var world = await NewWorldAsync(pinFactSheet: true);

        await world.RunAsync();

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.ErrorDetail.ShouldBe("fact_sheet_outdated");
        run.CountsTowardLimit.ShouldBeFalse();
        world.Writer.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Pipeline_TrueCrime_StoresFactSourcesOfTheCitedSources_NotAForeignRow()
    {
        // 02.5.5-T1 (D2): the stored content carries the backend-built fact_sources of the draft's fact refs, resolved
        // through the approved sheet's aliases to THIS episode's and owner's story_sources. A row of another episode
        // that happens to carry a cited id must never back this story's fact_sources, and the writer receives facts
        // only, never raw source text.
        var cited = Guid.CreateVersion7();
        var noteId = Guid.CreateVersion7();
        var foreignId = Guid.CreateVersion7();
        var sheet = new ApprovedFactSheet(
            Guid.CreateVersion7(),
            1,
            [
                new FactForWriter("f1", "Nguon A da duoc kiem chung.", ["s1"]),
                new FactForWriter("f2", "Ghi chu cua nguoi dung.", ["s2"]),
                new FactForWriter("f3", "Nguon la.", ["s3"]),
            ],
            new Dictionary<string, Guid>(StringComparer.Ordinal)
            {
                ["s1"] = cited,
                ["s2"] = noteId,
                ["s3"] = foreignId,
            });
        var world = await NewWorldAsync(
            topic: Topic.TrueCrime,
            approvedSheet: sheet,
            sources: async (userId, episodeId) =>
            {
                var actor = new TestCurrentUser { Id = userId };
                var otherSeries = await new SeriesBuilder(Database, actor).CreateAsync(userId, topic: Topic.TrueCrime, ct: Ct);
                var otherEpisode = await new EpisodeBuilder(Database, actor).CreateAsync(userId, otherSeries.Id, ct: Ct);

                return
                [
                    UrlSource(userId, episodeId, cited, "Nguon A", "https://example.com/source-a", "Doan trich nguyen van cua bai bao A."),
                    NoteSource(userId, episodeId, noteId, "Ghi chu rieng cua nguoi dung."),
                    UrlSource(userId, otherEpisode.Id, foreignId, "Nguon la", "https://example.com/foreign-leak", "Noi dung cua tap khac."),
                ];
            });
        world.Writer.EnqueueOk(Draft() with { Hook = new StorySegmentV1("Mo dau f1.", ["k1"], ["f1", "f2", "f3"]) });

        await world.RunAsync();

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Succeeded);
        run.ErrorDetail.ShouldBeNull();

        // The writer got the facts, not the sources: the request's serialised form carries the fact statements and
        // their aliases and none of the seeded source prose, note text or the foreign episode's text.
        var facts = world.Writer.Requests.ShouldHaveSingleItem().Facts.ShouldNotBeNull();
        facts.Select(fact => fact.Id).ShouldBe(["f1", "f2", "f3"]);
        facts.SelectMany(fact => fact.SourceAliases).ShouldBe(["s1", "s2", "s3"]);

        var serialised = JsonSerializer.Serialize(world.Writer.Requests[0]);
        serialised.ShouldContain("Nguon A da duoc kiem chung.", Case.Sensitive, "non-vacuity: the fact statements are in the request");
        serialised.ShouldNotContain("Doan trich nguyen van cua bai bao A.", Case.Sensitive, "the fetched source's extracted text never reaches the request");
        serialised.ShouldNotContain("Ghi chu rieng cua nguoi dung.", Case.Sensitive, "the owner's note text never reaches the request");
        serialised.ShouldNotContain("Noi dung cua tap khac.", Case.Sensitive, "another episode's source text never reaches the request");

        var sources = StoryContentJson.ReadFactSources((await world.OnlyVersionAsync()).Content);
        sources.Count.ShouldBe(2, "the cited url source and the note, never the foreign episode's row");
        var url = sources[0]!.AsObject();
        url["source_id"]!.GetValue<string>().ShouldBe(cited.ToString());
        url["title"]!.GetValue<string>().ShouldBe("Nguon A");
        url["url"]!.GetValue<string>().ShouldBe("https://example.com/source-a");
        var note = sources[1]!.AsObject();
        note["source_id"]!.GetValue<string>().ShouldBe(noteId.ToString());
        note["title"]!.GetValue<string>().ShouldBe("ghi chú của bạn", "a user note is listed under the label, never its text");
        note["url"].ShouldBeNull();
        sources.ToJsonString().ShouldNotContain(foreignId.ToString());
        sources.ToJsonString().ShouldNotContain("Nguon la");
        sources.ToJsonString().ShouldNotContain("Ghi chu rieng cua nguoi dung.");
    }

    [Fact]
    public async Task GenerateStoryJob_TrueCrime_PassesFactsToWriter_NotRawSourceText()
    {
        // 02.5.5-T1 (02 §7, D6/F12): the writer sees the approved facts and their aliases only — never the sources'
        // text, urls, titles or ids. The captured request is then assembled by the REAL prompt assembler over the
        // embedded prompt tree, so the assertion covers the shipped prompt path and not just the request record.
        var cited = Guid.CreateVersion7();
        var noteId = Guid.CreateVersion7();
        var sheet = new ApprovedFactSheet(
            Guid.CreateVersion7(),
            1,
            [
                new FactForWriter("f1", "Bang chung thu nhat da duoc kiem chung.", ["s1"]),
                new FactForWriter("f2", "Bang chung thu hai da duoc kiem chung.", ["s2"]),
            ],
            new Dictionary<string, Guid>(StringComparer.Ordinal)
            {
                ["s1"] = cited,
                ["s2"] = noteId,
            });
        var world = await NewWorldAsync(
            topic: Topic.TrueCrime,
            approvedSheet: sheet,
            sources: (userId, episodeId) =>
            {
                IReadOnlyList<StorySource> rows =
                [
                    UrlSource(userId, episodeId, cited, "Bai bao goc", "https://example.com/source-a", "DOAN TRICH NGUYEN VAN CUA BAI BAO GOC."),
                    NoteSource(userId, episodeId, noteId, "GHI CHU RIENG CUA NGUOI DUNG."),
                ];

                return Task.FromResult(rows);
            });
        world.Writer.EnqueueOk(Draft() with { Hook = new StorySegmentV1("Mo dau f1 va f2.", ["k1"], ["f1", "f2"]) });

        await world.RunAsync();

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);

        var request = world.Writer.Requests.ShouldHaveSingleItem();
        var facts = request.Facts.ShouldNotBeNull();
        facts.Count.ShouldBe(2, "the sheet's facts, compared field-wise: record equality compares the lists by reference");
        facts[0].Id.ShouldBe("f1");
        facts[0].Statement.ShouldBe("Bang chung thu nhat da duoc kiem chung.");
        facts[0].SourceAliases.ShouldBe(["s1"]);
        facts[1].Id.ShouldBe("f2");
        facts[1].Statement.ShouldBe("Bang chung thu hai da duoc kiem chung.");
        facts[1].SourceAliases.ShouldBe(["s2"]);

        var prompt = new PromptAssembler(
                new PromptCatalog(new EmbeddedPromptResourceSource()),
                NullLogger<PromptAssembler>.Instance)
            .Assemble(
                request,
                new AssemblerContext(
                    request.Series.Topic,
                    request.Series.Language,
                    $"story/true_crime.{(request.Series.Language == Language.Vi ? "vi" : "en")}.v1.md",
                    6000));
        var data = prompt.UserDataJson;
        data.ShouldContain("Bang chung thu nhat da duoc kiem chung.", Case.Sensitive, "non-vacuity: the fact statements really are in the data object");
        data.ShouldContain("Bang chung thu hai da duoc kiem chung.");
        data.ShouldContain("\"s1\"");
        data.ShouldContain("\"s2\"");
        data.ShouldNotContain("DOAN TRICH NGUYEN VAN CUA BAI BAO GOC.", Case.Sensitive, "the fetched source's extracted text never reaches the prompt");
        data.ShouldNotContain("GHI CHU RIENG CUA NGUOI DUNG.", Case.Sensitive, "the owner's note text never reaches the prompt");
        data.ShouldNotContain("https://example.com/source-a", Case.Sensitive, "no source url reaches the prompt");
        data.ShouldNotContain("Bai bao goc", Case.Sensitive, "no source title reaches the prompt");
        data.ShouldNotContain(cited.ToString(), Case.Sensitive, "no source id reaches the prompt");
        data.ShouldNotContain(noteId.ToString(), Case.Sensitive, "no source id reaches the prompt");
    }

    /// <summary>A fetched URL source of the episode, with the title a real fetch stores.</summary>
    private static StorySource UrlSource(Guid userId, Guid episodeId, Guid id, string title, string url, string extractedText)
    {
        var parsed = SourceUrl.Parse(url, new FakePublicSuffixList());
        parsed.IsError.ShouldBeFalse();
        var source = StorySource.CreateUrl(id, userId, episodeId, parsed.Value, Now).Value;
        source.MarkFetched(url, title, "text/html", new string('a', 64), $"sources/{id:N}", extractedText.Length, Now);

        return source;
    }

    private static StorySource NoteSource(Guid userId, Guid episodeId, Guid id, string noteText) =>
        StorySource.CreateNote(id, userId, episodeId, noteText, Now).Value;

    [Fact]
    public async Task Job_AlreadySucceeded_ReturnsWithoutTouchingAnything()
    {
        var world = await NewWorldAsync(succeeded: true);

        await world.RunAsync();

        world.Writer.Requests.ShouldBeEmpty();
        world.Moderator.Calls.ShouldBe(0);
        world.Ledger.Refunds.ShouldBeEmpty();
        world.Ledger.Settled.ShouldBeEmpty();
        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
    }

    [Fact]
    public async Task Job_RunningOwnedByAnotherWorker_Returns()
    {
        var world = await NewWorldAsync(seed: run => run.Start(Now));

        await world.RunAsync();

        world.Writer.Requests.ShouldBeEmpty();
        world.Moderator.Calls.ShouldBe(0);
        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Running);
    }

    [Fact]
    public async Task StoryWriteRequestFactory_SetsPriceVersionFromQueuedAt()
    {
        var world = await NewWorldAsync(priceVersion: 7);
        await using var db = Database.CreateAppDbContext(world.Current, new FakeClock(Now));
        var run = await db.GenerationRuns.SingleAsync(candidate => candidate.Id == world.RunId, Ct);
        var episode = await db.Episodes.SingleAsync(candidate => candidate.Id == world.EpisodeId, Ct);

        var built = await world.Factory.BuildAsync(run, episode, Topic.Ghost, Language.Vi, null, 0, null, new BibleForWriter(null, [], 0), Ct);

        built.IsError.ShouldBeFalse();
        built.Value.Request.Run.PriceVersion.ShouldBe("7");
        world.Catalog.AskedAt.ShouldBe(run.QueuedAt);
    }

    [Fact]
    public async Task Pipeline_HappyPath_StagesInD21Order()
    {
        var world = await NewWorldAsync(reviewEnabled: true);
        world.Writer.EnqueueOk();
        world.Writer.EnqueueReviewOk();

        await world.RunAsync();

        world.Progress.StageCalls.ShouldBe(
            [
                GenerationStage.Writing,
                GenerationStage.Checking,
                GenerationStage.Checking,
                GenerationStage.Reviewing,
                GenerationStage.Scoring,
                GenerationStage.Saving,
                GenerationStage.Done,
            ],
            "the D21 order when the review step is enabled");
        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Succeeded);
        Compact(run.Models).ShouldContain("\"review_enabled\":true");
        Compact(run.Models).ShouldContain("\"review\":\"gpt-review\"");
        world.Writer.ReviewCalls.Count.ShouldBe(1);
        var audit = (await world.OnlyVersionAsync()).AuditReport.ShouldNotBeNull();
        audit.ShouldContain("\"model_review\"");
        Compact(audit).ShouldContain("\"stale\":false");
    }

    [Fact]
    public async Task Pipeline_BlockingObjectiveIssue_NoReviser_KeepsGeneratedVersion()
    {
        // 02 §10 T113(b): ReviseAsync does not exist yet, so a blocking objective issue stays in the audit,
        // the generated version is kept, and the run records no `revising` stage.
        var world = await NewWorldAsync(reviewEnabled: true);
        world.Writer.EnqueueOk();
        world.Writer.EnqueueReviewOk(
            [new ReviewIssueV1("i1", "hook_missing", "blocking", "hook", "Mo dau.", "no usable hook", "add a hook")]);

        await world.RunAsync();

        world.Progress.StageCalls.ShouldNotContain(GenerationStage.Revising);
        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        var issue = ModelReviewIssues((await world.OnlyVersionAsync()).AuditReport!).ShouldHaveSingleItem();
        issue!["severity"]!.GetValue<string>().ShouldBe("blocking");
        issue["kind"]!.GetValue<string>().ShouldBe("objective");
        (await world.VersionCountAsync()).ShouldBe(1, "the generated version is kept; no revised version exists yet");
    }

    [Fact]
    public async Task Pipeline_AdvisoryOnlyIssue_SkipsRevision()
    {
        var world = await NewWorldAsync(reviewEnabled: true);
        world.Writer.EnqueueOk();
        world.Writer.EnqueueReviewOk(
            [new ReviewIssueV1("i1", "pacing_issue", "advisory", "p:0", "Doan mot.", "the pace drags", "tighten the paragraph")]);

        await world.RunAsync();

        world.Progress.StageCalls.ShouldNotContain(GenerationStage.Revising);
        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        var issue = ModelReviewIssues((await world.OnlyVersionAsync()).AuditReport!).ShouldHaveSingleItem();
        issue!["kind"]!.GetValue<string>().ShouldBe("taste");
    }

    [Fact]
    public async Task Pipeline_ReviewInvalidOutputOnce_RetriesAndSucceeds()
    {
        var world = await NewWorldAsync(reviewEnabled: true);
        world.Writer.EnqueueOk();
        world.Writer.EnqueueReview(TextCallResult<StoryReviewV1>.InvalidOutput("gpt-review", null, null, ["schema_invalid"]));
        world.Writer.EnqueueReviewOk();

        await world.RunAsync();

        world.Writer.ReviewCalls.Count.ShouldBe(2);
        world.Writer.ReviewCalls[0].Run.Attempt.ShouldBe(0);
        world.Writer.ReviewCalls[1].Run.Attempt.ShouldBe(1);
        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
    }

    [Fact]
    public async Task Pipeline_ReviewRejectedTwice_FailsInvalidOutput_NotPersistedRaw()
    {
        // A review with an issue type outside the closed list and no ear-rule answers is rejected by
        // ReviewResultValidator; after the single retry it ends the run and is never merged into audit_report.
        var world = await NewWorldAsync(reviewEnabled: true);
        world.Writer.EnqueueOk();
        world.Writer.EnqueueReview(TextCallResult<StoryReviewV1>.Ok(RejectedReview(), "gpt-review", null, null));
        world.Writer.EnqueueReview(TextCallResult<StoryReviewV1>.Ok(RejectedReview(), "gpt-review", null, null));

        await world.RunAsync();

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.ErrorCode.ShouldBe(ErrorCodes.StoryInvalidOutput);
        run.CountsTowardLimit.ShouldBeFalse();
        (await world.VersionCountAsync()).ShouldBe(0, "a rejected review is never persisted");
        world.Writer.ReviewCalls.Count.ShouldBe(2);
        world.Ledger.Refunds.ShouldBe([("story_invalid_output", world.Reservation)]);
    }

    [Fact]
    public async Task Pipeline_ReviewFundsExhausted_StaysQueued_WithPauseReason()
    {
        var world = await NewWorldAsync(reviewEnabled: true);
        world.Writer.EnqueueOk();
        world.Writer.EnqueueReview(TextCallResult<StoryReviewV1>.ProviderFundsExhausted("gpt-review", null));

        await world.RunAsync();

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Queued);
        run.PauseReason.ShouldBe(GenerationPauseReason.ProviderFundsExhausted);
        run.CountsTowardLimit.ShouldBeFalse();
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryGenerating);
        world.Ledger.Refunds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Pipeline_ReviewDisabled_SkipsReviewAndRevision()
    {
        var world = await NewWorldAsync(reviewEnabled: false);
        world.Writer.EnqueueOk();

        await world.RunAsync();

        world.Progress.StageCalls.ShouldBe(
            [
                GenerationStage.Writing,
                GenerationStage.Checking,
                GenerationStage.Checking,
                GenerationStage.Scoring,
                GenerationStage.Saving,
                GenerationStage.Done,
            ],
            "reviewing and revising are skipped when Story:Review:Enabled is false");
        world.Writer.ReviewCalls.ShouldBeEmpty();
        Compact((await world.OnlyVersionAsync()).AuditReport).ShouldContain("\"stale\":true");
        Compact((await world.ReadRunAsync()).Models).ShouldContain("\"review_enabled\":false");
    }

    [Fact]
    public async Task Pipeline_BudgetGuardDeniesAtSecondDispatch_PausesQueued_NoRefund()
    {
        var world = await NewWorldAsync(reviewEnabled: true, budget: new StagedBudgetGuard(allow: 1));
        world.Writer.EnqueueOk();

        await world.RunAsync();

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Queued);
        run.PauseReason.ShouldBe(GenerationPauseReason.SpendCapReached);
        run.CountsTowardLimit.ShouldBeFalse();
        world.Ledger.Refunds.ShouldBeEmpty();
        world.Writer.ReviewCalls.ShouldBeEmpty("the review call is gated before dispatch");
        world.BudgetCalls.ShouldBe(2, "the writer gate and the review gate were both checked");
    }

    [Fact]
    public async Task Pipeline_ModerationUnavailableAtDraft_FailsClosed_Refunded()
    {
        var world = await NewWorldAsync();
        world.Writer.EnqueueOk();
        world.Moderator.EnqueueUnavailable(ModerationStage.Draft);

        await world.RunAsync();

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.ErrorCode.ShouldBe(ErrorCodes.StoryGenerationFailed);
        run.ErrorDetail.ShouldBe("moderation_unavailable");
        run.CountsTowardLimit.ShouldBeFalse();
        (await world.VersionCountAsync()).ShouldBe(0);
        world.Ledger.Refunds.ShouldBe([("story_generation_failed", world.Reservation)]);
    }

    [Fact]
    public async Task Pipeline_ReviewIssueWithNul_IsSanitized_NotPersistedRaw()
    {
        var world = await NewWorldAsync(reviewEnabled: true);
        world.Writer.EnqueueOk();
        world.Writer.EnqueueReviewOk(
            [new ReviewIssueV1("i1", "pacing_issue", "advisory", "p:0", "Doan\0mot.", "slow", "tighten")]);

        await world.RunAsync();

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded, "a NUL in review text is cleaned, never a run failure");
        var audit = (await world.OnlyVersionAsync()).AuditReport.ShouldNotBeNull();
        audit.Contains("\\u0000", StringComparison.Ordinal).ShouldBeFalse("PostgreSQL jsonb rejects the escape");
        audit.Contains('\0').ShouldBeFalse();
        var issue = ModelReviewIssues(audit).ShouldHaveSingleItem();
        issue!["evidence"]!.GetValue<string>().Contains('\0').ShouldBeFalse();
    }

    [Fact]
    public async Task Pipeline_ModerationChecks_RunInsideModerationScope()
    {
        var world = await NewWorldAsync(reviewEnabled: true);
        world.Writer.EnqueueOk();
        world.Writer.EnqueueReviewOk();

        await world.RunAsync();

        world.Moderator.Calls.ShouldBe(2, "the brief check and the draft check");
        world.Scopes.Observed.Count.ShouldBe(world.Moderator.Calls);
        world.Scopes.Observed.ShouldAllBe(run => run != null && run.GenerationRunId == world.RunId);
    }

    private static StoryReviewV1 CleanReview(IReadOnlyList<ReviewIssueV1>? issues = null) => new(
        new ReviewScores(4, 4, 4, 4, 4, null, 4),
        issues ?? [],
        [.. EarRuleQuestions.For(Language.Vi).Select(question => new EarRuleAnswer(question.QuestionId, true, "meta", string.Empty))],
        FactualCare: null,
        [],
        "A calm summary.");

    private static StoryReviewV1 RejectedReview() =>
        CleanReview([new ReviewIssueV1("i1", "not_a_type", "advisory", "meta", "x", "y", "z")]) with { EarRuleAnswers = [] };

    private static JsonArray ModelReviewIssues(string audit) =>
        JsonNode.Parse(audit)!["model_review"]!["issues"]!.AsArray();

    private static string Compact(string? json) => JsonNode.Parse(json ?? throw new InvalidOperationException("json is null"))!.ToJsonString();

    private static StoryContentV1 Draft() => new(
        "Tieu de",
        new StorySegmentV1("Mo dau.", ["k1"], []),
        [new StorySegmentV1("Doan mot.", ["k2"], [])],
        new StorySegmentV1("Ket.", ["k3"], []),
        "Mo ta.",
        ["tag"],
        [],
        new StoryStructureV1("a", "b", "c", "d", "e"));

    private async Task<World> NewWorldAsync(
        int timeoutSeconds = 360,
        bool pinFactSheet = false,
        Action<GenerationRun>? seed = null,
        int priceVersion = 1,
        bool reviewEnabled = false,
        bool succeeded = false,
        IBudgetGuard? budget = null,
        Topic topic = Topic.Ghost,
        ApprovedFactSheet? approvedSheet = null,
        Func<Guid, Guid, Task<IReadOnlyList<StorySource>>>? sources = null)
    {
        var setup = new TestCurrentUser();
        var user = await new UserBuilder(Database, setup).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, setup).CreateAsync(user.Id, topic: topic, ct: Ct);
        var episode = await new EpisodeBuilder(Database, setup)
            .CreateAsync(user.Id, series.Id, status: EpisodeStatus.StoryGenerating, ct: Ct);
        var reservation = Guid.CreateVersion7();
        var current = new TestCurrentUser { Id = user.Id };

        // The run's input_fact_sheet_id is a same-owner FK to fact_sheets (02 CR-29), so a pinned run names a
        // real draft row of this episode.
        var sheet = pinFactSheet
            ? await new FactSheetBuilder(Database, current, new FakeClock(Now)).CreateAsync(user.Id, episode.Id, Ct)
            : null;

        // 02.5.5-T1: a true-crime run pins the approved sheet the guard serves, so its row must exist; the guard's
        // facts and alias map are kept as the test scripted them, only the id is re-pinned onto the real row.
        if (approvedSheet?.FactSheetId is not null)
        {
            sheet = await new FactSheetBuilder(Database, current, new FakeClock(Now)).CreateAsync(user.Id, episode.Id, Ct);
            approvedSheet = approvedSheet with { FactSheetId = sheet.Id };
        }

        Guid runId;
        await using (var seedDb = Database.CreateAppDbContext(current, new FakeClock(Now)))
        {
            if (sources is not null)
            {
                seedDb.StorySources.AddRange(await sources(user.Id, episode.Id));
            }

            var run = GenerationRun.Queue(
                    user.Id,
                    series.Id,
                    episode.Id,
                    GenerationKind.Story,
                    GenerationTrigger.User,
                    null,
                    EpisodeStatus.Draft,
                    sheet?.Id,
                    reservation,
                    0.5m,
                    Now)
                .Value;
            if (succeeded)
            {
                var version = StoryVersion.Create(
                    user.Id,
                    episode.Id,
                    1,
                    StoryVersionSource.Generated,
                    Draft(),
                    StoryContentJson.Write(Draft(), Array.Empty<FactSourceRef>()),
                    "gpt-test",
                    "pv-1",
                    null,
                    null,
                    null,
                    "{\"schema_version\":\"audit.v1\"}",
                    null,
                    Now);
                seedDb.StoryVersions.Add(version);
                await seedDb.SaveChangesAsync(Ct);
                run.Start(Now);
                run.Succeed(0m, version.Id, null, Now);
            }

            seed?.Invoke(run);
            seedDb.GenerationRuns.Add(run);
            seedDb.UsageRecords.Add(UsageRecord.Record(
                user.Id,
                UsageOperation.Story,
                "openai",
                0.25m,
                Now,
                episodeId: episode.Id,
                seriesId: series.Id,
                generationRunId: run.Id,
                reservationId: reservation));
            await seedDb.SaveChangesAsync(Ct);
            runId = run.Id;
        }

        return new World(Database, current, runId, episode.Id, new ReservationId(reservation), timeoutSeconds, priceVersion, reviewEnabled, budget, approvedSheet);
    }

    private sealed class World
    {
        private readonly TestDatabase _database;
        private readonly AppDbContext _db;
        private readonly StoryGenerationPipeline _pipeline;
        private readonly IBudgetGuard? _budget;
        private ScopeObservingModerator? _scopes;

        public World(
            TestDatabase database,
            TestCurrentUser current,
            Guid runId,
            Guid episodeId,
            ReservationId reservation,
            int timeoutSeconds,
            int priceVersion,
            bool reviewEnabled,
            IBudgetGuard? budget,
            ApprovedFactSheet? approvedSheet)
        {
            _database = database;
            Current = current;
            RunId = runId;
            EpisodeId = episodeId;
            Reservation = reservation;
            Catalog = new FixedCatalog(priceVersion);
            Factory = new StoryWriteRequestFactory(Catalog);
            Ledger = new RecordingLedger();

            var clock = new FakeClock(Now);
            _db = database.CreateAppDbContext(current, clock);
            Progress = new StoryGenerationProgress(_db, Ledger, clock, new StoryGenerationMetrics(), NullLogger<StoryGenerationProgress>.Instance);
            _budget = budget;
            _pipeline = new StoryGenerationPipeline(
                _db,
                Progress,
                Factory,
                new NoBibleSnapshotProvider(),
                new FakeFactSheetGuard { ApprovedSheet = approvedSheet },
                Scopes,
                Events,
                Writer,
                new StubAssembler(),
                budget ?? Budget,
                new FixedEstimator(),
                new StoryQualityChecker(),
                new EmptyNameCorpusProvider(),
                new StoryJobOptions { TimeoutSeconds = timeoutSeconds, TimeoutSecondsLong = timeoutSeconds },
                new StoryReviewOptions { Enabled = reviewEnabled },
                new StoryRevisionOptions { Enabled = false },
                new SimilarityCorpusProvider(_db, new FakeExtractedTextStore(), Options.Create(new SimilaritySettings())),
                new SimilaritySettings(),
                new StoryGenerationMetrics(),
                clock,
                NullLogger<StoryGenerationPipeline>.Instance);
        }

        public TestCurrentUser Current { get; }

        public Guid RunId { get; }

        public Guid EpisodeId { get; }

        public ReservationId Reservation { get; }

        public FixedCatalog Catalog { get; }

        public StoryWriteRequestFactory Factory { get; }

        public StoryGenerationProgress Progress { get; }

        public RecordingLedger Ledger { get; }

        public ScriptedWriter Writer { get; } = new();

        public ScopeObservingModerator Scopes => _scopes ??= new ScopeObservingModerator(Moderator);

        public int BudgetCalls => _budget is StagedBudgetGuard staged ? staged.Calls : Budget.Checked.Count;

        public FakeContentModerator Moderator { get; } = new();

        public RecordingEvents Events { get; } = new();

        public FakeBudgetGuard Budget { get; } = new();

        public Task RunAsync() => _pipeline.RunAsync(RunId, Ct);

        public async Task<GenerationRun> ReadRunAsync()
        {
            await using var db = _database.CreateAppDbContext(new TestCurrentUser { Id = Current.Id });

            return await db.GenerationRuns.AsNoTracking().SingleAsync(run => run.Id == RunId, Ct);
        }

        public async Task<EpisodeStatus> EpisodeStatusAsync()
        {
            await using var db = _database.CreateAppDbContext(new TestCurrentUser { Id = Current.Id });

            return await db.Episodes.AsNoTracking().Where(episode => episode.Id == EpisodeId).Select(episode => episode.Status).SingleAsync(Ct);
        }

        public async Task<StoryVersion> OnlyVersionAsync()
        {
            await using var db = _database.CreateAppDbContext(new TestCurrentUser { Id = Current.Id });

            return await db.StoryVersions.AsNoTracking().SingleAsync(version => version.EpisodeId == EpisodeId, Ct);
        }

        public async Task<int> VersionCountAsync()
        {
            await using var db = _database.CreateAppDbContext(new TestCurrentUser { Id = Current.Id });

            return await db.StoryVersions.CountAsync(version => version.EpisodeId == EpisodeId, Ct);
        }
    }

    private sealed class ScriptedWriter : IStoryWriter
    {
        private readonly Queue<Func<CancellationToken, ValueTask<TextCallResult<StoryContentV1>>>> _script = [];
        private readonly Queue<TextCallResult<StoryReviewV1>> _reviews = [];

        public List<StoryWriteRequest> Requests { get; } = [];

        public void Enqueue(TextCallResult<StoryContentV1> result) =>
            _script.Enqueue(_ => ValueTask.FromResult(result));

        public void EnqueueOk(StoryContentV1? draft = null) =>
            Enqueue(TextCallResult<StoryContentV1>.Ok(draft ?? Draft(), "gpt-test", null, "req-1"));

        public void EnqueueInvalid(params string[] ruleIds) =>
            Enqueue(TextCallResult<StoryContentV1>.InvalidOutput("gpt-test", null, null, ruleIds));

        public void EnqueueBlocking() =>
            _script.Enqueue(async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);

                throw new InvalidOperationException("unreachable");
            });

        public ValueTask<TextCallResult<StoryContentV1>> WriteAsync(StoryWriteRequest req, CancellationToken ct)
        {
            Requests.Add(req);

            return _script.Count == 0
                ? throw new InvalidOperationException("The writer script is empty.")
                : _script.Dequeue()(ct);
        }

        public List<StoryReviewRequest> ReviewCalls { get; } = [];

        public void EnqueueReview(TextCallResult<StoryReviewV1> result) =>
            _reviews.Enqueue(result);

        public void EnqueueReviewOk(IReadOnlyList<ReviewIssueV1>? issues = null) =>
            EnqueueReview(TextCallResult<StoryReviewV1>.Ok(CleanReview(issues), "gpt-review", null, "req-2"));

        public ValueTask<TextCallResult<StoryReviewV1>> ReviewAsync(StoryReviewRequest req, CancellationToken ct)
        {
            ReviewCalls.Add(req);

            return _reviews.Count == 0
                ? throw new InvalidOperationException("The review script is empty.")
                : ValueTask.FromResult(_reviews.Dequeue());
        }

        public ValueTask<TextCallResult<StoryContentV1>> ReviseAsync(StoryRevisionRequest req, CancellationToken ct) =>
            throw new InvalidOperationException("The revision stage is disabled in these tests.");
    }

    private sealed class ScopeObservingModerator(IContentModerator inner) : IContentModerator
    {
        public List<RunContext?> Observed { get; } = [];

        public ValueTask<ModerationResult> CheckAsync(IReadOnlyList<string> texts, ModerationStage stage, CancellationToken ct)
        {
            Observed.Add(ModerationScope.Current);

            return inner.CheckAsync(texts, stage, ct);
        }
    }

    private sealed class StagedBudgetGuard(int allow) : IBudgetGuard
    {
        public int Calls { get; private set; }

        public ValueTask<ErrorOr<Success>> CheckAsync(CostBound worstCaseUsd, CancellationToken ct, Guid? excludingRunId = null) =>
            ValueTask.FromResult<ErrorOr<Success>>(
                ++Calls > allow ? Error.Failure(ErrorCodes.SpendCapReached, "cap") : Result.Success);
    }

    private sealed class StubAssembler : IPromptAssembler
    {
        public AssembledPrompt Assemble(StoryWriteRequest req, AssemblerContext ctx) => new("system", "{}", WriterVersion, "cache");

        public ErrorOr<AssembledPrompt> Review(StoryReviewRequest req) => new AssembledPrompt("system", "{}", ReviewOwnVersion, "cache");

        public ErrorOr<AssembledPrompt> Revise(StoryRevisionRequest req) => new AssembledPrompt("system", "{}", RevisionOwnVersion, "cache");
    }

    private sealed class FixedCatalog(int version) : IPriceCatalog
    {
        public DateTimeOffset? AskedAt { get; private set; }

        public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct)
        {
            AskedAt = queuedAt;

            return Task.FromResult<ErrorOr<PriceVersion>>(Version());
        }

        public Task<PriceVersion> GetAsync(int requested, CancellationToken ct) => Task.FromResult(Version());

        private PriceVersion Version() =>
            PriceVersion.Create(version, Now, "{}", PriceVersionJson.WriteLimits(PriceLimits.D4Defaults), "fake", Now);
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

    private sealed class RecordingEvents : IModerationEventWriter
    {
        public List<ModerationStage> Stages { get; } = [];

        public Task WriteAsync(
            Guid userId,
            Guid? episodeId,
            ModerationSource source,
            ModerationStage stage,
            IReadOnlyCollection<string> categories,
            string? ownText,
            CancellationToken ct)
        {
            Stages.Add(stage);

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLedger : ICreditLedger
    {
        public List<(string Reason, ReservationId Reservation)> Refunds { get; } = [];

        public List<ReservationId> Settled { get; } = [];

        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(Guid userId, CreditOperation op, Guid referenceId, UsageBound bound, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct)
        {
            Settled.Add(reservationId);

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }

        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct)
        {
            Refunds.Add((reason, reservationId));

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }
    }
}
