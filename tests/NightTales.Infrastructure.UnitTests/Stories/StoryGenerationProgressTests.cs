using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Generation;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Stories;

/// <summary>
/// <c>StoryGenerationProgress</c> (02.2.2-T3) against a real <c>AppDbContext</c>: the CR-29 guards, the failure and
/// cancel transitions with their refunds, the hold, and the kinds that never move an episode.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StoryGenerationProgressTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Progress_TerminalRun_IsRejected()
    {
        var world = await NewWorldAsync(EpisodeStatus.StoryGenerating);
        (await world.Progress.StartAsync(world.RunId, Ct)).IsError.ShouldBeFalse();
        var failure = new StoryFailure(ErrorCodes.StoryGenerationFailed, "x", false, "story_generation_failed");
        (await world.Progress.FailAsync(world.RunId, failure, Ct)).IsError.ShouldBeFalse();

        var late = await world.Progress.RecordStageAsync(world.RunId, GenerationStage.Scoring, Ct);

        late.IsError.ShouldBeTrue();
        late.FirstError.Code.ShouldBe(StoryGenerationProgress.RunTerminalCode);
        (await world.Progress.CompleteAsync(new StoryCompletion(world.RunId, [Guid.CreateVersion7()], Guid.CreateVersion7()), Ct))
            .FirstError.Code.ShouldBe(StoryGenerationProgress.RunTerminalCode);
        (await world.Progress.CancelAsync(world.RunId, "r", null, null, Ct)).FirstError.Code.ShouldBe(StoryGenerationProgress.RunTerminalCode);
        (await world.Progress.FailAsync(world.RunId, failure, Ct)).FirstError.Code.ShouldBe(StoryGenerationProgress.RunTerminalCode);
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.Draft, "the rejected calls did not touch the episode");
        world.Ledger.Refunds.Count.ShouldBe(1, "only the first failure refunded");
    }

    [Fact]
    public async Task Progress_RunThatIsNotTheEpisodesActiveRun_IsRejected()
    {
        // The episode was moved on (admin or sweeper); a late call for the run must not be applied to it.
        var world = await NewWorldAsync(EpisodeStatus.StoryReview);

        var started = await world.Progress.StartAsync(world.RunId, Ct);

        started.IsError.ShouldBeTrue();
        started.FirstError.Code.ShouldBe(StoryGenerationProgress.RunNotActiveCode);
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryReview);
        (await world.RunAsync()).Status.ShouldBe(GenerationRunStatus.Queued);
    }

    [Fact]
    public async Task Progress_Fail_ReturnsEpisodeToPreviousStatus_NotCounted_AndRefunds()
    {
        var world = await NewWorldAsync(EpisodeStatus.StoryGenerating);
        (await world.Progress.StartAsync(world.RunId, Ct)).IsError.ShouldBeFalse();

        var failed = await world.Progress.FailAsync(
            world.RunId,
            new StoryFailure(ErrorCodes.StoryInvalidOutput, "invalid_output_twice", false, "story_invalid_output"),
            Ct);

        failed.IsError.ShouldBeFalse();
        var run = await world.RunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.ErrorCode.ShouldBe(ErrorCodes.StoryInvalidOutput);
        run.CountsTowardLimit.ShouldBeFalse();
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.Draft);
        world.Ledger.Refunds.ShouldBe([("story_invalid_output", world.Reservation)]);
    }

    [Fact]
    public async Task Sweeper_StuckQueuedRun_IsCancelled_WithErrorCode_AndNotCounted()
    {
        var world = await NewWorldAsync(EpisodeStatus.StoryGenerating);

        var cancelled = await world.Progress.CancelAsync(
            world.RunId,
            "stale_run",
            ErrorCodes.StoryGenerationCancelled,
            "stale_run_sweep",
            Ct,
            recovery: true);

        cancelled.IsError.ShouldBeFalse();
        var run = await world.RunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Cancelled);
        run.ErrorCode.ShouldBe(ErrorCodes.StoryGenerationCancelled);
        run.ErrorDetail.ShouldBe("stale_run_sweep");
        run.CountsTowardLimit.ShouldBeFalse();
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.Draft);
        world.Ledger.Refunds.ShouldBe([("stale_run", world.Reservation)]);
    }

    [Fact]
    public async Task Progress_Pause_StaysQueuedWithReason_AndDoesNotRefund()
    {
        var world = await NewWorldAsync(EpisodeStatus.StoryGenerating);
        (await world.Progress.StartAsync(world.RunId, Ct)).IsError.ShouldBeFalse();

        (await world.Progress.PauseAsync(world.RunId, GenerationPauseReason.ProviderFundsExhausted, Ct)).IsError.ShouldBeFalse();

        var run = await world.RunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Queued);
        run.PauseReason.ShouldBe(GenerationPauseReason.ProviderFundsExhausted);
        run.CountsTowardLimit.ShouldBeFalse();
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryGenerating);
        world.Ledger.Refunds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Progress_StageCalls_RepeatedStageEmitsOneEpisodeEvent_ButBothCallsAreListed()
    {
        var world = await NewWorldAsync(EpisodeStatus.StoryGenerating);
        await world.Progress.StartAsync(world.RunId, Ct);

        await world.Progress.RecordStageAsync(world.RunId, GenerationStage.Checking, Ct);
        await world.Progress.RecordStageAsync(world.RunId, GenerationStage.Checking, Ct);

        world.Progress.StageCalls.ShouldBe([GenerationStage.Writing, GenerationStage.Checking, GenerationStage.Checking]);
        (await world.RunAsync()).Stage.ShouldBe(GenerationStage.Checking);
    }

    [Fact]
    public async Task Progress_FailOfNonStoryKind_DoesNotTouchTheEpisode()
    {
        var world = await NewWorldAsync(EpisodeStatus.StoryReview, GenerationKind.FactSheet);

        var failed = await world.Progress.FailAsync(
            world.RunId,
            new StoryFailure(ErrorCodes.FactSheetGenerationFailed, "stale_run_sweep", false, "stale_run"),
            Ct,
            recovery: true);

        failed.IsError.ShouldBeFalse();
        (await world.RunAsync()).Status.ShouldBe(GenerationRunStatus.Failed);
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryReview);
    }

    [Fact]
    public async Task Progress_TwoConcurrentStarts_ExactlyOneWins_TheOtherIsRejectedBeforeAnyPaidWork()
    {
        await using var world = await NewWorldAsync(EpisodeStatus.StoryGenerating);
        var second = world.NewProgress(new RecordingLedger());

        var results = await Task.WhenAll(world.Progress.StartAsync(world.RunId, Ct), second.StartAsync(world.RunId, Ct));

        results.Count(result => !result.IsError).ShouldBe(1, "the claim is one conditional UPDATE: only one job gets the run");
        results.Single(result => result.IsError).FirstError.Code.ShouldBe(StoryGenerationProgress.RunNotActiveCode);
        (await world.RunAsync()).Status.ShouldBe(GenerationRunStatus.Running);
    }

    [Fact]
    public async Task Progress_StartOfARunThatAlreadyRuns_IsRejected_NotTreatedAsIdempotent()
    {
        await using var world = await NewWorldAsync(EpisodeStatus.StoryGenerating);
        (await world.Progress.StartAsync(world.RunId, Ct)).IsError.ShouldBeFalse();
        var second = world.NewProgress(new RecordingLedger());

        var again = await second.StartAsync(world.RunId, Ct);

        again.IsError.ShouldBeTrue();
        again.FirstError.Code.ShouldBe(StoryGenerationProgress.RunNotActiveCode);
        second.StageCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Progress_SweeperFailsRunMidFlight_NextCallsAreRejected_AndNothingIsSettledAfterTheRefund()
    {
        await using var world = await NewWorldAsync(EpisodeStatus.StoryGenerating);
        (await world.Progress.StartAsync(world.RunId, Ct)).IsError.ShouldBeFalse();
        var sweeperLedger = new RecordingLedger();
        var sweeper = world.NewProgress(sweeperLedger);

        var swept = await sweeper.FailAsync(
            world.RunId,
            new StoryFailure(ErrorCodes.StoryGenerationFailed, "stale_run_sweep", false, "stale_run"),
            Ct,
            recovery: true);
        swept.IsError.ShouldBeFalse();

        // The job's context still tracks the run as running; the guard reads the committed state.
        (await world.Progress.RecordStageAsync(world.RunId, GenerationStage.Scoring, Ct)).FirstError.Code
            .ShouldBe(StoryGenerationProgress.RunTerminalCode);
        (await world.Progress.RecordAttemptAsync(world.RunId, Ct)).FirstError.Code.ShouldBe(StoryGenerationProgress.RunTerminalCode);
        (await world.Progress.CompleteAsync(new StoryCompletion(world.RunId, [Guid.CreateVersion7()], Guid.CreateVersion7()), Ct))
            .FirstError.Code.ShouldBe(StoryGenerationProgress.RunTerminalCode);

        var run = await world.RunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.ErrorDetail.ShouldBe("stale_run_sweep");
        world.Ledger.Settles.ShouldBeEmpty("the late job never settles after the sweeper's refund");
        world.Ledger.Refunds.ShouldBeEmpty();
        sweeperLedger.Refunds.ShouldBe([("stale_run", world.Reservation)]);
    }

    [Fact]
    public async Task Progress_TerminalTransition_NeverOverwritesAnAlreadyEndedRun()
    {
        await using var world = await NewWorldAsync(EpisodeStatus.StoryGenerating);
        (await world.Progress.StartAsync(world.RunId, Ct)).IsError.ShouldBeFalse();
        var other = world.NewProgress(new RecordingLedger());
        (await other.CancelAsync(world.RunId, "stale_run", ErrorCodes.StoryGenerationCancelled, "stale_run_sweep", Ct, recovery: true))
            .IsError.ShouldBeFalse();

        var late = await world.Progress.FailAsync(
            world.RunId,
            new StoryFailure(ErrorCodes.StoryGenerationFailed, "deadline_exceeded", false, "story_generation_failed"),
            Ct);

        late.FirstError.Code.ShouldBe(StoryGenerationProgress.RunTerminalCode);
        var run = await world.RunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Cancelled);
        run.ErrorCode.ShouldBe(ErrorCodes.StoryGenerationCancelled);
        world.Ledger.Refunds.ShouldBeEmpty("a run ended by another actor is not refunded twice");
    }

    [Fact]
    public async Task Progress_LedgerCallAfterTerminalCommit_RunsOnItsOwnToken_NotTheCallersCancelledOne()
    {
        await using var world = await NewWorldAsync(EpisodeStatus.StoryGenerating);
        (await world.Progress.StartAsync(world.RunId, Ct)).IsError.ShouldBeFalse();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var ledger = new RecordingLedger { OnRefund = caller.Cancel };
        var progress = world.NewProgress(ledger);

        var failed = await progress.FailAsync(
            world.RunId,
            new StoryFailure(ErrorCodes.StoryGenerationFailed, "deadline_exceeded", false, "story_generation_failed"),
            caller.Token);

        failed.IsError.ShouldBeFalse();
        caller.IsCancellationRequested.ShouldBeTrue("the callback cancelled the caller's token during the ledger call");
        ledger.RefundTokenCancelled.ShouldBe([false], "the refund token is the ledger's own 10 s budget");
        ledger.Refunds.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Progress_LedgerThrowingCancellation_AfterTheCommit_IsLoggedAndTheRunStaysFailed()
    {
        await using var world = await NewWorldAsync(EpisodeStatus.StoryGenerating);
        (await world.Progress.StartAsync(world.RunId, Ct)).IsError.ShouldBeFalse();
        var progress = world.NewProgress(new RecordingLedger { ThrowOnRefund = true });

        var failed = await progress.FailAsync(
            world.RunId,
            new StoryFailure(ErrorCodes.StoryGenerationFailed, "deadline_exceeded", false, "story_generation_failed"),
            Ct);

        failed.IsError.ShouldBeFalse();
        (await world.RunAsync()).Status.ShouldBe(GenerationRunStatus.Failed);
    }

    [Fact]
    public async Task Progress_SweeperCancelOfAQueuedRun_ThatAWorkerStartedSinceTheRead_IsSkipped_NothingRefundedOrMoved()
    {
        await using var world = await NewWorldAsync(EpisodeStatus.StoryGenerating);
        var sweeperLedger = new RecordingLedger();
        var sweeper = world.NewProgress(sweeperLedger);

        // The sweeper read the run as queued; the worker's start commits before the sweeper's claim.
        (await world.Progress.StartAsync(world.RunId, Ct)).IsError.ShouldBeFalse();

        var cancelled = await sweeper.CancelAsync(
            world.RunId,
            "stale_run",
            ErrorCodes.StoryGenerationCancelled,
            "stale_run_sweep",
            Ct,
            recovery: true,
            stale: new StaleClaim(GenerationRunStatus.Queued, Now.AddHours(1)));

        cancelled.IsError.ShouldBeTrue();
        cancelled.FirstError.Code.ShouldBe(StoryGenerationProgress.RunNotActiveCode);
        (await world.RunAsync()).Status.ShouldBe(GenerationRunStatus.Running);
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryGenerating, "the episode is not returned while the run runs");
        sweeperLedger.Refunds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Progress_SweeperFailOfARunningRun_WhoseStartWasRefreshedSinceTheRead_IsSkipped_NothingRefundedOrMoved()
    {
        await using var world = await NewWorldAsync(EpisodeStatus.StoryGenerating);
        (await world.Progress.StartAsync(world.RunId, Ct)).IsError.ShouldBeFalse();
        var sweeperLedger = new RecordingLedger();
        var sweeper = world.NewProgress(sweeperLedger);
        var failure = new StoryFailure(ErrorCodes.StoryGenerationFailed, "stale_run_sweep", false, "stale_run");

        // started_at is Now: it is no longer before the cutoff the sweeper computed from its stale read.
        var skipped = await sweeper.FailAsync(
            world.RunId,
            failure,
            Ct,
            recovery: true,
            stale: new StaleClaim(GenerationRunStatus.Running, Now.AddMinutes(-1)));

        skipped.IsError.ShouldBeTrue();
        skipped.FirstError.Code.ShouldBe(StoryGenerationProgress.RunNotActiveCode);
        (await world.RunAsync()).Status.ShouldBe(GenerationRunStatus.Running);
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryGenerating);
        sweeperLedger.Refunds.ShouldBeEmpty();

        // Control: the same claim with a cutoff past started_at does end the run.
        var ended = await sweeper.FailAsync(
            world.RunId,
            failure,
            Ct,
            recovery: true,
            stale: new StaleClaim(GenerationRunStatus.Running, Now.AddMinutes(1)));

        ended.IsError.ShouldBeFalse();
        (await world.RunAsync()).Status.ShouldBe(GenerationRunStatus.Failed);
        sweeperLedger.Refunds.ShouldBe([("stale_run", world.Reservation)]);
    }

    private async Task<World> NewWorldAsync(EpisodeStatus episodeStatus, GenerationKind kind = GenerationKind.Story)
    {
        var setup = new TestCurrentUser();
        var user = await new UserBuilder(Database, setup).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, setup).CreateAsync(user.Id, topic: Topic.Ghost, ct: Ct);
        var episode = await new EpisodeBuilder(Database, setup).CreateAsync(user.Id, series.Id, status: episodeStatus, ct: Ct);
        var reservation = Guid.CreateVersion7();

        var current = new TestCurrentUser { Id = user.Id };
        await using (var seed = Database.CreateAppDbContext(current, new FakeClock(Now)))
        {
            var run = GenerationRun.Queue(
                    user.Id,
                    series.Id,
                    episode.Id,
                    kind,
                    GenerationTrigger.User,
                    null,
                    kind == GenerationKind.Story ? EpisodeStatus.Draft : null,
                    null,
                    reservation,
                    0.5m,
                    Now)
                .Value;
            seed.GenerationRuns.Add(run);
            await seed.SaveChangesAsync(Ct);

            return new World(Database, current, run.Id, episode.Id, reservation, user.Id);
        }
    }

    private sealed class World : IAsyncDisposable
    {
        private readonly AppDbContextLease _lease;
        private readonly TestDatabase _database;
        private readonly TestCurrentUser _current;
        private readonly List<AppDbContextLease> _extra = [];

        public World(TestDatabase database, TestCurrentUser current, Guid runId, Guid episodeId, Guid reservation, Guid userId)
        {
            _database = database;
            _current = current;
            RunId = runId;
            EpisodeId = episodeId;
            Reservation = new ReservationId(reservation);
            UserId = userId;
            Ledger = new RecordingLedger();
            _lease = new AppDbContextLease(database.CreateAppDbContext(current, new FakeClock(Now)));
            Progress = new StoryGenerationProgress(
                _lease.Db,
                Ledger,
                new FakeClock(Now),
                new StoryGenerationMetrics(),
                NullLogger<StoryGenerationProgress>.Instance);
        }

        public Guid RunId { get; }

        public Guid EpisodeId { get; }

        public Guid UserId { get; }

        public ReservationId Reservation { get; }

        public RecordingLedger Ledger { get; }

        public StoryGenerationProgress Progress { get; }

        public StoryGenerationProgress NewProgress(ICreditLedger ledger)
        {
            var lease = new AppDbContextLease(_database.CreateAppDbContext(_current, new FakeClock(Now)));
            _extra.Add(lease);

            return new StoryGenerationProgress(lease.Db, ledger, new FakeClock(Now), new StoryGenerationMetrics(), NullLogger<StoryGenerationProgress>.Instance);
        }

        public async Task<GenerationRun> RunAsync()
        {
            await using var db = _database.CreateAppDbContext(new TestCurrentUser { Id = UserId });

            return await db.GenerationRuns.AsNoTracking().SingleAsync(run => run.Id == RunId, Ct);
        }

        public async Task<EpisodeStatus> EpisodeStatusAsync()
        {
            await using var db = _database.CreateAppDbContext(new TestCurrentUser { Id = UserId });

            return await db.Episodes.AsNoTracking().Where(episode => episode.Id == EpisodeId).Select(episode => episode.Status).SingleAsync(Ct);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var extra in _extra)
            {
                await extra.DisposeAsync();
            }

            await _lease.DisposeAsync();
        }
    }

    private sealed class AppDbContextLease(NightTales.Application.Abstractions.Persistence.IAppDbContext context) : IAsyncDisposable
    {
        public NightTales.Application.Abstractions.Persistence.IAppDbContext Db { get; } = context;

        public ValueTask DisposeAsync() => Db is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
    }

    private sealed class RecordingLedger : ICreditLedger
    {
        public List<(string Reason, ReservationId Reservation)> Refunds { get; } = [];

        public List<ReservationId> Settles { get; } = [];

        public List<bool> RefundTokenCancelled { get; } = [];

        public Action? OnRefund { get; init; }

        public bool ThrowOnRefund { get; init; }

        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(Guid userId, CreditOperation op, Guid referenceId, UsageBound bound, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct)
        {
            Settles.Add(reservationId);

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }

        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct)
        {
            OnRefund?.Invoke();
            RefundTokenCancelled.Add(ct.IsCancellationRequested);

            if (ThrowOnRefund)
            {
                throw new OperationCanceledException(ct);
            }

            Refunds.Add((reason, reservationId));

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }
    }
}
