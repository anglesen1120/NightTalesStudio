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
/// The fact-sheet precondition at the generate command (02.3.3-T1, 02 README D21/F12): a true-crime
/// episode without an approved sheet is refused with the D8 409 before any spend, and a fiction episode
/// needs no sheet at all.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GenerateStoryCommandFactSheetTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GenerateStoryCommand_TrueCrimeWithoutApprovedSheet_Returns409FactSheetRequired()
    {
        var world = await NewWorldAsync(Topic.TrueCrime);

        var result = await world.SendAsync();

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.FactSheetRequired);
        result.FirstError.Type.ShouldBe(ErrorType.Conflict, "D8 answers fact_sheet_required with 409");
        world.Limit.Calls.ShouldBe(0, "the sheet is checked before the limit (step 4 before step 5)");
        (await world.RunCountAsync()).ShouldBe(0);
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.Draft);
    }

    [Fact]
    public async Task GenerateStoryCommand_Fiction_DoesNotRequireFactSheet()
    {
        var world = await NewWorldAsync(Topic.Ghost);

        var result = await world.SendAsync();

        result.IsError.ShouldBeFalse(string.Join(";", result.ErrorsOrEmptyList));
        result.Value.Status.ShouldBe("queued");
        (await world.RunCountAsync()).ShouldBe(1);
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryGenerating);
    }

    private async Task<World> NewWorldAsync(Topic topic)
    {
        var setup = new TestCurrentUser();
        var user = await new UserBuilder(Database, setup).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, setup).CreateAsync(user.Id, topic: topic, ct: Ct);
        var episode = await new EpisodeBuilder(Database, setup).CreateAsync(user.Id, series.Id, ct: Ct);

        return new World(Database, user.Id, episode.Id);
    }

    private sealed class World(TestDatabase database, Guid userId, Guid episodeId)
    {
        private readonly TestCurrentUser _current = new() { Id = userId };

        public FakeBudgetGuard Budget { get; } = new();

        public FakeFactSheetGuard FactSheets { get; } = new();

        public FakeLimitGuard Limit { get; } = new();

        public async Task<ErrorOr<GenerateStoryAccepted>> SendAsync()
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
                new RecordingLedger(),
                new FakeClock(Now));

            var result = await handler.Handle(new GenerateStoryCommand(episodeId, GenerationTrigger.User, "k1"), Ct);
            if (!result.IsError)
            {
                await db.SaveChangesAsync(Ct);
                await transaction.CommitAsync(Ct);
            }

            return result;
        }

        public async Task<int> RunCountAsync()
        {
            await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = userId });

            return await db.GenerationRuns.CountAsync(run => run.EpisodeId == episodeId, Ct);
        }

        public async Task<EpisodeStatus> EpisodeStatusAsync()
        {
            await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = userId });

            return await db.Episodes.AsNoTracking()
                .Where(episode => episode.Id == episodeId)
                .Select(episode => episode.Status)
                .SingleAsync(Ct);
        }
    }

    private sealed class FakeLimitGuard : IGenerationLimitGuard
    {
        public int Calls { get; private set; }

        public ValueTask<ErrorOr<Success>> CheckAsync(Guid episodeId, Guid userId, CancellationToken cancellationToken)
        {
            Calls++;

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }
    }

    private sealed class RecordingLedger : ICreditLedger
    {
        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<ReservationId>>(new ReservationId(Guid.CreateVersion7()));

        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);

        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
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
}
