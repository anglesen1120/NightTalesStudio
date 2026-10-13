using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Similarity;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Application.Features.Stories.Jobs;
using NightTales.Application.Features.Stories.Quality;
using NightTales.Application.Features.Stories.Review;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Common;
using NightTales.Domain.Compliance;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Quality;
using NightTales.Domain.Stories.Review;
using NightTales.Domain.Stories.Similarity;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Queries;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Moderation;
using NightTales.TestKit.Stories;
using NightTales.TestKit.Storage;
using NightTales.TestKit.Time;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Stories;

/// <summary>
/// The 02.6.2-T1 revision harness: a real <see cref="AppDbContext"/> over the class database and hand-written fakes
/// for the writer, the moderator, the guards and the ledger. It is separate from the 02.2.2-T3 harness inside
/// <c>StoryGenerationPipelineTests</c> (which those tests own) because it also scripts the revision stage.
/// </summary>
internal sealed class RevisionWorld
{
    /// <summary>The frozen clock of every harness instance.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _database;
    private readonly StoryGenerationPipeline _pipeline;

    private RevisionWorld(
        TestDatabase database,
        StoryGenerationPipeline pipeline,
        TestCurrentUser current,
        Guid runId,
        Guid episodeId,
        ReservationId reservation,
        ScriptedRevisionWriter writer,
        FakeContentModerator moderator,
        RecordingRevisionEvents events,
        RecordingLedger ledger,
        StoryGenerationProgress progress,
        StoryWriteRequestFactory factory,
        CountingCorpusProvider corpus)
    {
        _database = database;
        _pipeline = pipeline;
        Current = current;
        RunId = runId;
        EpisodeId = episodeId;
        Reservation = reservation;
        Writer = writer;
        Moderator = moderator;
        Events = events;
        Ledger = ledger;
        Progress = progress;
        Factory = factory;
        Corpus = corpus;
    }

    public TestCurrentUser Current { get; }

    public Guid RunId { get; }

    public Guid EpisodeId { get; }

    public ReservationId Reservation { get; }

    public ScriptedRevisionWriter Writer { get; }

    public FakeContentModerator Moderator { get; }

    public RecordingRevisionEvents Events { get; }

    public RecordingLedger Ledger { get; }

    public StoryGenerationProgress Progress { get; }

    public StoryWriteRequestFactory Factory { get; }

    /// <summary>The similarity corpus port, counting its reads (A4: one read per run).</summary>
    public CountingCorpusProvider Corpus { get; }

    public static async Task<RevisionWorld> CreateAsync(
        TestDatabase database,
        CancellationToken ct,
        bool reviewEnabled = true,
        bool revisionEnabled = true,
        int maxRevisions = 1,
        double minTextSimilarity = 0.6,
        IBudgetGuard? budget = null,
        Topic topic = Topic.Ghost,
        ApprovedFactSheet? approvedSheet = null,
        IStoryCostEstimator? estimator = null,
        StoryJobOptions? jobOptions = null)
    {
        var setup = new TestCurrentUser();
        var user = await new UserBuilder(database, setup).CreateAsync(ct: ct);
        var series = await new SeriesBuilder(database, setup).CreateAsync(user.Id, topic: topic, ct: ct);
        var episode = await new EpisodeBuilder(database, setup)
            .CreateAsync(user.Id, series.Id, status: EpisodeStatus.StoryGenerating, ct: ct);

        var clock = new FakeClock(Now);
        var ledger = new RecordingLedger();
        var writer = new ScriptedRevisionWriter();
        var events = new RecordingRevisionEvents();
        var current = new TestCurrentUser { Id = user.Id };
        var reservation = Guid.CreateVersion7();

        Guid runId;
        await using (var seed = database.CreateAppDbContext(current, clock))
        {
            var run = GenerationRun.Queue(
                    user.Id,
                    series.Id,
                    episode.Id,
                    GenerationKind.Story,
                    GenerationTrigger.User,
                    null,
                    EpisodeStatus.Draft,
                    approvedSheet?.FactSheetId,
                    reservation,
                    0.5m,
                    Now)
                .Value;
            seed.GenerationRuns.Add(run);
            seed.UsageRecords.Add(UsageRecord.Record(
                user.Id,
                UsageOperation.Story,
                "openai",
                0.25m,
                Now,
                episodeId: episode.Id,
                seriesId: series.Id,
                generationRunId: run.Id,
                reservationId: reservation));
            await seed.SaveChangesAsync(ct);
            runId = run.Id;
        }

        var db = database.CreateAppDbContext(current, clock);
        var progress = new StoryGenerationProgress(
            db,
            ledger,
            clock,
            new StoryGenerationMetrics(),
            NullLogger<StoryGenerationProgress>.Instance);
        var factory = new StoryWriteRequestFactory(new FixedRevisionCatalog());
        var moderator = new FakeContentModerator();
        var corpus = new CountingCorpusProvider(
            new SimilarityCorpusProvider(db, new FakeExtractedTextStore(), Options.Create(new SimilaritySettings())));

        var pipeline = new StoryGenerationPipeline(
            db: db,
            progress: progress,
            requests: factory,
            factSheetGuard: new FakeFactSheetGuard { ApprovedSheet = approvedSheet },
            moderator: moderator,
            moderationEvents: events,
            writer: writer,
            assembler: new StubRevisionAssembler(),
            budgetGuard: budget ?? new FakeBudgetGuard(),
            estimator: estimator ?? new FixedRevisionEstimator(),
            checker: new StoryQualityChecker(),
            jobOptions: jobOptions ?? new StoryJobOptions(),
            reviewOptions: new StoryReviewOptions { Enabled = reviewEnabled },
            revisionOptions: new StoryRevisionOptions
            {
                Enabled = revisionEnabled,
                MaxRevisions = maxRevisions,
                MinTextSimilarity = minTextSimilarity,
            },
            similarityCorpus: corpus,
            similaritySettings: new SimilaritySettings(),
            metrics: new StoryGenerationMetrics(),
            clock: clock,
            logger: NullLogger<StoryGenerationPipeline>.Instance);

        return new RevisionWorld(
            database,
            pipeline,
            current,
            runId,
            episode.Id,
            new ReservationId(reservation),
            writer,
            moderator,
            events,
            ledger,
            progress,
            factory,
            corpus);
    }

    public Task RunAsync() => _pipeline.RunAsync(RunId, Ct);

    public Task RunAsync(CancellationToken token) => _pipeline.RunAsync(RunId, token);

    public async Task<GenerationRun> ReadRunAsync()
    {
        await using var db = _database.CreateAppDbContext(new TestCurrentUser { Id = Current.Id });

        return await db.GenerationRuns.AsNoTracking().SingleAsync(run => run.Id == RunId, Ct);
    }

    public async Task<EpisodeStatus> EpisodeStatusAsync()
    {
        await using var db = _database.CreateAppDbContext(new TestCurrentUser { Id = Current.Id });

        return await db.Episodes.AsNoTracking()
            .Where(episode => episode.Id == EpisodeId)
            .Select(episode => episode.Status)
            .SingleAsync(Ct);
    }

    /// <summary>Every <c>story_versions</c> row of the episode, oldest first.</summary>
    public async Task<IReadOnlyList<StoryVersion>> VersionsAsync()
    {
        await using var db = _database.CreateAppDbContext(new TestCurrentUser { Id = Current.Id });

        return await db.StoryVersions.AsNoTracking()
            .Where(version => version.EpisodeId == EpisodeId)
            .OrderBy(version => version.Version)
            .ToListAsync(Ct);
    }

    public async Task<int> VersionCountAsync()
    {
        await using var db = _database.CreateAppDbContext(new TestCurrentUser { Id = Current.Id });

        return await db.StoryVersions.AsNoTracking().CountAsync(version => version.EpisodeId == EpisodeId, Ct);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}

/// <summary>A writer whose write, review and revision outcomes are queued per stage; a call with nothing queued fails loudly.</summary>
internal sealed class ScriptedRevisionWriter : IStoryWriter
{
    private readonly Queue<TextCallResult<StoryContentV1>> _writes = [];
    private readonly Queue<TextCallResult<StoryReviewV1>> _reviews = [];
    private readonly Queue<Func<CancellationToken, ValueTask<TextCallResult<StoryContentV1>>>> _revisions = [];

    public void EnqueueWrite(TextCallResult<StoryContentV1> result) => _writes.Enqueue(result);

    public void EnqueueReview(TextCallResult<StoryReviewV1> result) => _reviews.Enqueue(result);

    public void EnqueueRevision(TextCallResult<StoryContentV1> result) =>
        _revisions.Enqueue(_ => ValueTask.FromResult(result));

    /// <summary>Queues a revision that cancels <paramref name="cancellation"/> and then blocks on the caller's token.</summary>
    public void EnqueueRevisionCancelling(CancellationTokenSource cancellation) =>
        _revisions.Enqueue(async ct =>
        {
            await cancellation.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);

            throw new InvalidOperationException("unreachable");
        });

    /// <summary>Queues a revision that never returns before its token is cancelled (A2's revision window).</summary>
    public void EnqueueRevisionBlocking() =>
        _revisions.Enqueue(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);

            throw new InvalidOperationException("unreachable");
        });

    /// <summary>Queues a revision that takes <paramref name="delay"/> before returning (A2's second milestone).</summary>
    /// <param name="delay">The delay the scripted call spends before it answers.</param>
    /// <param name="result">The outcome it answers with.</param>
    public void EnqueueRevisionDelayed(TimeSpan delay, TextCallResult<StoryContentV1> result) =>
        _revisions.Enqueue(async ct =>
        {
            await Task.Delay(delay, ct);

            return result;
        });

    public List<StoryWriteRequest> WriteRequests { get; } = [];

    public List<StoryReviewRequest> ReviewRequests { get; } = [];

    public List<StoryRevisionRequest> RevisionRequests { get; } = [];

    public ValueTask<TextCallResult<StoryContentV1>> WriteAsync(StoryWriteRequest req, CancellationToken ct)
    {
        WriteRequests.Add(req);

        return _writes.Count == 0
            ? throw new InvalidOperationException("The writer script is empty.")
            : ValueTask.FromResult(_writes.Dequeue());
    }

    public ValueTask<TextCallResult<StoryReviewV1>> ReviewAsync(StoryReviewRequest req, CancellationToken ct)
    {
        ReviewRequests.Add(req);

        return _reviews.Count == 0
            ? throw new InvalidOperationException("The review script is empty.")
            : ValueTask.FromResult(_reviews.Dequeue());
    }

    public ValueTask<TextCallResult<StoryContentV1>> ReviseAsync(StoryRevisionRequest req, CancellationToken ct)
    {
        RevisionRequests.Add(req);

        return _revisions.Count == 0
            ? throw new InvalidOperationException("The revision script is empty.")
            : _revisions.Dequeue()(ct);
    }
}

/// <summary>
/// Counts the corpus reads of a run (A4): the pipeline loads the episode's corpus once and scores both saved rows
/// against that one read.
/// </summary>
internal sealed class CountingCorpusProvider(ISimilarityCorpusProvider inner) : ISimilarityCorpusProvider
{
    /// <summary>The number of attempted loads.</summary>
    public int Loads { get; private set; }

    public async ValueTask<IReadOnlyList<CorpusText>> LoadAsync(Guid episodeId, Language lang, CancellationToken ct)
    {
        Loads++;

        return await inner.LoadAsync(episodeId, lang, ct).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<CorpusText>> GetPastEpisodesAsync(Guid seriesId, Guid excludeEpisodeId, int max, CancellationToken ct) =>
        inner.GetPastEpisodesAsync(seriesId, excludeEpisodeId, max, ct);
}

/// <summary>Records the moderation events the pipeline writes, without a database.</summary>
internal sealed class RecordingRevisionEvents : IModerationEventWriter
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

/// <summary>Records settlements and refunds; never rejects.</summary>
internal sealed class RecordingLedger : ICreditLedger
{
    public List<(string Reason, ReservationId Reservation)> Refunds { get; } = [];

    public List<ReservationId> Settled { get; } = [];

    public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
        Guid userId,
        CreditOperation op,
        Guid referenceId,
        UsageBound bound,
        CancellationToken ct) => throw new NotSupportedException();

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

/// <summary>An assembler that names each step's prompt version; the data object is irrelevant to these tests.</summary>
internal sealed class StubRevisionAssembler : IPromptAssembler
{
    public AssembledPrompt Assemble(StoryWriteRequest req, AssemblerContext ctx) => new("system", "{}", "pv-writer", "cache");

    public ErrorOr<AssembledPrompt> Review(StoryReviewRequest req) => new AssembledPrompt("system", "{}", "pv-review", "cache");

    public ErrorOr<AssembledPrompt> Revise(StoryRevisionRequest req) => new AssembledPrompt("system", "{}", "pv-revise", "cache");
}

/// <summary>A price catalog with one active version, enough for the request factory.</summary>
internal sealed class FixedRevisionCatalog : IPriceCatalog
{
    public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct) =>
        Task.FromResult<ErrorOr<PriceVersion>>(Version());

    public Task<PriceVersion> GetAsync(int requested, CancellationToken ct) => Task.FromResult(Version());

    private static PriceVersion Version() => PriceVersion.Create(
        1,
        RevisionWorld.Now,
        "{}",
        PriceVersionJson.WriteLimits(PriceLimits.D4Defaults),
        "fake",
        RevisionWorld.Now);
}

/// <summary>Returns a fixed bound for every step, so the budget guard's own script drives the outcome.</summary>
internal sealed class FixedRevisionEstimator : IStoryCostEstimator
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

/// <summary>
/// The estimator whose optional-step bounds can fail on demand: the writer's bound and review #1's bound always
/// succeed, while the revision bound or review #2's bound returns a typed error (the D3 degradation probes — the run
/// must not fail when the estimator cannot produce a bound for an optional step).
/// </summary>
internal sealed class DegradingRevisionEstimator(bool failRevisionBound = false, bool failReviewAfterBound = false) : IStoryCostEstimator
{
    private int _reviewCalls;

    public Task<ErrorOr<UsageBound>> WorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) => Bound();

    public Task<ErrorOr<UsageBound>> WriterWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) => Bound();

    public Task<ErrorOr<UsageBound>> RevisionWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
        failRevisionBound ? Failed() : Bound();

    /// <summary>Review #1's bound must succeed; only review #2's bound (the second call) may fail.</summary>
    public Task<ErrorOr<UsageBound>> ReviewWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
        failReviewAfterBound && ++_reviewCalls > 1 ? Failed() : Bound();

    private static Task<ErrorOr<UsageBound>> Bound() =>
        Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(0.5m, 1000, 500));

    private static Task<ErrorOr<UsageBound>> Failed() =>
        Task.FromResult<ErrorOr<UsageBound>>(Error.Failure(ErrorCodes.PriceCatalogEmpty, "no bound"));
}
