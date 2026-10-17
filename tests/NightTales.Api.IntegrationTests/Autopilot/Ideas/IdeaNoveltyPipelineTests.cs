using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Fixtures;
using NightTales.TestKit.Moderation;
using NightTales.TestKit.Text;
using Shouldly;
using Xunit;

using static NightTales.Api.IntegrationTests.Autopilot.Ideas.IdeaPlannerHarness;

namespace NightTales.Api.IntegrationTests.Autopilot.Ideas;

/// <summary>
/// The pipeline half of 02.8.1-T2 (task Design "Novelty", "Content policy", "Scoring and pick"): one real run
/// over the production graph — the real lexical shingle pass, the real corpus reader, the real novelty checker
/// with its one judge call per batch, the real content-policy pre-check and the real scorer — with the provider
/// edge scripted on <see cref="TextStep.Novelty"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every assertion of the measurements is made on the stored <c>episode_ideas</c> rows (status, rejection
/// reason, score, similarity and the <c>novelty</c> document), never on a mock of the checker: what these tests
/// pin is what a run leaves behind. The judge's own model is never reached — <see cref="FakeTextClient"/> answers
/// the step — and the moderation endpoint is a <see cref="FakeContentModerator"/>.
/// </para>
/// <para>
/// The batches of the corpus tests are built from <see cref="DistinctIdea"/>: the shared <see cref="Idea"/> shape
/// repeats its logline and setting in every candidate, which the shingle pass of two candidates of one batch would
/// read as shared material, and a novelty test must not start from a corpus that is already half-matched.
/// </para>
/// </remarks>
public sealed class IdeaNoveltyPipelineTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>
    /// The one judge call of a batch: five candidates, one call, five stored score rows and one pick. The data
    /// object the judge sees carries the whole batch and the series' control values.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_JudgeCallIsOnePerBatch()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(
                DistinctIdea("Ý tưởng một"),
                DistinctIdea("Ý tưởng hai"),
                DistinctIdea("Ý tưởng ba"),
                DistinctIdea("Ý tưởng bốn"),
                DistinctIdea("Ý tưởng năm")),
            Usage);
        text.EnqueueOk(
            TextStep.Novelty,
            NoveltyBatch(
                JudgeRow(0, 0.1m),
                JudgeRow(1, 0.1m),
                JudgeRow(2, 0.1m),
                JudgeRow(3, 0.1m),
                JudgeRow(4, 0.1m)),
            Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 5);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse(pick.Errors.Count > 0 ? pick.FirstError.Description : null);
        text.Calls(TextStep.Novelty).ShouldBe(1, "one judge call per batch, whatever the batch's size (task Decision)");
        var judge = NoveltyCalls(text).ShouldHaveSingleItem();
        judge.Run.GenerationRunId.ShouldBe(run.GenerationRunId);
        judge.Run.Attempt.ShouldBe(1);
        DataObjectText(judge, "task").ShouldBe("score_ideas");
        DataObjectText(judge, "language").ShouldBe("vi");
        DataObjectText(judge, "topic").ShouldBe("ghost");
        DataObjectArrayLength(judge, "ideas").ShouldBe(5, "the judge sees the whole batch in one call");
        DataObjectArrayLength(judge, "past_episodes").ShouldBe(0, "a first episode has no past");

        IdeaCount(Database, world.SeriesId).ShouldBe(5, "every candidate is stored, judged or not");
        foreach (var title in new[] { "Ý tưởng một", "Ý tưởng hai", "Ý tưởng ba", "Ý tưởng bốn", "Ý tưởng năm" })
        {
            NoveltyJudgePresence(Database, IdeaIdOf(Database, world.SeriesId, title))
                .ShouldBe("judge", "the judge scored every candidate it was sent");
        }

        // The judge's own numbers decide the pick: freshness 1 − 0.1, fit 1.0 and hook 0.5 of the scripted row.
        pick.Value.Score.ShouldBe(0.83m, "0.45 × (1 − 0.1) + 0.30 × 1.0 + 0.25 × 0.5");
        pick.Value.Similarity.ShouldBe(0.1m, "no lexical overlap, so the judge's score is the batch's similarity");
    }

    /// <summary>
    /// F32's lexical ceiling: a candidate the shingle pass finds identical to an approved episode is rejected
    /// <c>novelty_lexical</c> with the corpus row it overlapped, and the rest of the batch is still scored and
    /// picked.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_LexicalOverThreshold_RejectsWithNoveltyLexical()
    {
        var world = await SeedAsync(Database);
        var copy = await SeedCorpusCopyAsync(Database, world, "Bản sao cũ");
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(DistinctIdea("Bản sao cũ"), DistinctIdea("Ý tưởng mới toanh")),
            Usage);
        text.EnqueueOk(
            TextStep.Novelty,
            NoveltyBatch(JudgeRow(0, 0.1m), JudgeRow(1, 0.2m, fit: 0.8m, hookStrength: 0.6m)),
            Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        var copied = IdeaIdOf(Database, world.SeriesId, "Bản sao cũ");
        var fresh = IdeaIdOf(Database, world.SeriesId, "Ý tưởng mới toanh");

        IdeaOutcome(Database, copied).ShouldBe(
            "rejected|novelty_lexical|0.425|1",
            "freshness is 1 − max(lexical, judge) = 1 − 1 for a copy, so the score is 0.30 × 1 + 0.25 × 0.5");
        NoveltyScores(Database, copied).ShouldBe($"1|0.1|null|episode|{Pg(copy)}");
        IdeaOutcome(Database, fresh).ShouldBe("picked|-|0.75|0.2");
        pick.Value.IdeaId.ShouldBe(fresh, "a lexically blocked candidate can never win the pick");
        pick.Value.Score.ShouldBe(0.75m, "0.45 × (1 − 0.2) + 0.30 × 0.8 + 0.25 × 0.6");
    }

    /// <summary>
    /// F32's judge ceiling: a score <b>above</b> <c>JudgeMax</c> rejects, the threshold itself does not (the
    /// comparison is strict), and the rejected candidate keeps the judge's reading in its stored document.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_JudgeOverThreshold_RejectsWithNoveltyJudge()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(DistinctIdea("Ý tưởng đúng ngưỡng"), DistinctIdea("Ý tưởng vượt ngưỡng")),
            Usage);
        text.EnqueueOk(
            TextStep.Novelty,
            NoveltyBatch(JudgeRow(0, 0.7m), JudgeRow(1, 0.71m)),
            Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        var atThreshold = IdeaIdOf(Database, world.SeriesId, "Ý tưởng đúng ngưỡng");
        var above = IdeaIdOf(Database, world.SeriesId, "Ý tưởng vượt ngưỡng");

        IdeaOutcome(Database, atThreshold).ShouldStartWith("picked|-", Case.Sensitive, "0.70 is not above the 0.70 ceiling");
        IdeaOutcome(Database, above).ShouldStartWith("rejected|novelty_judge");
        NoveltyScores(Database, above).ShouldBe("0|0.71|null|null|null");
        pick.Value.IdeaId.ShouldBe(atThreshold);
    }

    /// <summary>
    /// The degradation rule (task Design "Novelty"): a judge call that fails — refused twice, a provider error or
    /// an outage — leaves the run standing. Every candidate keeps a null judge, freshness becomes
    /// <c>1 − lexical</c> and the neutral fit/hook, the run picks, and only a refused output earns the one retry.
    /// </summary>
    /// <param name="outcome">The scripted judge outcome.</param>
    /// <param name="judgeCalls">How many judge calls that outcome costs.</param>
    [Theory]
    [InlineData("invalid-twice", 2)]
    [InlineData("provider-error", 1)]
    [InlineData("provider-unavailable", 1)]
    public async Task ProposeAndPick_JudgeFails_DegradesToLexicalOnly(string outcome, int judgeCalls)
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(DistinctIdea("Ý tưởng sống sót")), Usage);
        switch (outcome)
        {
            case "invalid-twice":
                text.Enqueue(TextStep.Novelty, TextCallResult<NoveltyV1>.InvalidOutput(Model, Usage, "req-fake", [TextCallInvalidOutput<NoveltyV1>.SchemaInvalid]))
                    .Enqueue(TextStep.Novelty, TextCallResult<NoveltyV1>.InvalidOutput(Model, Usage, "req-fake", [TextCallInvalidOutput<NoveltyV1>.SchemaInvalid]));
                break;
            case "provider-error":
                text.Enqueue(TextStep.Novelty, TextCallResult<NoveltyV1>.ProviderError(Model, 500, "req-fake"));
                break;
            default:
                text.Enqueue(TextStep.Novelty, TextCallResult<NoveltyV1>.ProviderUnavailable(Model, "req-fake"));
                break;
        }

        // The retry re-checks the platform cap, and the real guard sums the whole platform's spend, which a
        // shared class database cannot pin: only the refused-output case asks it at all.
        await using var worker = await StartWorkerAsync(
            Database,
            text,
            count: 1,
            guard: new StubBudgetGuard(allowed: true),
            estimator: new StubIdeaCostEstimator(new UsageBound(0.25m, 1_000, 1_500)));

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse("a judge that fails never fails the run");
        NoveltyCalls(text).Count.ShouldBe(judgeCalls, "only an output the schema refused is retried (D7)");
        var ideaId = pick.Value.IdeaId;
        NoveltyJudgePresence(Database, ideaId).ShouldBe("judge-null", "the degraded batch stores a null judge");
        NoveltyScores(Database, ideaId).ShouldBe("0|-|null|null|null");
        IdeaOutcome(Database, ideaId).ShouldBe("picked|-|0.875|0", "freshness 1 − 0 with the neutral fit 1.0 and hook 0.5");
        pick.Value.Score.ShouldBe(0.875m);
        UsageCount(Database, world.UserId).ShouldBe(1 + judgeCalls, "every dispatched call is metered (D14)");
    }

    /// <summary>
    /// TL ruling #3: a provider refusal <b>of the judge call</b> writes one <c>moderation_events</c> row with
    /// <c>stage = ideas</c> — distinct from the per-candidate <c>brief</c> pre-check — and the batch degrades like
    /// any other judge failure.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_JudgeModerated_WritesModerationEventStageIdeas_AndDegrades()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(DistinctIdea("Ý tưởng bị từ chối")), Usage);
        text.Enqueue(TextStep.Novelty, TextCallResult<NoveltyV1>.Moderated(Model, Usage, "req-fake", ["violence"]));
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse("a refusal degrades the batch, it does not fail the run");
        ModerationRows(Database, world.UserId).ShouldBe(
            "openai_text|ideas|true|violence|true",
            "the refusal of the run's own paid call: stage = the run kind, series-level, no excerpt (D19)");
        NoveltyJudgePresence(Database, pick.Value.IdeaId).ShouldBe("judge-null");
        pick.Value.Score.ShouldBe(0.875m, "lexical-only scoring: 1 − 0 freshness with the neutral fit and hook");
    }

    /// <summary>
    /// The banned-theme backstop (Design "Content policy"): a theme of the series in a candidate's text rejects
    /// that candidate for free — the moderator is not asked about it — and the rest of the batch is still scored.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_BannedThemeMatch_RejectsCandidate()
    {
        var world = await SeedAsync(Database, autopilotSettings: """{"banned_themes":["tự tử"]}""");
        var run = await CreateRunAsync(Database, world);
        var moderator = new FakeContentModerator();
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(
                IdeaWithLogline("Ý tưởng chạm chủ đề cấm", "Một chuyện về chuyện tự tử của người hàng xóm."),
                DistinctIdea("Ý tưởng sạch")),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.1m), JudgeRow(1, 0.1m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2, moderator: moderator);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        var bannedId = IdeaIdOf(Database, world.SeriesId, "Ý tưởng chạm chủ đề cấm");
        IdeaOutcome(Database, bannedId).ShouldStartWith("rejected|policy_banned_theme");
        NoveltyScores(Database, bannedId).ShouldBe("0|0.1|null|null|null", "the batch was still measured and scored");
        pick.Value.IdeaId.ShouldBe(IdeaIdOf(Database, world.SeriesId, "Ý tưởng sạch"));
        moderator.Calls.ShouldBe(1, "a candidate the banned-theme check stopped never reaches the moderator");
        var clean = DistinctCandidate("Ý tưởng sạch");
        moderator.Checks[0].Texts.ShouldBe([clean.Logline, clean.Hook], "one check per cleared candidate, logline and hook together");
        moderator.Checks[0].Stage.ShouldBe(ModerationStage.Brief);
    }

    /// <summary>
    /// D12's per-candidate pre-check: a flagged logline/hook rejects the candidate <c>policy_moderated</c> and
    /// writes one <c>moderation_events</c> row with <c>stage = brief</c>, the categories and the flagged text as
    /// its excerpt, while the cleared candidate is picked.
    /// </summary>
    /// <remarks>
    /// The moderated usage row itself (<c>operation = moderation</c>, <c>cost_usd = 0</c>, D12/D14) is written by
    /// the real moderator's own meter, which a harness fake deliberately does not have: it is asserted end to end
    /// by <c>ContentModeratorTests.Moderator_RecordsUsageWithOperationModerationAndZeroCost</c>. What this test
    /// pins is the pipeline's half — the flag becomes a rejection and an evidence row, and the run buys no
    /// moderation usage row of its own.
    /// </remarks>
    [Fact]
    public async Task ProposeAndPick_ModeratorFlags_RejectsAndWritesModerationEvent()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var moderator = new FakeContentModerator().EnqueueFlagged(ModerationStage.Brief, "sexual");
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(DistinctIdea("Ý tưởng bị gắn cờ"), DistinctIdea("Ý tưởng được duyệt")),
            Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2, moderator: moderator);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        var flagged = IdeaIdOf(Database, world.SeriesId, "Ý tưởng bị gắn cờ");
        IdeaOutcome(Database, flagged).ShouldStartWith("rejected|policy_moderated");
        ModerationRows(Database, world.UserId).ShouldBe(
            "openai_text|brief|true|sexual|false",
            "one row per flagged candidate, with the flagged text as its excerpt");
        pick.Value.IdeaId.ShouldBe(IdeaIdOf(Database, world.SeriesId, "Ý tưởng được duyệt"));
        moderator.Calls.ShouldBe(2, "the check is asked once per candidate, logline and hook together");
        moderator.Checks.ShouldAllBe(check => check.Stage == ModerationStage.Brief);
        UsageCount(Database, world.UserId).ShouldBe(2, "the run's own rows are the two text calls; nothing else is metered here");
    }

    /// <summary>
    /// F31/F33's framing flag on the real checker: a true-crime series' candidate that carries a framing phrase
    /// comes back <c>RequiresResearch</c>, a plain angle of the same series does not, and no other topic arms the
    /// check at all.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_TrueCrime_SetsRequiresResearch()
    {
        const string framing = "dựa trên sự thật";
        var world = await SeedAsync(Database, Topic.TrueCrime);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(IdeaWithLogline("Một góc nhìn chưa kể", $"Chuyện {framing} về một vụ mất tích năm 1998.")),
            Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1, framingLexicon: [framing]);
        using var scope = OpenScope(worker, world.UserId);

        var flagged = Guid.CreateVersion7();
        var plain = Guid.CreateVersion7();
        var batch = new List<(Guid IdeaId, IdeaCandidate Candidate)>
        {
            (flagged, CandidateWithLogline("Góc nhìn có thật", $"Chuyện {framing} về một vụ mất tích năm 1998.")),
            (plain, CandidateWithLogline("Góc nhìn thường", "Một chuyện ma trong ngôi nhà bên sông.")),
        };

        var trueCrime = await scope.Policy.CheckAsync(
            batch,
            new IdeaPolicyContext(IdeaPolicyContext.TrueCrimeTopic, []),
            run,
            Ct);

        trueCrime.ByIdeaId[flagged].RequiresResearch.ShouldBeTrue("the framing lexicon arms the flag (F31/F33)");
        trueCrime.ByIdeaId[plain].RequiresResearch.ShouldBeFalse("a plain angle of a true-crime series needs no research");
        trueCrime.ByIdeaId[flagged].RejectReason.ShouldBeNull("research is a signal for the story gate, never a rejection");

        var ghost = await scope.Policy.CheckAsync(batch, new IdeaPolicyContext("ghost", []), run, Ct);
        ghost.ByIdeaId.Values.ShouldAllBe(verdict => !verdict.RequiresResearch, "only true_crime arms the framing check");

        // The pick carries the flag: T1's topic rule stays and the pre-check's own reading joins it (IDEA-S52-6).
        var pick = await ProposeAsync(worker, run);
        pick.IsError.ShouldBeFalse();
        pick.Value.RequiresResearch.ShouldBeTrue();
    }

    /// <summary>
    /// The <c>NoIdea</c> outcome: every candidate rejected, the rows stored <c>rejected</c> with their own
    /// reasons, and the error naming the batch's most common reason (highest count, then alphabetical).
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_AllCandidatesRejected_ReturnsNoIdeaWithMostCommonReason()
    {
        var world = await SeedAsync(Database, autopilotSettings: """{"banned_themes":["tự tử"]}""");
        _ = await SeedCorpusCopyAsync(Database, world, "Bản sao cũ");
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(
                IdeaWithLogline("Ý tưởng chạm chủ đề cấm", "Một chuyện về chuyện tự tử của người hàng xóm."),
                DistinctIdea("Bản sao cũ")),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.1m), JudgeRow(1, 0.1m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2);

        var result = await ProposeAsync(worker, run);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.IdeasNoIdea);
        result.FirstError.Description.ShouldContain(IdeaRejectionReason.NoveltyLexical, Case.Sensitive, "one reason each, so the tie breaks alphabetically");
        IdeaCount(Database, world.SeriesId).ShouldBe(2, "the batch is stored whole, rejected or not");
        IdeaOutcome(Database, IdeaIdOf(Database, world.SeriesId, "Ý tưởng chạm chủ đề cấm"))
            .ShouldStartWith("rejected|policy_banned_theme");
        IdeaOutcome(Database, IdeaIdOf(Database, world.SeriesId, "Bản sao cũ"))
            .ShouldStartWith("rejected|novelty_lexical");
    }

    /// <summary>
    /// The pick: the highest weighted score wins, a candidate that broke no rule but lost the ranking is
    /// <c>not_best</c>, and one a rule rejected keeps that rule's reason.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_PickedIsHighestScore_OthersRejectedWithNotBestOrTheirReason()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(DistinctIdea("Ý tưởng điểm thấp"), DistinctIdea("Ý tưởng điểm cao"), DistinctIdea("Ý tưởng bị loại")),
            Usage);
        text.EnqueueOk(
            TextStep.Novelty,
            NoveltyBatch(
                JudgeRow(0, 0.1m, fit: 0.2m, hookStrength: 0.3m),
                JudgeRow(1, 0.1m, fit: 1m, hookStrength: 1m),
                JudgeRow(2, 0.9m, fit: 0.5m, hookStrength: 0.5m)),
            Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 3);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        var low = IdeaIdOf(Database, world.SeriesId, "Ý tưởng điểm thấp");
        var high = IdeaIdOf(Database, world.SeriesId, "Ý tưởng điểm cao");
        var blocked = IdeaIdOf(Database, world.SeriesId, "Ý tưởng bị loại");

        pick.Value.IdeaId.ShouldBe(high);
        pick.Value.Score.ShouldBe(0.955m);
        IdeaOutcome(Database, low).ShouldBe("rejected|not_best|0.54|0.1");
        IdeaOutcome(Database, blocked).ShouldBe("rejected|novelty_judge|0.32|0.9");
        IdeaState(Database, high).ShouldBe("picked|-");
    }

    /// <summary>
    /// The stored <c>similarity</c> column is the larger of the two raw readings — the one the stricter threshold
    /// answers to — so a lexically-blocked candidate keeps 1.0 and a judge-only candidate keeps the judge's score.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_SimilarityIsMaxOfLexicalAndJudge()
    {
        var world = await SeedAsync(Database);
        _ = await SeedCorpusCopyAsync(Database, world, "Bản sao cũ");
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(DistinctIdea("Bản sao cũ"), DistinctIdea("Ý tưởng mới")),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.1m), JudgeRow(1, 0.42m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        IdeaOutcome(Database, IdeaIdOf(Database, world.SeriesId, "Bản sao cũ"))
            .ShouldEndWith("|1", Case.Sensitive, "the lexical reading 1.0 is larger than the judge's 0.1");
        IdeaOutcome(Database, pick.Value.IdeaId).ShouldEndWith("|0.42", Case.Sensitive, "the judge's reading is larger than the lexical 0");
        pick.Value.Similarity.ShouldBe(0.42m);
    }

    /// <summary>
    /// The corpus' second source: a <c>proposed</c> idea of an earlier run of the same series is compared against,
    /// so this batch cannot buy the same story again — and the stored document says which pending idea it hit.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_PendingIdeasFromTheSameSeriesAreInTheCorpus()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);

        // The pending idea hangs off this same run, because the partial unique index allows one active
        // series-level idea run per series; what the corpus excludes is the batch's own idea ids, not its run.
        var pending = await SeedPendingCopyAsync(Database, world, run.GenerationRunId, "Bản sao cũ");

        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(DistinctIdea("Bản sao cũ"), DistinctIdea("Ý tưởng khác hẳn")),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.1m), JudgeRow(1, 0.1m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        var copy = IdeaIdOfOther(Database, world.SeriesId, "Bản sao cũ", pending);
        IdeaOutcome(Database, copy).ShouldStartWith("rejected|novelty_lexical");
        NoveltyScores(Database, copy).ShouldBe($"1|0.1|null|pending_idea|{Pg(pending)}");
        NoveltyCorpusCounts(Database, copy).ShouldBe("0|1", "the corpus saw the earlier run's pending idea");
        pick.Value.IdeaId.ShouldBe(IdeaIdOf(Database, world.SeriesId, "Ý tưởng khác hẳn"));
    }

    /// <summary>
    /// The corpus is the approved versions only, reached through <c>episodes.approved_story_version_id</c>: a
    /// draft of the same series is invisible to it, and the summary travels as the approved version's description.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_CorpusIsNewest100ApprovedOnly()
    {
        var world = await SeedAsync(Database);
        _ = await SeedApprovedEpisodeAsync(Database, world, "Tập cũ về ngọn hải đăng", "Một câu chuyện rất khác về con tàu.");
        _ = await SeedEpisodeAsync(Database, world);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(DistinctIdea("Ý tưởng mới")), Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.1m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        var judge = NoveltyCalls(text).ShouldHaveSingleItem();
        DataObjectArrayLength(judge, "past_episodes").ShouldBe(1, "the series' draft is not an approved episode");
        DataObjectArrayText(judge, "past_episodes", 0, "title").ShouldBe("Tập cũ về ngọn hải đăng");
        DataObjectArrayText(judge, "past_episodes", 0, "summary").ShouldBe("Một câu chuyện rất khác về con tàu.");
        NoveltyCorpusCounts(Database, pick.Value.IdeaId).ShouldBe("1|0");
    }

    /// <summary>
    /// The judge names a past episode by its <b>1-based index</b>, and the server resolves it defensively: a valid
    /// index becomes the episode's id, an out-of-range or absent one becomes <c>null</c> while the score still
    /// counts (a model's arithmetic may never drop a candidate).
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_JudgeResponseWithIndexOutOfRange_MapsToNullOverlap()
    {
        var world = await SeedAsync(Database);
        var episode = await SeedApprovedEpisodeAsync(Database, world, "Tập cũ về ngọn hải đăng", "Một câu chuyện rất khác về con tàu.");
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(DistinctIdea("Ý tưởng một"), DistinctIdea("Ý tưởng hai"), DistinctIdea("Ý tưởng ba")),
            Usage);
        text.EnqueueOk(
            TextStep.Novelty,
            NoveltyBatch(
                JudgeRow(0, 0.1m, overlapIndex: "1"),
                JudgeRow(1, 0.1m, overlapIndex: "99"),
                JudgeRow(2, 0.1m, overlapIndex: null)),
            Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 3);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        NoveltyScores(Database, IdeaIdOf(Database, world.SeriesId, "Ý tưởng một"))
            .ShouldBe($"0|0.1|{Pg(episode)}|null|null", "the only past episode is index 1");
        NoveltyScores(Database, IdeaIdOf(Database, world.SeriesId, "Ý tưởng hai"))
            .ShouldBe("0|0.1|null|null|null", "an index outside the corpus names no episode, the score still counts");
        NoveltyScores(Database, IdeaIdOf(Database, world.SeriesId, "Ý tưởng ba"))
            .ShouldBe("0|0.1|null|null|null");
        IdeaCount(Database, world.SeriesId).ShouldBe(3, "no candidate was dropped by the judge's arithmetic");
    }

    /// <summary>
    /// D14: the judge is a paid call of the feature, so it is metered inside the judge itself, under the same
    /// <c>operation = idea</c> as the generation call, with a NULL episode and a positive cost.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_RecordsJudgeUsageWithOperationIdea()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(DistinctIdea("Ý tưởng được ghi nhận")), Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.1m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        text.Calls(TextStep.Novelty).ShouldBe(1);
        UsageCount(Database, world.UserId).ShouldBe(2, "the generation call and the judge call, one row each");
        UsageRows(Database, world.UserId).ShouldBe(
            $"idea|true|true|{ActivePriceVersion(Database)}|false || idea|true|true|{ActivePriceVersion(Database)}|false",
            "both rows are operation = idea, series-level, priced (not estimated)");
    }

    /// <summary>
    /// The failure table's saving rule: a batch whose <b>every</b> candidate is lexically blocked skips the judge
    /// call entirely — a rejected candidate cannot be un-rejected, so that call would be billed for nothing — and
    /// the run answers <c>NoIdea(novelty_lexical)</c>.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_EveryCandidateLexicallyBlocked_SkipsTheJudge()
    {
        var world = await SeedAsync(Database);
        _ = await SeedCorpusCopyAsync(Database, world, "Bản sao một");
        _ = await SeedCorpusCopyAsync(Database, world, "Bản sao hai");
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(DistinctIdea("Bản sao một"), DistinctIdea("Bản sao hai")),
            Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2);

        var result = await ProposeAsync(worker, run);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.IdeasNoIdea);
        result.FirstError.Description.ShouldContain(IdeaRejectionReason.NoveltyLexical);
        text.Calls(TextStep.Novelty).ShouldBe(0, "no judge call is made when no candidate could be un-rejected");
        text.Queued(TextStep.Novelty).ShouldBe(1, "only the harness's neutral script, which is never consumed");
        UsageCount(Database, world.UserId).ShouldBe(1, "only the generation call was ever paid for");
        IdeaOutcome(Database, IdeaIdOf(Database, world.SeriesId, "Bản sao một"))
            .ShouldStartWith("rejected|novelty_lexical");
        IdeaOutcome(Database, IdeaIdOf(Database, world.SeriesId, "Bản sao hai"))
            .ShouldStartWith("rejected|novelty_lexical");
    }

    /// <summary>
    /// The one judge outcome that stops the run: an account out of funds propagates
    /// <c>provider_funds_exhausted</c> for the caller to pause the series, and the batch is not stored (the run
    /// has no scores to store).
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_JudgeFundsExhausted_PropagatesProviderFundsExhausted()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(DistinctIdea("Ý tưởng không được chấm")), Usage);
        text.Enqueue(TextStep.Novelty, TextCallResult<NoveltyV1>.ProviderFundsExhausted(Model, "req-fake"));
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var result = await ProposeAsync(worker, run);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.ProviderFundsExhausted);
        IdeaCount(Database, world.SeriesId).ShouldBe(0, "the run has no scores, so it stores no idea");
        ModerationRows(Database, world.UserId).ShouldBeEmpty();
        UsageCount(Database, world.UserId).ShouldBe(1, "no request was sent for the judge, so it cannot have been billed (SEC-1)");
    }

    /// <summary>
    /// The judge's retry is a second paid dispatch, so the platform cap is re-checked with <b>the judge's own</b>
    /// bound before it is bought: a refusal answers <c>spend_cap_reached</c>, no second call is made and the batch
    /// is not stored.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_SpendCapRefusedBeforeTheJudgeRetry_ReturnsSpendCapReached()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(DistinctIdea("Ý tưởng không được chấm")), Usage);
        text.Enqueue(TextStep.Novelty, TextCallResult<NoveltyV1>.InvalidOutput(Model, Usage, "req-fake", [TextCallInvalidOutput<NoveltyV1>.SchemaInvalid]));
        var guard = new StubBudgetGuard(allowed: false);
        var estimator = new StubIdeaCostEstimator(
            new UsageBound(0.11m, 1_000, 1_500),
            noveltyBound: new UsageBound(0.22m, 400, 400));
        await using var worker = await StartWorkerAsync(Database, text, count: 1, guard: guard, estimator: estimator);

        var result = await ProposeAsync(worker, run);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.SpendCapReached);
        NoveltyCalls(text).Count.ShouldBe(1, "the refused retry was never dispatched");
        var check = guard.Checks.ShouldHaveSingleItem();
        check.Bound.WorstCaseUsd.ShouldBe(0.22m, "the checker asks the judge's own bound, not the generation step's");
        check.ExcludingRunId.ShouldBe(run.GenerationRunId, "the run's own reserved bound stays out of the sum");
        UsageCount(Database, world.UserId).ShouldBe(2, "the ideas call and the one judge attempt that was dispatched before the cap refused its retry (D14)");
        IdeaCount(Database, world.SeriesId).ShouldBe(0);
    }

    /// <summary>
    /// The tie rule of the pick: equal scores are broken by the candidate's batch index, so two runs of the same
    /// answer always buy the same idea (determinism for the run log and the calibration histograms).
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_TieOnScore_PicksTheLowestBatchIndex()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(Idea("Ý tưởng đứng trước"), Idea("Ý tưởng đứng sau")),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.2m), JudgeRow(1, 0.2m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        pick.Value.Title.ShouldBe("Ý tưởng đứng trước", "the lowest batch index of the tie wins");
        IdeaOutcome(Database, IdeaIdOf(Database, world.SeriesId, "Ý tưởng đứng sau")).ShouldStartWith("rejected|not_best");
    }

    /// <summary>
    /// Tenant isolation (09 README D10): another user's series, or a series that is not the run's own, answers the
    /// same <c>not_found</c>, no call of any step is made and nothing is stored.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_AnotherUsersSeries_IsNotFoundAndStoresNothing()
    {
        var world = await SeedAsync(Database);
        var other = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(DistinctIdea("Không được dùng")), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var foreign = await ProposeAsync(worker, run, seriesId: other.SeriesId, asUser: world.UserId);
        var wrongRun = await ProposeAsync(worker, run, seriesId: other.SeriesId, asUser: other.UserId);

        foreign.IsError.ShouldBeTrue();
        foreign.FirstError.Code.ShouldBe(ErrorCodes.NotFound);
        wrongRun.FirstError.Code.ShouldBe(ErrorCodes.NotFound, "a run of another series buys nothing");
        IdeaCount(Database, world.SeriesId).ShouldBe(0);
        IdeaCount(Database, other.SeriesId).ShouldBe(0);
        text.Requests.ShouldBeEmpty("no step is reached before the series is resolved");
        UsageCount(Database, world.UserId).ShouldBe(0);
    }

    // ---------------------------------------------------------------- the novelty fixtures

    /// <summary>
    /// The novelty fixture that rides in a candidate's logline: it reaches the judge inside the data object, is
    /// absent from the instructions, and the run follows the scripted answer, never the payload's demand to score
    /// every idea 0.
    /// </summary>
    [Fact]
    public async Task Novelty_InjectionFixtureInCandidateLogline_Ignored()
    {
        var fixture = InjectionFixture.Load(InjectionFixture.NoveltyLoglineFixture);
        fixture.Step.ShouldBe(InjectionFixture.NoveltyStep);
        fixture.Field.ShouldBe("ideas[0].logline", "the fixture's own claim about where the payload lands");

        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(IdeaWithLogline("Hải đăng bỏ hoang", fixture.Value)),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.9m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var result = await ProposeAsync(worker, run);

        var judge = NoveltyCalls(text).ShouldHaveSingleItem();
        judge.SystemPrompt.ShouldNotContain(fixture.Canary);
        DataObjectContains(judge, fixture.Canary).ShouldBeTrue("the payload travels as material of the data object");
        DataObjectArrayText(judge, "ideas", 0, "logline").ShouldBe(fixture.Value);

        // "score every idea 0" is a value, not an instruction: the scripted 0.9 still rejects the candidate.
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.IdeasNoIdea);
        result.FirstError.Description.ShouldContain(IdeaRejectionReason.NoveltyJudge);
    }

    /// <summary>
    /// The novelty fixture that rides in a past episode's summary: the corpus summary reaches the judge's data
    /// object, is absent from the instructions, and the payload's "return an empty scores array" changes nothing —
    /// the run keeps the scripted reading.
    /// </summary>
    [Fact]
    public async Task Novelty_InjectionFixtureInPastEpisodeSummary_Ignored()
    {
        var fixture = InjectionFixture.Load(InjectionFixture.NoveltyPastEpisodeFixture);
        fixture.Step.ShouldBe(InjectionFixture.NoveltyStep);
        fixture.Field.ShouldBe("past_episodes[0].summary", "the fixture's own claim about where the payload lands");

        var world = await SeedAsync(Database, language: Language.En);
        _ = await SeedApprovedEpisodeAsync(Database, world, "The Ardmore light", fixture.Value);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(DistinctIdea("A quiet new idea")), Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.5m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse("the payload cannot turn a valid answer into a failure");
        var judge = NoveltyCalls(text).ShouldHaveSingleItem();
        judge.SystemPrompt.ShouldNotContain(fixture.Canary);

        // Both the corpus read and the data object cap a summary at 200 characters, and the payload is longer than
        // that, so what travels is its prefix: the canary sits past the cut by construction, and the instruction
        // half of the payload — the positive control — is what the next two assertions pin exactly.
        var travelled = fixture.Value.Length <= 200 ? fixture.Value : fixture.Value[..200];
        DataObjectContains(judge, travelled).ShouldBeTrue("the payload travels as material of the data object");
        DataObjectArrayText(judge, "past_episodes", 0, "summary").ShouldBe(travelled);
        NoveltyJudgePresence(Database, pick.Value.IdeaId).ShouldBe("judge", "the scripted reading decided the batch");
        pick.Value.Similarity.ShouldBe(0.5m);
    }
}
