using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research.Commands.GenerateFactSheet;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.Persistence.UnitOfWork;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Research;
using NightTales.TestKit.Stories;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research;

/// <summary>
/// <c>GenerateFactSheetCommandHandler</c> (02.5.3-T2, plan §4.1 U4.1) against a real <c>AppDbContext</c>: the
/// pipeline's commit is replaced by one <c>SaveChangesAsync</c>, the guards by hand-written fakes. Each test
/// owns a fresh user.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GenerateFactSheetCommandHandlerTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly decimal[] WorstCaseChecks = [0.5m];

    private static readonly string[] AuditKeys = ["episodeId", "generationRunId", "trigger", "worstCaseUsd"];

    [Fact]
    public async Task GenerateFactSheetCommand_FetchedNote_InsertsQueuedRunAndReservesCredit()
    {
        var world = await NewWorldAsync(sources: Sources.FetchedNote);

        var result = await world.SendAsync(new GenerateFactSheetCommand(world.EpisodeId, "k1"));

        result.IsError.ShouldBeFalse(string.Join(";", result.ErrorsOrEmptyList));
        result.Value.Status.ShouldBe("queued");
        result.Value.FactSheetId.ShouldBeNull();

        var run = await world.OnlyRunAsync();
        run.Id.ShouldBe(result.Value.RunId);
        run.Status.ShouldBe(GenerationRunStatus.Queued);
        run.Stage.ShouldBe(GenerationStage.Queued);
        run.Kind.ShouldBe(GenerationKind.FactSheet);
        run.Trigger.ShouldBe(GenerationTrigger.User);
        run.PreviousEpisodeStatus.ShouldBe(EpisodeStatus.Draft);
        run.CountsTowardLimit.ShouldBeFalse("D2 counts `kind = story` only");
        run.WorstCaseUsd.ShouldBe(0.5m);
        run.IdempotencyKey.ShouldBe("k1");
        run.ReservationId.ShouldNotBeNull();
        run.QueuedAt.ShouldBe(Now);

        // The episode has no fact-sheet state of its own: the run carries it, the episode does not move.
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.Draft);

        world.Ledger.Operations.ShouldBe([CreditOperation.FactSheet]);
        world.Ledger.References.ShouldBe([world.EpisodeId]);
        world.Budget.Checked.ShouldBe(WorstCaseChecks);
    }

    [Fact]
    public async Task GenerateFactSheetCommand_FromStoryReview_KeepsTheEpisodeStatus()
    {
        var world = await NewWorldAsync(EpisodeStatus.StoryReview, sources: Sources.FetchedNote);

        var result = await world.SendAsync(new GenerateFactSheetCommand(world.EpisodeId, "k1"));

        result.IsError.ShouldBeFalse(string.Join(";", result.ErrorsOrEmptyList));
        (await world.OnlyRunAsync()).PreviousEpisodeStatus.ShouldBe(EpisodeStatus.StoryReview);
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.StoryReview);
    }

    [Theory]
    [InlineData(EpisodeStatus.StoryGenerating)]
    [InlineData(EpisodeStatus.StoryApproved)]
    [InlineData(EpisodeStatus.Rendering)]
    [InlineData(EpisodeStatus.Published)]
    [InlineData(EpisodeStatus.Cancelled)]
    public async Task GenerateFactSheetCommand_WrongStatus_ReturnsEpisodeInvalidState(EpisodeStatus status)
    {
        var world = await NewWorldAsync(status, sources: Sources.FetchedNote);

        var result = await world.SendAsync(new GenerateFactSheetCommand(world.EpisodeId, "k1"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.EpisodeInvalidState);
        await world.ShouldHaveNoRunAsync();
        (await world.EpisodeStatusAsync()).ShouldBe(status);
        world.Ledger.Reservations.ShouldBeEmpty();
    }

    [Fact]
    public async Task GenerateFactSheetCommand_GhostTopic_ReturnsEpisodeInvalidState()
    {
        var world = await NewWorldAsync(topic: Topic.Ghost, sources: Sources.FetchedNote);

        var result = await world.SendAsync(new GenerateFactSheetCommand(world.EpisodeId, "k1"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.EpisodeInvalidState);
        await world.ShouldHaveNoRunAsync();
        world.Budget.Checked.ShouldBeEmpty("the topic is checked before the cost is estimated");
    }

    [Fact]
    public async Task GenerateFactSheetCommand_NoSource_ReturnsSourcesInsufficient()
    {
        var world = await NewWorldAsync(sources: Sources.None);

        var result = await world.SendAsync(new GenerateFactSheetCommand(world.EpisodeId, "k1"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.FactSheetSourcesInsufficient);
        await world.ShouldHaveNoRunAsync();
        world.Budget.Checked.ShouldBeEmpty("the source gate runs before the spend cap");
        world.Ledger.Reservations.ShouldBeEmpty();
    }

    [Fact]
    public async Task GenerateFactSheetCommand_OnlyFailedUrlSource_ReturnsSourcesInsufficient()
    {
        var world = await NewWorldAsync(sources: Sources.FailedUrl);

        var result = await world.SendAsync(new GenerateFactSheetCommand(world.EpisodeId, "k1"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.FactSheetSourcesInsufficient);
        await world.ShouldHaveNoRunAsync();
    }

    [Fact]
    public async Task GenerateFactSheetCommand_ActiveFactSheetRun_ReturnsInProgress()
    {
        var world = await NewWorldAsync(sources: Sources.FetchedNote);
        await world.SeedActiveRunAsync(GenerationKind.FactSheet);

        var result = await world.SendAsync(new GenerateFactSheetCommand(world.EpisodeId, "k1"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.FactSheetGenerationInProgress);
        (await world.RunCountAsync()).ShouldBe(1, "only the seeded run exists");
        world.Ledger.Reservations.ShouldBeEmpty();
        world.Budget.Checked.ShouldBeEmpty("the active-run gate runs before the spend cap");
    }

    [Fact]
    public async Task GenerateFactSheetCommand_BudgetDenied_ReturnsSpendCapReached()
    {
        var world = await NewWorldAsync(sources: Sources.FetchedNote);
        world.Budget.Deny = true;

        var result = await world.SendAsync(new GenerateFactSheetCommand(world.EpisodeId, "k1"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.SpendCapReached);
        await world.ShouldHaveNoRunAsync();
        world.Ledger.Reservations.ShouldBeEmpty("a refused budget check reserves nothing");
        (await world.EpisodeStatusAsync()).ShouldBe(EpisodeStatus.Draft);
    }

    [Fact]
    public async Task GenerateFactSheetCommand_AnotherUsersEpisode_ReturnsNotFound()
    {
        var owner = await NewWorldAsync(sources: Sources.FetchedNote);
        var stranger = await NewWorldAsync(sources: Sources.FetchedNote);

        var result = await stranger.SendAsync(new GenerateFactSheetCommand(owner.EpisodeId, "k1"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.NotFound);
        await owner.ShouldHaveNoRunAsync();
    }

    [Fact]
    public async Task GenerateFactSheetCommand_Success_ProjectsAnAuditRowWithIdsAndNumbersOnly()
    {
        var world = await NewWorldAsync(sources: Sources.FetchedNote);
        var command = new GenerateFactSheetCommand(world.EpisodeId, "k1");

        var result = await world.SendAsync(command);

        var audit = command.ToAuditData();
        audit.ShouldNotBeNull();
        audit.EntityType.ShouldBe("episode");
        audit.EntityId.ShouldBe(world.EpisodeId);
        audit.Summary["generationRunId"].ShouldBe(result.Value.RunId.ToString());
        audit.Summary["trigger"].ShouldBe("user", "the route's own caller is a user trigger, named as the wire value (S71-6)");
        audit.Summary["worstCaseUsd"].ShouldBe(0.5m);
        audit.Summary.Keys.OrderBy(key => key, StringComparer.Ordinal).ShouldBe(AuditKeys);
    }

    [Fact]
    public void GenerateFactSheetCommand_FailedHandler_ProjectsNoAuditRow() =>
        new GenerateFactSheetCommand(Guid.CreateVersion7(), "k1").ToAuditData().ShouldBeNull();

    /// <summary>
    /// The D2 partial unique index is the guarantee behind the handler's active-run check: a second queued run
    /// for one episode must come back as the route's 409, never as a raw 500 from the provider exception.
    /// </summary>
    [Fact]
    public async Task Commit_SecondActiveRun_MapsTheUniqueViolationToFactSheet409()
    {
        var world = await NewWorldAsync(sources: Sources.FetchedNote);
        await world.SeedActiveRunAsync(GenerationKind.FactSheet);

        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        var unit = new EfUnitOfWork(db);
        await unit.BeginAsync(Ct);
        db.GenerationRuns.Add(world.QueuedFactSheetRun());

        var result = await unit.CommitAsync(Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.FactSheetGenerationInProgress);
        (await world.RunCountAsync()).ShouldBe(1, "the refused insert rolled back");
    }

    [Fact]
    public async Task GenerateFactSheetCommand_ArchivedSeries_ReturnsSeriesArchived()
    {
        // SEC-3: archiving a series must stop new paid runs for its episodes, exactly as the story command
        // refuses to generate a story for one.
        var world = await NewWorldAsync(sources: Sources.FetchedNote);
        await Database.ExecuteAsync($"UPDATE series SET archived_at = now() WHERE id = '{world.SeriesId}'", Ct);

        var result = await world.SendAsync(new GenerateFactSheetCommand(world.EpisodeId, "k1"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.SeriesArchived);
        await world.ShouldHaveNoRunAsync();
        world.Budget.Checked.ShouldBeEmpty("the archived series is refused before the cost is estimated");
        world.Ledger.Reservations.ShouldBeEmpty();
    }

    /// <summary>
    /// S71-6: the autopilot's sheet generation must record <see cref="GenerationTrigger.Autopilot"/> on the run row
    /// (plan §3.5 rule 241) and on the audit row, and the trigger must be the caller's, not a hard-coded user one.
    /// </summary>
    [Fact]
    public async Task GenerateFactSheetCommand_AutopilotTriggerUnderSystemScope_QueuesAnAutopilotRun()
    {
        var world = await NewWorldAsync(sources: Sources.FetchedNote, actor: ActorType.System);
        var command = new GenerateFactSheetCommand(world.EpisodeId, "autopilot:s:t:fact_sheet", GenerationTrigger.Autopilot);

        var result = await world.SendAsync(command);

        result.IsError.ShouldBeFalse(string.Join(";", result.ErrorsOrEmptyList));
        (await world.OnlyRunAsync()).Trigger.ShouldBe(GenerationTrigger.Autopilot);
        command.ToAuditData()!.Summary["trigger"].ShouldBe("autopilot", "the audit row stores the wire value, not the enum");
    }

    /// <summary>Only a system scope may name a non-user trigger, exactly as the story and source-search commands rule.</summary>
    [Fact]
    public async Task GenerateFactSheetCommand_AutopilotTriggerUnderUserScope_ReturnsForbidden()
    {
        var world = await NewWorldAsync(sources: Sources.FetchedNote);

        var result = await world.SendAsync(
            new GenerateFactSheetCommand(world.EpisodeId, "k1", GenerationTrigger.Autopilot));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.Forbidden);
        await world.ShouldHaveNoRunAsync();
        world.Budget.Checked.ShouldBeEmpty("the scope guard runs before the cost is estimated");
        world.Ledger.Reservations.ShouldBeEmpty();
    }

    /// <summary>Which sources the episode already has; a note is born <c>fetched</c>, a url source starts <c>pending</c>.</summary>
    private enum Sources
    {
        None,
        FetchedNote,
        FailedUrl,
    }

    private async Task<World> NewWorldAsync(
        EpisodeStatus status = EpisodeStatus.Draft,
        Topic topic = Topic.TrueCrime,
        Sources sources = Sources.None,
        ActorType actor = ActorType.User)
    {
        var setup = new TestCurrentUser();
        var user = await new UserBuilder(Database, setup).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, setup).CreateAsync(user.Id, topic: topic, ct: Ct);
        var episode = await new EpisodeBuilder(Database, setup).CreateAsync(user.Id, series.Id, status: status, ct: Ct);

        var world = new World(Database, user.Id, series.Id, episode.Id, actor);
        if (sources != Sources.None)
        {
            await world.SeedSourceAsync(sources == Sources.FetchedNote);
        }

        return world;
    }

    private sealed class World(TestDatabase database, Guid userId, Guid seriesId, Guid episodeId, ActorType actor)
    {
        private readonly TestCurrentUser _current = new() { Id = userId, Actor = actor };

        public Guid UserId => userId;

        public Guid SeriesId => seriesId;

        public Guid EpisodeId => episodeId;

        public FakeBudgetGuard Budget { get; } = new();

        public RecordingLedger Ledger { get; } = new();

        public async Task<ErrorOr<FactSheetRunDto>> SendAsync(GenerateFactSheetCommand command)
        {
            await using var db = database.CreateAppDbContext(_current, new FakeClock(Now));
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            var handler = new GenerateFactSheetCommandHandler(
                db,
                new FixedEstimator(),
                Budget,
                Ledger,
                _current,
                new FakeClock(Now));

            var result = await handler.Handle(command, Ct);
            if (!result.IsError)
            {
                await db.SaveChangesAsync(Ct);
                await transaction.CommitAsync(Ct);
            }

            return result;
        }

        public async Task SeedSourceAsync(bool fetched)
        {
            await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = userId });
            var source = StorySource
                .CreateUrl(
                    Guid.CreateVersion7(),
                    userId,
                    episodeId,
                    SourceUrl.Parse("https://example.com/true-crime", new FakePublicSuffixList()).Value,
                    Now)
                .Value;
            if (fetched)
            {
                source.MarkFetched(
                    "https://example.com/true-crime",
                    "A true-crime article",
                    "text/html",
                    new string('a', 64),
                    "sources/seed.txt",
                    120,
                    Now);
            }
            else
            {
                source.MarkFailed("source_unreachable", 503, Now);
            }

            if (fetched)
            {
                // The gate counts a user note too (02.5.3 plan §4.1), so cover that kind as well.
                db.StorySources.Add(StorySource.CreateNote(
                    Guid.CreateVersion7(),
                    userId,
                    episodeId,
                    "A true-crime note kept by the owner.",
                    Now).Value);
            }

            db.StorySources.Add(source);
            await db.SaveChangesAsync(Ct);
        }

        public async Task SeedActiveRunAsync(GenerationKind kind)
        {
            await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = userId });
            db.GenerationRuns.Add(QueuedFactSheetRun(kind));
            await db.SaveChangesAsync(Ct);
        }

        public GenerationRun QueuedFactSheetRun(GenerationKind kind = GenerationKind.FactSheet) =>
            GenerationRun.Queue(
                    userId,
                    seriesId,
                    episodeId,
                    kind,
                    GenerationTrigger.User,
                    null,
                    EpisodeStatus.Draft,
                    null,
                    null,
                    0m,
                    Now,
                    countsTowardLimit: false)
                .Value;

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

            return await db.Episodes
                .AsNoTracking()
                .Where(episode => episode.Id == episodeId)
                .Select(episode => episode.Status)
                .SingleAsync(Ct);
        }
    }

    private sealed class FixedEstimator : IFactSheetCostEstimator
    {
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(0.5m, 1000, 500));
    }

    private sealed class RecordingLedger : ICreditLedger
    {
        public List<ReservationId> Reservations { get; } = [];

        public List<CreditOperation> Operations { get; } = [];

        public List<Guid> References { get; } = [];

        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct)
        {
            Operations.Add(op);
            References.Add(referenceId);
            var id = new ReservationId(Guid.CreateVersion7());
            Reservations.Add(id);

            return ValueTask.FromResult<ErrorOr<ReservationId>>(id);
        }

        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);

        public ValueTask<ErrorOr<Success>> RefundAsync(
            ReservationId reservationId,
            string reason,
            CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
    }
}
