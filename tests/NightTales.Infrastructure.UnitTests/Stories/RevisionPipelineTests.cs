using System.Text.Json.Nodes;
using ErrorOr;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories;
using NightTales.Application.Features.Stories.Jobs;
using NightTales.Application.Features.Stories.Review;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Review;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Research;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Stories;

/// <summary>
/// The single revision pass (02.6.2-T1, 02 README D21 step 5 + D22) against a real <c>AppDbContext</c>:
/// the decision gate, the untrusted-output defences (persistence rules, a new blocking validator finding,
/// review #2 unusable), the one-call bound, cancellation and the provider-funds path. The provider edge is
/// the scripted <see cref="ScriptedRevisionWriter"/>; nothing else is replaced.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RevisionPipelineTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Model = "gpt-test";

    [Fact]
    public async Task RevisionDisabled_NeverCallsReviseAsync_AndWritesNoRevisionBlock()
    {
        var world = await RevisionWorld.CreateAsync(Database, Ct, revisionEnabled: false);
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        world.Writer.RevisionRequests.ShouldBeEmpty("Story:Revision:Enabled is false");
        var version = (await world.VersionsAsync()).ShouldHaveSingleItem();
        version.Source.ShouldBe(StoryVersionSource.Generated);
        version.AuditReport.ShouldNotBeNull().ShouldNotContain("\"revision\"");
        world.Progress.StageCalls.ShouldBe(NoRevisionStages);
    }

    [Fact]
    public async Task MaxRevisionsZero_NeverCallsReviseAsync()
    {
        var world = await RevisionWorld.CreateAsync(Database, Ct, maxRevisions: 0);
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        world.Writer.RevisionRequests.ShouldBeEmpty("D21: MaxRevisions is a hard bound");
        (await world.VersionCountAsync()).ShouldBe(1);
        world.Progress.StageCalls.ShouldBe(NoRevisionStages);
    }

    [Fact]
    public async Task SpendCapAtRevisionGate_KeepsGeneratedRowWithReason()
    {
        // Two gates pass (the writer's and the review's), the third — the one that pays for the revision — is refused.
        var world = await RevisionWorld.CreateAsync(Database, Ct, budget: new StagedRevisionBudgetGuard(allow: 2));
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded, "a refused revision degrades, it never pauses the run");
        world.Writer.RevisionRequests.ShouldBeEmpty("no paid call past the cap");
        var versions = await world.VersionsAsync();
        var version = versions.ShouldHaveSingleItem();
        var block = RevisionBlock(version);
        block["applied"]!.GetValue<bool>().ShouldBeFalse();
        block["reason"]!.GetValue<string>().ShouldBe(AuditReportBuilder.RevisionReasonSpendCapReached);
        world.Progress.StageCalls.ShouldBe(AttemptedNotAppliedStages);
    }

    [Fact]
    public async Task RevisionBoundUnavailable_DegradesWithTheUnavailableReason()
    {
        // D3: the estimator cannot produce the revision bound. The run must not fail and the completed generated row
        // must not be discarded: the revision is recorded as not applied and the episode still reaches story_review.
        var world = await RevisionWorld.CreateAsync(Database, Ct, estimator: new DegradingRevisionEstimator(failRevisionBound: true));
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded, "an unavailable bound degrades, it never fails the run");
        world.Writer.RevisionRequests.ShouldBeEmpty("no paid call without a bound");
        var version = (await world.VersionsAsync()).ShouldHaveSingleItem();
        version.Source.ShouldBe(StoryVersionSource.Generated);
        var block = RevisionBlock(version);
        block["applied"]!.GetValue<bool>().ShouldBeFalse();
        block["reason"]!.GetValue<string>().ShouldBe(AuditReportBuilder.RevisionReasonUnavailable);
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryReview, "the episode still reaches story_review");
        world.Progress.StageCalls.ShouldBe(AttemptedNotAppliedStages);
    }

    [Fact]
    public async Task ReviewTwoBoundUnavailable_KeepsGeneratedRowWithTheUnavailableReason()
    {
        // D3: the estimator cannot produce review #2's bound. The revision itself runs, but an unreviewed revision
        // never becomes current, so the generated row is kept with the same additive reason the revision gate records
        // when the estimator cannot produce a bound (the single reason the task names for both optional gates).
        var world = await RevisionWorld.CreateAsync(Database, Ct, estimator: new DegradingRevisionEstimator(failReviewAfterBound: true));
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(RevisionOk(RevisedDraft()));

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded, "an unavailable review #2 bound degrades, it never fails the run");
        world.Writer.RevisionRequests.Count.ShouldBe(1, "the revision runs; only review #2's bound is unavailable");
        world.Writer.ReviewRequests.Count.ShouldBe(1, "review #2 is never called without a bound");
        var version = (await world.VersionsAsync()).ShouldHaveSingleItem();
        version.Source.ShouldBe(StoryVersionSource.Generated);
        var block = RevisionBlock(version);
        block["applied"]!.GetValue<bool>().ShouldBeFalse();
        block["reason"]!.GetValue<string>().ShouldBe(AuditReportBuilder.RevisionReasonUnavailable);
    }

    [Fact]
    public async Task RevisedTextWithControlCharacter_KeepsOriginalDraft()
    {
        var world = await RevisionWorld.CreateAsync(Database, Ct);
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(RevisionOk(Draft() with
        {
            Hook = new StorySegmentV1("Mo dau moi \u0007.", ["k1"], []),
        }));

        await world.RunAsync(Ct);

        await AssertKeptOriginalAsync(world);
    }

    [Fact]
    public async Task RevisedTextWithLoneSurrogate_KeepsOriginalDraft()
    {
        var world = await RevisionWorld.CreateAsync(Database, Ct);
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(RevisionOk(Draft() with
        {
            Hook = new StorySegmentV1("Mo dau moi \ud83d.", ["k1"], []),
        }));

        await world.RunAsync(Ct);

        await AssertKeptOriginalAsync(world);
    }

    [Fact]
    public async Task RevisedDraftWithAnUnapprovedFactRef_KeepsOriginalDraft()
    {
        // The citation the revision invents is not in the approved sheet: a new blocking validator finding, so the
        // revision cannot become current (D21: the revised output is untrusted like a fresh draft).
        var sheetId = Guid.CreateVersion7();
        var sheet = new ApprovedFactSheet(
            sheetId,
            1,
            [new FactForWriter("f1", "Nguon da duoc kiem chung.", ["s1"])],
            new Dictionary<string, Guid>(StringComparer.Ordinal));
        var world = await RevisionWorld.CreateAsync(Database, Ct, topic: Topic.TrueCrime, approvedSheet: sheet);
        world.Writer.EnqueueWrite(WriteOk(Cited("f1")));
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(RevisionOk(Cited("f9")));

        await world.RunAsync(Ct);

        await AssertKeptOriginalAsync(world);
    }

    [Fact]
    public async Task RevisionKeepingEveryFactRefReordered_IsApplied()
    {
        // The dropped-ref guard is set-based on the story as a whole: here the two approved ids swap order in the hook,
        // so nothing is lost and the revision is applied.
        var sheet = new ApprovedFactSheet(
            Guid.CreateVersion7(),
            1,
            [
                new FactForWriter("f1", "Nguon mot da duoc kiem chung.", ["s1"]),
                new FactForWriter("f2", "Nguon hai da duoc kiem chung.", ["s2"]),
            ],
            new Dictionary<string, Guid>(StringComparer.Ordinal));
        var world = await RevisionWorld.CreateAsync(Database, Ct, topic: Topic.TrueCrime, approvedSheet: sheet);
        world.Writer.EnqueueWrite(WriteOk(CitedAll("f1", "f2")));
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(RevisionOk(CitedAll("f2", "f1")));
        world.Writer.EnqueueReview(ReviewOk());

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        var versions = await world.VersionsAsync();
        versions.Count.ShouldBe(2, "a re-ordered citation keeps every approved ref, so the revision is applied");
        versions[1].Source.ShouldBe(StoryVersionSource.Revised);
        RevisionBlock(versions[1])["applied"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task BothSavedRows_CarryBuilderBuiltFactSources_FromTheEpisodesSources()
    {
        // 02.5.5-T1: both rows the run stores (the generated one and the applied revision) carry the fact_sources the
        // backend built from the episode's story_sources, resolved through the sheet's aliases — never from the draft.
        var sourceId = Guid.CreateVersion7();
        var sheet = new ApprovedFactSheet(
            Guid.CreateVersion7(),
            1,
            [new FactForWriter("f1", "Nguon da duoc kiem chung.", ["s1"])],
            new Dictionary<string, Guid>(StringComparer.Ordinal) { ["s1"] = sourceId });
        var world = await RevisionWorld.CreateAsync(
            Database,
            Ct,
            topic: Topic.TrueCrime,
            approvedSheet: sheet,
            sources: (userId, episodeId) => [UrlSource(userId, episodeId, sourceId, "Nguon A", "https://example.com/source-a")]);

        var original = Draft() with { Hook = new StorySegmentV1("Mo dau f1.", ["k1"], ["f1"]) };
        var revised = Draft() with { Hook = new StorySegmentV1("Mo dau ro rang hon f1.", ["k1"], ["f1"]) };
        world.Writer.EnqueueWrite(WriteOk(original));
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(RevisionOk(revised));
        world.Writer.EnqueueReview(ReviewOk());

        await world.RunAsync(Ct);

        var versions = await world.VersionsAsync();
        versions.Count.ShouldBe(2);
        versions[1].Source.ShouldBe(StoryVersionSource.Revised);
        foreach (var version in versions)
        {
            var sources = StoryContentJson.ReadFactSources(version.Content);
            var entry = sources.ShouldHaveSingleItem()!.AsObject();
            entry["source_id"]!.GetValue<string>().ShouldBe(sourceId.ToString());
            entry["title"]!.GetValue<string>().ShouldBe("Nguon A");
            entry["url"]!.GetValue<string>().ShouldBe("https://example.com/source-a");
        }
    }

    /// <summary>An episode source with a title, as a fetch leaves it: the row the builder reads the display metadata from.</summary>
    private static StorySource UrlSource(Guid userId, Guid episodeId, Guid id, string title, string url)
    {
        var parsed = SourceUrl.Parse(url, new FakePublicSuffixList());
        parsed.IsError.ShouldBeFalse();
        var source = StorySource.CreateUrl(id, userId, episodeId, parsed.Value, RevisionWorld.Now).Value;
        source.MarkFetched(url, title, "text/html", new string('a', 64), $"sources/{id:N}", 64, RevisionWorld.Now);

        return source;
    }

    [Fact]
    public async Task FictionRevision_IsExemptFromTheFactRefGuard()
    {
        // A fiction story has no approved fact sheet, so the guard short-circuits: a revision that changes a fiction
        // draft's refs is never refused as invalid output on that account (only the D3 validator's own rules apply).
        var world = await RevisionWorld.CreateAsync(Database, Ct);
        world.Writer.EnqueueWrite(WriteOk(Cited("f1")));
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(RevisionOk(Draft()));
        world.Writer.EnqueueReview(ReviewOk());

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        var versions = await world.VersionsAsync();
        versions.Count.ShouldBe(2, "the fact-ref guard never applies to fiction");
        versions[1].Source.ShouldBe(StoryVersionSource.Revised);
    }

    [Fact]
    public async Task RevisedDraftChangingSegmentCountAndAnUnaddressedSegment_RecordsAdvisoryDrift()
    {
        var world = await RevisionWorld.CreateAsync(Database, Ct);
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(RevisionOk(Draft() with
        {
            Paragraphs =
            [
                new StorySegmentV1("Troi chuyen mua tren con pho vang va chiec dong ho ga tau diem muoi hai tieng.", ["k2"], []),
                new StorySegmentV1("Doan hai duoc them vao ban sua.", ["k4"], []),
            ],
        }));
        world.Writer.EnqueueReview(ReviewOk());

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryReview, "drift is advisory and never blocks the episode");
        var versions = await world.VersionsAsync();
        versions.Count.ShouldBe(2);
        var block = RevisionBlock(versions[1]);
        block["applied"]!.GetValue<bool>().ShouldBeTrue();
        var drift = block["drift"]!;
        drift["segment_count_changed"]!.GetValue<bool>().ShouldBeTrue();
        drift["changed_segments"]!.AsArray().Select(node => node!.GetValue<string>())
            .ShouldContain("p:0", "the untouched paragraph was rewritten");

        var issues = block["review_after"]!["issues"]!.AsArray();
        var driftIssue = issues.Single(issue => issue!["type"]!.GetValue<string>() == AuditReportBuilder.RevisionDriftType);
        driftIssue!["severity"]!.GetValue<string>().ShouldBe("advisory");
        driftIssue!["kind"]!.GetValue<string>().ShouldBe("objective");
    }

    [Fact]
    public async Task CleanOriginalFlaggedRevision_KeepsTheOriginalDraft()
    {
        // A1: a revision the local moderator flags must not replace a CLEAN original — the revised text is never
        // stored, so the run keeps the generated row and records the moderation-flagged reason. No moderation event is
        // written for text that is not stored (the table points at the episode, not at a story version).
        var world = await RevisionWorld.CreateAsync(Database, Ct);
        world.Moderator.EnqueueClean(ModerationStage.Draft);
        world.Moderator.EnqueueFlagged(ModerationStage.Draft, "hate/threatening");
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(RevisionOk(RevisedDraft()));

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded, "a flagged revision degrades, it never fails the run");
        var version = (await world.VersionsAsync()).ShouldHaveSingleItem();
        version.Source.ShouldBe(StoryVersionSource.Generated, "a flagged revision of a clean original never becomes current");
        var block = RevisionBlock(version);
        block["applied"]!.GetValue<bool>().ShouldBeFalse();
        block["reason"]!.GetValue<string>().ShouldBe(AuditReportBuilder.RevisionReasonModerationFlagged);
        world.Events.Stages.ShouldBeEmpty("the revised text is not stored, so no moderation event is written for it");
        world.Writer.ReviewRequests.Count.ShouldBe(1, "review #2 never runs for a revision that was not applied");
        world.Moderator.Checks.Count(check => check.Stage == ModerationStage.Draft)
            .ShouldBe(2, "the original draft and the revised draft are both checked");
    }

    [Fact]
    public async Task FlaggedOriginalFlaggedRevision_IsStillAppliedWithTheModerationBlock()
    {
        // A1, unchanged behaviour: when the original draft was itself flagged, the revision is recorded like the first
        // draft (D12) — both decisions write a moderation event and the flagged revised row can still become current.
        var world = await RevisionWorld.CreateAsync(Database, Ct);
        world.Moderator.EnqueueFlagged(ModerationStage.Draft, "hate/threatening");
        world.Moderator.EnqueueFlagged(ModerationStage.Draft, "violence");
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(RevisionOk(RevisedDraft()));
        world.Writer.EnqueueReview(ReviewOk());

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        var versions = await world.VersionsAsync();
        versions.Count.ShouldBe(2, "a flagged revision of an already-flagged draft still becomes current");
        var block = RevisionBlock(versions[1]);
        block["applied"]!.GetValue<bool>().ShouldBeTrue();
        var audit = JsonNode.Parse(versions[1].AuditReport!)!;
        audit["moderation"]!["flagged"]!.GetValue<bool>().ShouldBeTrue();
        audit["moderation"]!["categories"]!.AsArray().Select(node => node!.GetValue<string>())
            .ShouldContain("violence");
        world.Events.Stages.ShouldBe([ModerationStage.Draft, ModerationStage.Draft], "the original and the revised draft each record their flag");
        world.Moderator.Checks.Count(check => check.Stage == ModerationStage.Draft)
            .ShouldBe(2, "the original draft and the revised draft are both checked");
    }

    [Fact]
    public async Task DeadlineTooShortForTheRevisionStage_DegradesWithTheUnavailableReason()
    {
        // A2: the run deadline cannot fit the revise call, review #2 and the save margin (the defaults need 165 s), so
        // the stage degrades like a refused gate: no revise call, the generated row saved, the episode still reaches
        // story_review — never a deadline failure.
        var world = await RevisionWorld.CreateAsync(
            Database,
            Ct,
            jobOptions: new StoryJobOptions { TimeoutSeconds = 60, TimeoutSecondsLong = 60 });
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded, "an unfittable revision degrades, it never fails the run");
        world.Writer.RevisionRequests.ShouldBeEmpty("no revision call without the time to finish it");
        var version = (await world.VersionsAsync()).ShouldHaveSingleItem();
        version.Source.ShouldBe(StoryVersionSource.Generated);
        var block = RevisionBlock(version);
        block["applied"]!.GetValue<bool>().ShouldBeFalse();
        block["reason"]!.GetValue<string>().ShouldBe(AuditReportBuilder.RevisionReasonUnavailable);
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryReview, "the episode still reaches story_review");
        world.Progress.StageCalls.ShouldBe(AttemptedNotAppliedStages);
    }

    [Fact]
    public async Task RevisionWindowClosingBeforeTheDeadline_DegradesWithTheUnavailableReason()
    {
        // A2: the revise call, the revised draft's moderation and review #2 run under a window that closes the save
        // margin before the run deadline. A call that never returns inside it degrades the revision instead of the
        // deadline failing the run. The headroom keys are shrunk so the window closes in seconds instead of minutes.
        var world = await RevisionWorld.CreateAsync(
            Database,
            Ct,
            jobOptions: new StoryJobOptions
            {
                TimeoutSeconds = 120,
                TimeoutSecondsLong = 120,
                RevisionRequestTimeoutSeconds = 1,
                ReviewRequestTimeoutSeconds = 1,
                SaveMarginSeconds = 112,
            });
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevisionBlocking();

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded, "the window degrades the revision, it never fails the run");
        world.Writer.RevisionRequests.Count.ShouldBe(1);
        world.Writer.ReviewRequests.Count.ShouldBe(1, "review #2 never runs for a revision that was not applied");
        var version = (await world.VersionsAsync()).ShouldHaveSingleItem();
        version.Source.ShouldBe(StoryVersionSource.Generated);
        var block = RevisionBlock(version);
        block["applied"]!.GetValue<bool>().ShouldBeFalse();
        block["reason"]!.GetValue<string>().ShouldBe(AuditReportBuilder.RevisionReasonUnavailable);
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryReview);
    }

    [Fact]
    public async Task RevisionFinishingTooLateForReviewTwo_DegradesWithTheUnavailableReason()
    {
        // A2's second milestone: the revise call returned inside the window, but what is left of the deadline no longer
        // fits review #2 and the save margin (ReviewAfterHeadroom), so the revision is not applied and review #2 is
        // never paid for. The headroom keys are shrunk so the milestone is reached in seconds instead of minutes.
        var world = await RevisionWorld.CreateAsync(
            Database,
            Ct,
            jobOptions: new StoryJobOptions
            {
                TimeoutSeconds = 60,
                TimeoutSecondsLong = 60,
                RevisionRequestTimeoutSeconds = 1,
                ReviewRequestTimeoutSeconds = 53,
                SaveMarginSeconds = 1,
            });
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevisionDelayed(TimeSpan.FromSeconds(8), RevisionOk(RevisedDraft()));

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded, "an unfinished revision degrades, it never fails the run");
        world.Writer.RevisionRequests.Count.ShouldBe(1);
        world.Writer.ReviewRequests.Count.ShouldBe(1, "review #2 is not paid for once the deadline no longer fits it");
        var version = (await world.VersionsAsync()).ShouldHaveSingleItem();
        version.Source.ShouldBe(StoryVersionSource.Generated);
        var block = RevisionBlock(version);
        block["applied"]!.GetValue<bool>().ShouldBeFalse();
        block["reason"]!.GetValue<string>().ShouldBe(AuditReportBuilder.RevisionReasonUnavailable);
    }

    [Fact]
    public async Task AppliedRevision_ReadsTheSimilarityCorpusOnce()
    {
        // A4: both saved rows are scored against the same corpus read of the episode.
        var world = await RevisionWorld.CreateAsync(Database, Ct);
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(RevisionOk(RevisedDraft()));
        world.Writer.EnqueueReview(ReviewOk());

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        (await world.VersionsAsync()).Count.ShouldBe(2, "the revision was applied, so both rows were scored");
        world.Corpus.Loads.ShouldBe(1, "the corpus is loaded once per run, not once per scored row");
    }

    [Fact]
    public async Task ReviewTwoUnusable_KeepsOriginalDraftWithReviewUnavailableReason()
    {
        var world = await RevisionWorld.CreateAsync(Database, Ct);
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(RevisionOk(RevisedDraft()));
        world.Writer.EnqueueReview(TextCallResult<StoryReviewV1>.InvalidOutput(Model, null, null, ["schema_invalid"]));

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        world.Writer.ReviewRequests.Count.ShouldBe(2, "review #2 runs once and is never retried");
        var version = (await world.VersionsAsync()).ShouldHaveSingleItem();
        var block = RevisionBlock(version);
        block["applied"]!.GetValue<bool>().ShouldBeFalse();
        block["reason"]!.GetValue<string>().ShouldBe(AuditReportBuilder.RevisionReasonReviewUnavailable);
    }

    [Fact]
    public async Task ProviderFundsExhaustedOnRevision_StaysQueuedWithPauseReason()
    {
        var world = await RevisionWorld.CreateAsync(Database, Ct);
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevision(TextCallResult<StoryContentV1>.ProviderFundsExhausted(Model, "req-revision"));

        await world.RunAsync(Ct);

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Queued);
        run.PauseReason.ShouldBe(GenerationPauseReason.ProviderFundsExhausted);
        run.CountsTowardLimit.ShouldBeFalse();
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryGenerating);
        (await world.VersionCountAsync()).ShouldBe(0);
        world.Ledger.Refunds.ShouldBeEmpty();
    }

    [Fact]
    public async Task CancellationDuringRevision_PropagatesAndWritesNoVersionRow()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var world = await RevisionWorld.CreateAsync(Database, Ct);
        world.Writer.EnqueueWrite(WriteOk());
        world.Writer.EnqueueReview(ReviewOk(BlockingHook()));
        world.Writer.EnqueueRevisionCancelling(cancellation);

        await Should.ThrowAsync<OperationCanceledException>(() => world.RunAsync(cancellation.Token));

        world.Writer.RevisionRequests.Count.ShouldBe(1);
        (await world.VersionCountAsync()).ShouldBe(0, "the caller's token cancels the run before the saving stage");
    }

    private static readonly GenerationStage[] NoRevisionStages =
    [
        GenerationStage.Writing,
        GenerationStage.Checking,
        GenerationStage.Checking,
        GenerationStage.Reviewing,
        GenerationStage.Scoring,
        GenerationStage.Saving,
        GenerationStage.Done,
    ];

    private static readonly GenerationStage[] AttemptedNotAppliedStages =
    [
        GenerationStage.Writing,
        GenerationStage.Checking,
        GenerationStage.Checking,
        GenerationStage.Reviewing,
        GenerationStage.Revising,
        GenerationStage.Scoring,
        GenerationStage.Saving,
        GenerationStage.Done,
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task AssertKeptOriginalAsync(RevisionWorld world)
    {
        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded, "an unusable revision output is not a run failure");
        world.Writer.RevisionRequests.Count.ShouldBe(1, "Goal: exactly one revision call, never retried");
        var version = (await world.VersionsAsync()).ShouldHaveSingleItem();
        version.Source.ShouldBe(StoryVersionSource.Generated);
        var block = RevisionBlock(version);
        block["applied"]!.GetValue<bool>().ShouldBeFalse();
        block["reason"]!.GetValue<string>().ShouldBe(AuditReportBuilder.RevisionReasonInvalidOutput);
    }

    private static JsonNode RevisionBlock(StoryVersion version) =>
        JsonNode.Parse(version.AuditReport!)!["revision"]
        ?? throw new InvalidOperationException("the row carries no revision block.");

    private static TextCallResult<StoryContentV1> WriteOk(StoryContentV1? draft = null) =>
        TextCallResult<StoryContentV1>.Ok(draft ?? Draft(), Model, null, "req-writer");

    private static TextCallResult<StoryContentV1> RevisionOk(StoryContentV1 draft) =>
        TextCallResult<StoryContentV1>.Ok(draft, Model, null, "req-revision");

    private static TextCallResult<StoryReviewV1> ReviewOk(params ReviewIssueV1[] issues) =>
        TextCallResult<StoryReviewV1>.Ok(Review(issues), Model, null, "req-review");

    private static ReviewIssueV1 BlockingHook() =>
        new("i1", "hook_missing", "blocking", "hook", "Mo dau.", "Thieu moc.", "Them moc ro rang.");

    private static StoryReviewV1 Review(IReadOnlyList<ReviewIssueV1> issues) => new(
        new ReviewScores(4, 4, 4, 4, 4, null, 4),
        [.. issues],
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

    /// <summary>A draft whose hook cites <paramref name="factRef"/>; used to prove an unapproved citation is refused.</summary>
    private static StoryContentV1 Cited(string factRef) => Draft() with
    {
        Hook = new StorySegmentV1($"Mo dau {factRef}.", ["k1"], [factRef]),
    };

    /// <summary>A draft whose hook cites every id in <paramref name="factRefs"/>, in the given order.</summary>
    private static StoryContentV1 CitedAll(params string[] factRefs) => Draft() with
    {
        Hook = new StorySegmentV1("Mo dau hai nguon da duoc kiem chung.", ["k1"], [.. factRefs]),
    };

    private static StoryContentV1 RevisedDraft() => Draft() with
    {
        Hook = new StorySegmentV1("Mo dau moi voi loi dan ro rang hon.", ["k1"], []),
        Paragraphs = [new StorySegmentV1("Doan mot da duoc viet lai cho gon hon.", ["k2"], [])],
    };

    /// <summary>Refuses every check past <paramref name="allow"/>: the writer gate and the review gate pass, the revision gate does not.</summary>
    private sealed class StagedRevisionBudgetGuard(int allow) : IBudgetGuard
    {
        private int _calls;

        public ValueTask<ErrorOr<Success>> CheckAsync(CostBound worstCaseUsd, CancellationToken ct, Guid? excludingRunId = null) =>
            ValueTask.FromResult<ErrorOr<Success>>(
                ++_calls > allow ? Error.Failure(ErrorCodes.SpendCapReached, "cap") : Result.Success);
    }
}
