using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Common;
using NightTales.Domain.Stories.Structure;
using Shouldly;

namespace NightTales.Application.UnitTests.Features.Autopilot.Ideas;

/// <summary>
/// The novelty checker's own edges (02.8.1-T2 task "Failure handling and edge cases"): what the lexical pass and
/// the batch's one judge call do with a corpus that is empty, a candidate the past already answered, an answer the
/// model garbled, and a judge that refuses. Every case rides a hand-written judge — no provider and no database —
/// because what is under test is the mapping from the model's answer to the signals the planner scores, not the
/// call itself.
/// </summary>
public sealed class IdeaNoveltyCheckerTests
{
    private const string Model = "gpt-6-luna";

    /// <summary>
    /// The corpus' closest neighbour can never be the candidate itself, so a text that is in the corpus scores 1.0
    /// and is blocked by any sane ceiling: the batch then has no outcome a judge could change.
    /// </summary>
    [Fact]
    public async Task AllCandidatesLexicallyBlocked_SkipsTheJudge()
    {
        var candidate = Candidate(title: "The lighthouse keeper's double", logline: "A keeper sees himself arrive.");
        var episode = Episode(title: candidate.Title, summary: candidate.Logline);
        var judge = new StubJudge();

        var result = await CheckAsync(
            [candidate],
            new NoveltyCorpus([episode], []),
            judge: judge);

        result.IsError.ShouldBeFalse();
        result.Value.JudgeUsed.ShouldBeFalse();
        judge.Requests.ShouldBeEmpty();
        result.Value.ByIdeaId.Values.Single().LexicalScore.ShouldBe(1m);
    }

    /// <summary>
    /// Fewer rows than candidates: the unjudged candidates keep their lexical reading (<c>Judge = null</c>) and the
    /// run continues, because a missing row cannot be told apart from a model that ran out of room.
    /// </summary>
    [Fact]
    public async Task FewerScoresThanCandidates_LeavesTheMissingIndicesUnjudged()
    {
        var batch = new[] { Candidate(title: "A"), Candidate(title: "B"), Candidate(title: "C") };
        var judge = new StubJudge(Row(index: 1, score: 0.4m, overlap: null));

        var result = await CheckAsync(batch, EmptyCorpus, judge: judge);

        result.IsError.ShouldBeFalse();
        result.Value.JudgeUsed.ShouldBeTrue();
        var signals = InOrder(result.Value);
        signals.Count.ShouldBe(3);
        signals[0].Judge.ShouldBeNull();
        signals[1].Judge!.Score.ShouldBe(0.4m);
        signals[2].Judge.ShouldBeNull();
    }

    /// <summary>
    /// Two rows for one index: the first wins, because a second answer for the same candidate is the model
    /// contradicting itself and the score already recorded is the one the request was made for.
    /// </summary>
    [Fact]
    public async Task DuplicateIndexRows_KeepTheFirstAnswer()
    {
        var judge = new StubJudge(
            Row(index: 0, score: 0.1m, overlap: null),
            Row(index: 0, score: 0.9m, overlap: null));

        var result = await CheckAsync([Candidate(title: "A")], EmptyCorpus, judge: judge);

        InOrder(result.Value).Single().Judge!.Score.ShouldBe(0.1m);
    }

    /// <summary>
    /// A garbled, non-numeric, zero-based or out-of-range overlap index leaves the score in place and drops the
    /// reference: the stored document says <c>null</c> rather than naming an episode the judge never saw.
    /// </summary>
    /// <param name="overlap">The value the model answered with.</param>
    [Theory]
    [InlineData("not-an-id")]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("")]
    public async Task GarbledOrOutOfRangeOverlapIndex_MapsToNull(string overlap)
    {
        var judge = new StubJudge(Row(index: 0, score: 0.2m, overlap: overlap));

        var result = await CheckAsync([Candidate(title: "A")], new NoveltyCorpus([Episode("E", "S")], []), judge: judge);

        var signal = InOrder(result.Value).Single();
        signal.Judge!.Score.ShouldBe(0.2m);
        signal.Judge.MaxOverlapEpisodeId.ShouldBeNull();
    }

    /// <summary>The one valid answer: the 1-based index resolves to the corpus episode at that position.</summary>
    [Fact]
    public async Task ValidOverlapIndex_ResolvesToTheCorpusEpisode()
    {
        var first = Episode("First", "S");
        var second = Episode("Second", "S");
        var judge = new StubJudge(Row(index: 0, score: 0.2m, overlap: "2"));

        var result = await CheckAsync([Candidate(title: "A")], new NoveltyCorpus([first, second], []), judge: judge);

        InOrder(result.Value).Single().Judge!.MaxOverlapEpisodeId.ShouldBe(second.Id);
    }

    /// <summary>
    /// The series' first episode: nothing to compare against, so every lexical score is 0 — and the judge is still
    /// asked, because <c>fit</c> and <c>hook_strength</c> are what ranks the batch.
    /// </summary>
    [Fact]
    public async Task EmptyCorpus_StillAsksTheJudge()
    {
        var judge = new StubJudge(Row(index: 0, score: 0.05m, overlap: null, fit: 0.8m, hook: 0.9m));

        var result = await CheckAsync([Candidate(title: "A")], EmptyCorpus, judge: judge);

        result.Value.JudgeUsed.ShouldBeTrue();
        judge.Requests.Count.ShouldBe(1);
        var signal = InOrder(result.Value).Single();
        signal.LexicalScore.ShouldBe(0m);
        signal.Judge!.Fit.ShouldBe(0.8m);
        signal.Judge.HookStrength.ShouldBe(0.9m);
    }

    /// <summary>
    /// A ceiling of 0 the candidate already sits on: the comparison is strictly greater, so an equal reading is
    /// eligible and the judge is asked. This is the boundary the calibration knob moves.
    /// </summary>
    [Fact]
    public async Task ThresholdEquality_IsStillEligible()
    {
        var judge = new StubJudge(Row(index: 0, score: 0.05m, overlap: null));
        var options = new IdeaNoveltyOptions { LexicalMax = 0m };

        // Empty corpus means lexical 0, which is exactly the ceiling set above.
        var result = await CheckAsync([Candidate(title: "A")], EmptyCorpus, judge: judge, options: options);

        result.Value.JudgeUsed.ShouldBeTrue();
        judge.Requests.Count.ShouldBe(1);
    }

    /// <summary>
    /// Two invalid answers in a row: D7's one retry is spent while the second call is admitted under the judge's
    /// own worst case, and the batch then degrades to the lexical readings instead of failing the run.
    /// </summary>
    /// <param name="first">The first answer's rule ids.</param>
    /// <param name="second">The second answer's rule ids.</param>
    [Theory]
    [InlineData("schema", "schema")]
    [InlineData("schema", "max_output_tokens_exceeded")]
    public async Task InvalidOutputTwice_DegradesToLexicalOnly(string first, string second)
    {
        var judge = new StubJudge(
            TextCallResult<NoveltyV1>.InvalidOutput(Model, null, "req-1", [first]),
            TextCallResult<NoveltyV1>.InvalidOutput(Model, null, "req-2", [second]));
        var guard = new StubBudgetGuard(allowed: true);

        var result = await CheckAsync([Candidate(title: "A")], EmptyCorpus, judge: judge, guard: guard);

        result.IsError.ShouldBeFalse();
        result.Value.JudgeUsed.ShouldBeFalse();
        InOrder(result.Value).Single().Judge.ShouldBeNull();
        judge.Requests.Count.ShouldBe(2);
    }

    /// <summary>A stop at the output cap is final on the first call: the second dispatch would truncate identically.</summary>
    [Fact]
    public async Task TruncatedOutput_DoesNotRetry()
    {
        var judge = new StubJudge(TextCallResult<NoveltyV1>.InvalidOutput(
            Model,
            null,
            "req-1",
            [TextCallInvalidOutput<NoveltyV1>.MaxOutputTokensExceeded]));

        var result = await CheckAsync([Candidate(title: "A")], EmptyCorpus, judge: judge);

        result.Value.JudgeUsed.ShouldBeFalse();
        judge.Requests.Count.ShouldBe(1);
    }

    /// <summary>An unreachable judge (and a 5xx) degrades the same way an unparsable answer does.</summary>
    /// <param name="status">The provider status; 0 means the call never reached the provider.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(503)]
    public async Task UnavailableJudge_DegradesToLexicalOnly(int status)
    {
        var judge = status == 0
            ? new StubJudge(TextCallResult<NoveltyV1>.ProviderUnavailable(Model, "req-1"))
            : new StubJudge(TextCallResult<NoveltyV1>.ProviderError(Model, status, "req-1"));

        var result = await CheckAsync([Candidate(title: "A")], EmptyCorpus, judge: judge);

        result.IsError.ShouldBeFalse();
        result.Value.JudgeUsed.ShouldBeFalse();
        InOrder(result.Value).Single().Judge.ShouldBeNull();
    }

    /// <summary>
    /// The judge's own refusal on the run's paid call: one <c>moderation_events</c> row at <c>stage = ideas</c> (TL
    /// ruling #3, distinct from the pre-check's <c>brief</c>), then the same degrade as any other refusal.
    /// </summary>
    [Fact]
    public async Task ModeratedJudge_WritesRefusalWithStageIdeas_AndDegrades()
    {
        var judge = new StubJudge(TextCallResult<NoveltyV1>.Moderated(Model, null, "req-1", ["violence"]));
        var events = new StubEventWriter();

        var result = await CheckAsync([Candidate(title: "A")], EmptyCorpus, judge: judge, events: events);

        result.Value.JudgeUsed.ShouldBeFalse();
        var write = events.Writes.Single();
        write.Stage.ShouldBe(ModerationStage.Ideas);
        write.Source.ShouldBe(ModerationSource.OpenaiText);
        write.Categories.ShouldBe(["violence"]);
        write.OwnText.ShouldBeNull();
    }

    /// <summary>An account that cannot pay for the judge stops the run: the error is the caller's, not a degrade.</summary>
    [Fact]
    public async Task FundsExhaustedJudge_PropagatesTheRunError()
    {
        var judge = new StubJudge(TextCallResult<NoveltyV1>.ProviderFundsExhausted(Model, "req-1"));

        var result = await CheckAsync([Candidate(title: "A")], EmptyCorpus, judge: judge);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(IdeaErrors.ProviderFundsExhausted.Code);
    }

    /// <summary>
    /// The retry's re-check refuses the platform cap: the error propagates before the second dispatch is bought, so
    /// the judge sees exactly the one call the caller's admission already reserved.
    /// </summary>
    [Fact]
    public async Task SpendCapRefusedBeforeTheRetry_PropagatesWithoutASecondCall()
    {
        var judge = new StubJudge(TextCallResult<NoveltyV1>.InvalidOutput(Model, null, "req-1", ["schema"]));
        var guard = new StubBudgetGuard(allowed: false);

        var result = await CheckAsync([Candidate(title: "A")], EmptyCorpus, judge: judge, guard: guard);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(IdeaErrors.SpendCapReached.Code);
        judge.Requests.Count.ShouldBe(1);
        guard.Checks.Count.ShouldBe(1);
    }

    /// <summary>A price catalog with no usable version fails closed rather than dispatching unbounded.</summary>
    [Fact]
    public async Task UnusablePriceVersion_Propagates()
    {
        var judge = new StubJudge(TextCallResult<NoveltyV1>.InvalidOutput(Model, null, "req-1", ["schema"]));
        var estimator = new StubEstimator(Error.Failure(ErrorCodes.PriceCatalogEmpty, "no version"));

        var result = await CheckAsync([Candidate(title: "A")], EmptyCorpus, judge: judge, estimator: estimator);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.PriceCatalogEmpty);
        judge.Requests.Count.ShouldBe(1);
    }

    /// <summary>
    /// The model's justification is stored, so it is capped and stripped before it is: control characters and the
    /// line/paragraph separators go, and the text ends at 300 characters.
    /// </summary>
    [Fact]
    public async Task ReasonIsSanitisedAndCappedAt300Characters()
    {
        var reason = new string('x', 400) + "\u0007\u2028\u2029";
        var judge = new StubJudge(Row(index: 0, score: 0.2m, overlap: null, reason: reason));

        var result = await CheckAsync([Candidate(title: "A")], EmptyCorpus, judge: judge);

        var stored = InOrder(result.Value).Single().Judge!.Reason;
        stored.Length.ShouldBe(300);
        stored.ShouldNotContain("\u0007");
        stored.ShouldNotContain("\u2028");
    }

    /// <summary>Out-of-range numbers from a provider that ignored its own schema are clamped, never saved as-is.</summary>
    [Fact]
    public async Task ScoresOutsideZeroToOne_AreClamped()
    {
        var judge = new StubJudge(Row(index: 0, score: 1.5m, overlap: null, fit: -0.5m, hook: 2m));

        var result = await CheckAsync([Candidate(title: "A")], EmptyCorpus, judge: judge);

        var signal = InOrder(result.Value).Single().Judge!;
        signal.Score.ShouldBe(1m);
        signal.Fit.ShouldBe(0m);
        signal.HookStrength.ShouldBe(1m);
    }

    /// <summary>The judge sees the batch's candidates in order, the corpus episodes, the topic and the wire language.</summary>
    [Fact]
    public async Task JudgeRequest_CarriesTheBatchTheCorpusAndTheTopic()
    {
        var judge = new StubJudge(Row(index: 0, score: 0.2m, overlap: null), Row(index: 1, score: 0.3m, overlap: null));
        var batch = new[] { Candidate(title: "A"), Candidate(title: "B") };
        var episodes = new[] { Episode("E1", "S1"), Episode("E2", "S2") };

        await CheckAsync(batch, new NoveltyCorpus(episodes, []) , judge: judge, language: Language.Vi, topic: "true_crime");

        var request = judge.Requests.Single();
        request.Ideas.Count.ShouldBe(2);
        request.PastEpisodes.Count.ShouldBe(2);
        request.Language.ShouldBe("vi");
        request.Topic.ShouldBe("true_crime");
        request.Run.Attempt.ShouldBe(1);
    }

    /// <summary>Every candidate of the batch gets a signal, keyed by the id the planner stored the row with.</summary>
    [Fact]
    public async Task EveryCandidateIsKeyedByItsIdeaId()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var judge = new StubJudge(Row(index: 0, score: 0.2m, overlap: null));

        var result = await CheckAsync(
            [Candidate(title: "A"), Candidate(title: "B")],
            EmptyCorpus,
            judge: judge,
            ideaIds: [first, second]);

        result.Value.ByIdeaId.Keys.ShouldBe([first, second], ignoreOrder: true);
    }

    private static Task<ErrorOr<NoveltyBatchResult>> CheckAsync(
        IReadOnlyList<IdeaCandidate> batch,
        NoveltyCorpus corpus,
        StubJudge judge,
        StubBudgetGuard? guard = null,
        StubEstimator? estimator = null,
        StubEventWriter? events = null,
        IdeaNoveltyOptions? options = null,
        Language language = Language.En,
        string topic = "supernatural",
        IReadOnlyList<Guid>? ideaIds = null)
    {
        var checker = new IdeaNoveltyChecker(
            options ?? new IdeaNoveltyOptions(),
            judge,
            guard ?? new StubBudgetGuard(allowed: true),
            estimator ?? new StubEstimator(),
            events ?? new StubEventWriter(),
            new IdeaMetrics(),
            NullLogger<IdeaNoveltyChecker>.Instance);
        // The rows are the caller's (the planner generates them before it reads the corpus), so a test that cares
        // about the keying sets them and every other test gets throwaway ones.
        var ids = ideaIds ?? [.. batch.Select(_ => Guid.NewGuid())];

        return checker
            .CheckAsync(
                [.. batch.Select((candidate, index) => (ids[index], candidate))],
                corpus,
                language,
                topic,
                Run(),
                CancellationToken.None)
            .AsTask();
    }

    private static IReadOnlyList<NoveltySignals> InOrder(NoveltyBatchResult result) =>
        [.. result.ByIdeaId.Values];

    private static NoveltyScoreV1 Row(
        int index,
        decimal score,
        string? overlap,
        decimal fit = 0.5m,
        decimal hook = 0.5m,
        string reason = "closest to the second episode") =>
        new(index, overlap, score, fit, hook, reason);

    private static IdeaCandidate Candidate(
        string title,
        string logline = "A logline.") =>
        new(
            title,
            logline,
            "A hook that opens on the first page.",
            "A setting.",
            StoryStructureTags.Empty,
            []);

    private static CorpusEpisode Episode(string title, string summary) =>
        new(Guid.NewGuid(), title, summary);

    private static RunContext Run() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            new NightTales.Application.Abstractions.Money.ReservationId(Guid.NewGuid()),
            NightTales.Domain.Stories.Generation.GenerationTrigger.User,
            1,
            "1");

    private static NoveltyCorpus EmptyCorpus => new([], []);

    /// <summary>A hand-written judge: the answers are queued, and every request is kept.</summary>
    private sealed class StubJudge(params TextCallResult<NoveltyV1>[] answers) : IIdeaNoveltyJudge
    {
        private readonly Queue<TextCallResult<NoveltyV1>> _answers = new(answers);

        /// <summary>Every request the checker sent, in order.</summary>
        internal List<NoveltyJudgeRequest> Requests { get; } = [];

        /// <inheritdoc />
        public ValueTask<TextCallResult<NoveltyV1>> JudgeAsync(NoveltyJudgeRequest request, CancellationToken ct)
        {
            Requests.Add(request);

            if (_answers.Count == 0)
            {
                throw new InvalidOperationException("The stub judge has no answer left: the checker asked too often.");
            }

            return ValueTask.FromResult(_answers.Dequeue());
        }
    }

    /// <summary>A hand-written spend cap: it records every check and answers what the test set.</summary>
    private sealed class StubBudgetGuard(bool allowed) : IBudgetGuard
    {
        /// <summary>What the checker asked, in order.</summary>
        internal List<(CostBound Bound, Guid? ExcludingRunId)> Checks { get; } = [];

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> CheckAsync(
            CostBound worstCaseUsd,
            CancellationToken ct,
            Guid? excludingRunId = null)
        {
            Checks.Add((worstCaseUsd, excludingRunId));

            return ValueTask.FromResult(allowed
                ? (ErrorOr<Success>)Result.Success
                : Error.Failure(ErrorCodes.SpendCapReached, "The stub guard refuses every dispatch."));
        }
    }

    /// <summary>A hand-written estimator, so the retry's re-check does not depend on the price catalogue.</summary>
    private sealed class StubEstimator(ErrorOr<UsageBound>? novelty = null) : IIdeaCostEstimator
    {
        /// <inheritdoc />
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(string priceVersion, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(0.01m, 100, 100));

        /// <inheritdoc />
        public Task<ErrorOr<UsageBound>> NoveltyWorstCaseAsync(string priceVersion, CancellationToken ct) =>
            Task.FromResult(novelty ?? new UsageBound(0.01m, 100, 400));
    }

    /// <summary>A hand-written evidence writer: nothing reaches a database, every row is inspected.</summary>
    private sealed class StubEventWriter : IModerationEventWriter
    {
        /// <summary>Every row the checker asked for, in order.</summary>
        internal List<(Guid UserId, Guid? EpisodeId, ModerationSource Source, ModerationStage Stage, IReadOnlyList<string> Categories, string? OwnText)> Writes { get; } = [];

        /// <inheritdoc />
        public Task WriteAsync(
            Guid userId,
            Guid? episodeId,
            ModerationSource source,
            ModerationStage stage,
            IReadOnlyList<string> categories,
            string? ownText,
            CancellationToken ct)
        {
            Writes.Add((userId, episodeId, source, stage, categories, ownText));

            return Task.CompletedTask;
        }
    }
}
