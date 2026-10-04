using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Commands.GenerateStory;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Generation;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Stories;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Stories;

/// <summary>
/// <c>GenerateStoryCommandHandler</c> steps 1–12 (02.2.2-T2) against a real <c>AppDbContext</c>: the pipeline's commit
/// is replaced by one <c>SaveChangesAsync</c>, the guards by hand-written fakes. Each test owns a fresh user.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GenerateStoryCommandHandlerTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly decimal[] WorstCaseChecks = [0.5m];

    private static readonly string[] AuditKeys = ["episodeId", "generationRunId", "tier", "trigger", "worstCaseUsd"];

    [Fact]
    public async Task GenerateStoryCommand_FromDraft_InsertsQueuedRunAndMovesEpisodeToGenerating()
    {
        var world = await NewWorldAsync();

        var result = await world.SendAsync(new GenerateStoryCommand(world.EpisodeId, GenerationTrigger.User, "k1"));

        result.IsError.ShouldBeFalse(string.Join(";", result.ErrorsOrEmptyList));
        result.Value.Status.ShouldBe("queued");
        result.Value.Stage.ShouldBe("queued");

        var run = await world.OnlyRunAsync();
        run.Id.ShouldBe(result.Value.GenerationRunId);
        run.Status.ShouldBe(GenerationRunStatus.Queued);
        run.Stage.ShouldBe(GenerationStage.Queued);
        run.Kind.ShouldBe(GenerationKind.Story);
        run.Trigger.ShouldBe(GenerationTrigger.User);
        run.PreviousEpisodeStatus.ShouldBe(EpisodeStatus.Draft);
        run.CountsTowardLimit.ShouldBeTrue();
        run.WorstCaseUsd.ShouldBe(0.5m);
        run.IdempotencyKey.ShouldBe("k1");
        run.ReservationId.ShouldNotBeNull();
        run.QueuedAt.ShouldBe(Now);
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryGenerating);

        world.Ledger.Reservations.Count.ShouldBe(1);
        world.Ledger.Reservations[0].ShouldBe(new ReservationId(run.ReservationId!.Value));
        world.Budget.Checked.ShouldBe(WorstCaseChecks);
        world.Limit.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task GenerateStoryCommand_FromStoryReview_SetsPreviousStatusStoryReview()
    {
        var world = await NewWorldAsync(EpisodeStatus.StoryReview);

        var result = await world.SendAsync(new GenerateStoryCommand(world.EpisodeId, GenerationTrigger.User, "k1"));

        result.IsError.ShouldBeFalse();
        (await world.OnlyRunAsync()).PreviousEpisodeStatus.ShouldBe(EpisodeStatus.StoryReview);
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryGenerating);
    }

    [Theory]
    [InlineData(EpisodeStatus.StoryApproved)]
    [InlineData(EpisodeStatus.Rendering)]
    [InlineData(EpisodeStatus.Published)]
    [InlineData(EpisodeStatus.Cancelled)]
    public async Task GenerateStoryCommand_FromStoryApproved_ReturnsEpisodeInvalidState(EpisodeStatus status)
    {
        var world = await NewWorldAsync(status);

        var result = await world.SendAsync(new GenerateStoryCommand(world.EpisodeId, GenerationTrigger.User, "k1"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.EpisodeInvalidState);
        await world.ShouldHaveNoRunAsync();
        (await world.EpisodeStatusAsync()).ShouldBe(status);
        world.Ledger.Reservations.ShouldBeEmpty();
    }

    [Fact]
    public async Task GenerateStoryCommand_TrueCrimeWithoutApprovedSheet_ReturnsFactSheetRequired()
    {
        var world = await NewWorldAsync(topic: Topic.TrueCrime);

        var result = await world.SendAsync(new GenerateStoryCommand(world.EpisodeId, GenerationTrigger.User, "k1"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.FactSheetRequired);
        await world.ShouldHaveNoRunAsync();
        world.Limit.Calls.ShouldBe(0, "the sheet is checked before the limit (step 4 before step 5)");
    }

    [Fact]
    public async Task GenerateStoryCommand_TrueCrimeWithApprovedSheet_PinsTheSheetOnTheRun()
    {
        var world = await NewWorldAsync(topic: Topic.TrueCrime);
        var sheetId = Guid.CreateVersion7();
        world.FactSheets.ApprovedSheet = new Application.Abstractions.Research.ApprovedFactSheet(
            sheetId,
            1,
            [],
            new Dictionary<string, Guid>());

        var result = await world.SendAsync(new GenerateStoryCommand(world.EpisodeId, GenerationTrigger.User, "k1"));

        result.IsError.ShouldBeFalse();
        (await world.OnlyRunAsync()).InputFactSheetId.ShouldBe(sheetId);
    }

    [Fact]
    public async Task GenerateStoryCommand_ActiveRun_ReturnsStoryGenerationInProgress()
    {
        var world = await NewWorldAsync();
        await world.SeedActiveRunAsync();

        var result = await world.SendAsync(new GenerateStoryCommand(world.EpisodeId, GenerationTrigger.User, "k1"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.StoryGenerationInProgress);
        (await world.RunCountAsync()).ShouldBe(1, "only the seeded run exists");
        world.Ledger.Reservations.ShouldBeEmpty();
    }

    [Fact]
    public async Task GenerateStoryCommand_EpisodeAlreadyGenerating_ReturnsStoryGenerationInProgress()
    {
        var world = await NewWorldAsync(EpisodeStatus.StoryGenerating);

        var result = await world.SendAsync(new GenerateStoryCommand(world.EpisodeId, GenerationTrigger.User, "k1"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.StoryGenerationInProgress);
    }

    [Fact]
    public async Task GenerateStoryCommand_UserTriggerAutopilot_ReturnsForbidden()
    {
        var world = await NewWorldAsync();

        var result = await world.SendAsync(new GenerateStoryCommand(world.EpisodeId, GenerationTrigger.Autopilot, "k1"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.Forbidden);
        await world.ShouldHaveNoRunAsync();
    }

    [Fact]
    public async Task GenerateStoryCommand_AutopilotUnderSystemScope_IsQueuedWithoutTheLimitCheck()
    {
        var world = await NewWorldAsync(actor: ActorType.System);

        var result = await world.SendAsync(new GenerateStoryCommand(world.EpisodeId, GenerationTrigger.Autopilot, "autopilot:s:t:story"));

        result.IsError.ShouldBeFalse(string.Join(";", result.ErrorsOrEmptyList));
        var run = await world.OnlyRunAsync();
        run.Trigger.ShouldBe(GenerationTrigger.Autopilot);
        world.Limit.Calls.ShouldBe(0, "FR-02-7 counts user-triggered runs only (02 D2)");
        world.Budget.Checked.Count.ShouldBe(1, "the spend cap still applies to the autopilot");
    }

    [Fact]
    public async Task GenerateStoryCommand_LimitDenied_ReturnsTheLimitErrorAndInsertsNothing()
    {
        var world = await NewWorldAsync();
        world.Limit.Deny = true;

        var result = await world.SendAsync(new GenerateStoryCommand(world.EpisodeId, GenerationTrigger.User, "k1"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.StoryGenerationLimit);
        await world.ShouldHaveNoRunAsync();
        world.Budget.Checked.ShouldBeEmpty();
    }

    [Fact]
    public async Task GenerateStoryCommand_BudgetDenied_ReturnsSpendCapReached()
    {
        var world = await NewWorldAsync();
        world.Budget.Deny = true;

        var result = await world.SendAsync(new GenerateStoryCommand(world.EpisodeId, GenerationTrigger.User, "k1"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.SpendCapReached);
        await world.ShouldHaveNoRunAsync();
        world.Ledger.Reservations.ShouldBeEmpty("a refused budget check reserves nothing");
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.Draft);
    }

    [Fact]
    public async Task GenerateStoryCommand_AnotherUsersEpisode_ReturnsNotFound()
    {
        var owner = await NewWorldAsync();
        var stranger = await NewWorldAsync();

        var result = await stranger.SendAsync(new GenerateStoryCommand(owner.EpisodeId, GenerationTrigger.User, "k1"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.NotFound);
        await owner.ShouldHaveNoRunAsync();
    }

    [Fact]
    public async Task GenerateStoryCommand_Success_ProjectsAnAuditRowWithIdsAndNumbersOnly()
    {
        var world = await NewWorldAsync();
        var command = new GenerateStoryCommand(world.EpisodeId, GenerationTrigger.User, "k1");

        var result = await world.SendAsync(command);

        var audit = command.ToAuditData();
        audit.ShouldNotBeNull();
        audit.EntityType.ShouldBe("episode");
        audit.EntityId.ShouldBe(world.EpisodeId);
        audit.Summary["generationRunId"].ShouldBe(result.Value.GenerationRunId.ToString());
        audit.Summary["trigger"].ShouldBe("user");
        audit.Summary["tier"].ShouldBe("standard");
        audit.Summary["worstCaseUsd"].ShouldBe(0.5m);
        audit.Summary.Keys.OrderBy(key => key, StringComparer.Ordinal)
            .ShouldBe(AuditKeys);
    }

    [Fact]
    public void GenerateStoryCommand_FailedHandler_ProjectsNoAuditRow() =>
        new GenerateStoryCommand(Guid.CreateVersion7()).ToAuditData().ShouldBeNull();

    private async Task<World> NewWorldAsync(
        EpisodeStatus status = EpisodeStatus.Draft,
        Topic topic = Topic.Ghost,
        ActorType actor = ActorType.User)
    {
        var setup = new TestCurrentUser();
        var user = await new UserBuilder(Database, setup).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, setup).CreateAsync(user.Id, topic: topic, ct: Ct);
        var episode = await new EpisodeBuilder(Database, setup).CreateAsync(user.Id, series.Id, status: status, ct: Ct);

        return new World(Database, user.Id, episode.Id, actor);
    }

    private sealed class World(TestDatabase database, Guid userId, Guid episodeId, ActorType actor)
    {
        private readonly TestCurrentUser _current = new() { Id = userId, Actor = actor };

        public Guid EpisodeId => episodeId;

        public FakeBudgetGuard Budget { get; } = new();

        public FakeFactSheetGuard FactSheets { get; } = new();

        public RecordingLedger Ledger { get; } = new();

        public FakeLimitGuard Limit { get; } = new();

        public async Task<ErrorOr<GenerateStoryAccepted>> SendAsync(GenerateStoryCommand command)
        {
            await using var db = database.CreateAppDbContext(_current, new FakeClock(Now));
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            var handler = new GenerateStoryCommandHandler(
                db,
                _current,
                FactSheets,
                Limit,
                new FixedEstimator(),
                Budget,
                Ledger,
                new FakeClock(Now));

            var result = await handler.Handle(command, Ct);
            if (!result.IsError)
            {
                await db.SaveChangesAsync(Ct);
                await transaction.CommitAsync(Ct);
            }

            return result;
        }

        public async Task<GenerationRun> OnlyRunAsync()
        {
            await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = userId });

            return await db.GenerationRuns.AsNoTracking().SingleAsync(run => run.EpisodeId == episodeId, Ct);
        }

        public async Task<int> RunCountAsync()
        {
            await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = userId });

            return await db.GenerationRuns.CountAsync(run => run.EpisodeId == episodeId, Ct);
        }

        public async Task ShouldHaveNoRunAsync() => (await RunCountAsync()).ShouldBe(0);

        public async Task<EpisodeStatus> EpisodeStatusAsync()
        {
            await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = userId });

            return await db.Episodes.AsNoTracking().Where(episode => episode.Id == episodeId).Select(episode => episode.Status).SingleAsync(Ct);
        }

        public async Task SeedActiveRunAsync()
        {
            await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = userId });
            var seriesId = await db.Episodes.Where(episode => episode.Id == episodeId).Select(episode => episode.SeriesId).SingleAsync(Ct);
            var run = GenerationRun.Queue(
                    userId,
                    seriesId,
                    episodeId,
                    GenerationKind.Story,
                    GenerationTrigger.User,
                    null,
                    EpisodeStatus.Draft,
                    null,
                    null,
                    0m,
                    Now)
                .Value;
            db.GenerationRuns.Add(run);
            await db.SaveChangesAsync(Ct);
        }
    }

    private sealed class FixedEstimator : IStoryCostEstimator
    {
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(0.5m, 1000, 500));

        public Task<ErrorOr<UsageBound>> ReviewWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            WriterWorstCaseAsync(tier, language, ct);

        public Task<ErrorOr<UsageBound>> WriterWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(0.5m, 1000, 500));
    }

    private sealed class RecordingLedger : ICreditLedger
    {
        public List<ReservationId> Reservations { get; } = [];

        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct)
        {
            op.ShouldBe(CreditOperation.StoryGenerate);
            var id = new ReservationId(Guid.CreateVersion7());
            Reservations.Add(id);

            return ValueTask.FromResult<ErrorOr<ReservationId>>(id);
        }

        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);

        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
    }

    private sealed class FakeLimitGuard : IGenerationLimitGuard
    {
        public int Calls { get; private set; }

        public bool Deny { get; set; }

        public ValueTask<ErrorOr<Success>> CheckAsync(Guid episodeId, Guid userId, CancellationToken cancellationToken)
        {
            Calls++;

            return ValueTask.FromResult<ErrorOr<Success>>(
                Deny ? Error.Failure(ErrorCodes.StoryGenerationLimit, "limit") : Result.Success);
        }
    }
}
