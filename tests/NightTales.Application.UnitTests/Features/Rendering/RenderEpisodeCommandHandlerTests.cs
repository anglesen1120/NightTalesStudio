using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Abstractions.Stories;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Rendering;
using NightTales.Application.Features.Rendering.Commands.RenderEpisode;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering;
using NightTales.Domain.Rendering.Subtitles;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Keywords;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Rendering;

/// <summary>
/// <see cref="RenderEpisodeCommandHandler"/> (09.6.1-T1, 09 README BD5) against a real <c>AppDbContext</c>: the
/// BD5 call order of the six ports, the script bound in code points, the lint and plan short-circuits, the
/// spend cap, the reserve contract and the content-free <c>render-params.v1</c>, plus the state, foreign-owner
/// and <c>If-Match</c> guards. The ports are hand-written recording fakes; the handler is called directly, so
/// no HTTP, idempotency or audit behaviour is in the way.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RenderEpisodeCommandHandlerTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The keywords the happy path plans images from; every one passes <see cref="KeywordRules"/>.</summary>
    private static readonly SegmentKeywords[] DefaultSegments =
    [
        new("hook", ["quiet street at night"]),
        new("p:0", ["empty corridor at dawn"]),
        new("ending", ["closed door in the hall"]),
    ];

    /// <summary>One astral code point (U+1F600) whose UTF-16 form is two units, so code points ≠ units.</summary>
    private const string AstralCodePoint = "\U0001F600";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Repeat(string value, int count) => string.Concat(Enumerable.Repeat(value, count));

    [Fact]
    public async Task Order_MatchesBd5()
    {
        var seed = await SeedAsync();
        var ports = PortsFor(seed);
        await using var db = Open(seed.UserId);
        var handler = Handler(db, ports);

        var result = await handler.Handle(Command(seed, ETags.Episode(seed.EpisodeVersion)), Ct);

        result.IsError.ShouldBeFalse();
        ports.Calls.ShouldBe(
            ["storyInput", "nameCorpus", "priceCatalog", "imageCostEstimator", "budgetGuard", "recentMusic", "creditLedger"]);
    }

    [Fact]
    public async Task Script_20001CodePoints_ReturnsValidationFailed_FieldScript()
    {
        var seed = await SeedAsync();
        // U+1F600 is one code point in two UTF-16 units, so the bound is counted in code points, not units.
        var body = Repeat(AstralCodePoint, RenderEpisodeCommandHandler.MaxScriptCodePoints + 1);
        body.Length.ShouldBe(2 * (RenderEpisodeCommandHandler.MaxScriptCodePoints + 1));
        var ports = PortsFor(seed, StoryInput(seed.StoryVersionId, DefaultSegments, body));
        await using var db = Open(seed.UserId);
        var handler = Handler(db, ports);

        var result = await handler.Handle(Command(seed, ETags.Episode(seed.EpisodeVersion)), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.ValidationFailed);
        result.FirstError.Metadata![ErrorMetadata.Field].ShouldBe(RenderEpisodeCommandHandler.ScriptField);
        ports.Reserves.ShouldBeEmpty();
    }

    [Fact]
    public async Task Script_AtLimit_Passes()
    {
        var seed = await SeedAsync();
        var body = Repeat(AstralCodePoint, RenderEpisodeCommandHandler.MaxScriptCodePoints);
        var ports = PortsFor(seed, StoryInput(seed.StoryVersionId, DefaultSegments, body));
        await using var db = Open(seed.UserId);
        var handler = Handler(db, ports);

        var result = await handler.Handle(Command(seed, ETags.Episode(seed.EpisodeVersion)), Ct);

        result.IsError.ShouldBeFalse();
        result.Value.Status.ShouldBe(RenderJob.QueuedStage);
    }

    [Fact]
    public async Task LinterBlocking_ReturnsKeywordsFlagged_WithErrors()
    {
        var seed = await SeedAsync();
        var ports = PortsFor(
            seed,
            StoryInput(
                seed.StoryVersionId,
                [new SegmentKeywords("hook", ["quiet street at night", "two words"]), new SegmentKeywords("p:0", ["tiny"])]));
        await using var db = Open(seed.UserId);
        var handler = Handler(db, ports);

        var result = await handler.Handle(Command(seed, ETags.Episode(seed.EpisodeVersion)), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.KeywordsFlagged);
        var flags = result.FirstError.Metadata![ErrorMetadata.Flags].ShouldBeAssignableTo<IReadOnlyList<KeywordFlag>>();
        flags.ShouldBe(
        [
            new KeywordFlag("hook", 1, StoryDraftValidator.KeywordInvalid),
            new KeywordFlag("p:0", 0, StoryDraftValidator.KeywordInvalid),
        ]);
        result.FirstError.Description.ShouldNotContain("two words");
        ports.Reserves.ShouldBeEmpty();
        (await CountRenderJobsAsync(seed)).ShouldBe(0);
    }

    [Fact]
    public async Task Plan_NoTerms_ReturnsKeywordsFlagged_NoScenes()
    {
        var seed = await SeedAsync();
        var ports = PortsFor(seed, StoryInput(seed.StoryVersionId, []));
        await using var db = Open(seed.UserId);
        var handler = Handler(db, ports);

        var result = await handler.Handle(Command(seed, ETags.Episode(seed.EpisodeVersion)), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.KeywordsFlagged);
        var flags = result.FirstError.Metadata![ErrorMetadata.Flags].ShouldBeAssignableTo<IReadOnlyList<KeywordFlag>>();
        flags.ShouldBe([new KeywordFlag(RenderErrorRules.MetaSegment, 0, RenderErrorRules.NoScenes)]);
        ports.Reserves.ShouldBeEmpty();
    }

    [Fact]
    public async Task BudgetGuardRefused_ReturnsSpendCapReached_NoRowWritten()
    {
        var seed = await SeedAsync();
        var ports = PortsFor(seed, budget: Error.Failure(ErrorCodes.SpendCapReached, "The platform spend cap was reached."));
        await using var db = Open(seed.UserId);
        var handler = Handler(db, ports);

        var result = await handler.Handle(Command(seed, ETags.Episode(seed.EpisodeVersion)), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.SpendCapReached);
        ports.Calls.ShouldBe(["storyInput", "nameCorpus", "priceCatalog", "imageCostEstimator", "budgetGuard"]);
        ports.Reserves.ShouldBeEmpty();
        db.ChangeTracker.Entries<RenderJob>().ShouldBeEmpty();
        await db.SaveChangesAsync(Ct);
        (await CountRenderJobsAsync(seed)).ShouldBe(0);
    }

    [Fact]
    public async Task Reserve_UsesRenderOperation_AndPersistsReservationId()
    {
        var seed = await SeedAsync();
        var reservation = new ReservationId(Guid.CreateVersion7());
        var ports = PortsFor(seed, reservation: reservation);
        await using var db = Open(seed.UserId);
        var handler = Handler(db, ports);

        var result = await handler.Handle(Command(seed, ETags.Episode(seed.EpisodeVersion)), Ct);
        result.IsError.ShouldBeFalse();
        await db.SaveChangesAsync(Ct);

        await using var read = Open(seed.UserId);
        var job = await read.RenderJobs.AsNoTracking().SingleAsync(row => row.EpisodeId == seed.EpisodeId, Ct);
        job.Id.ShouldBe(result.Value.RenderJobId);
        job.ReservationId.ShouldNotBe(Guid.Empty);
        job.ReservationId.ShouldBe(reservation.Value);
        ports.Reserves.ShouldBe([(CreditOperation.Render, job.Id)]);
    }

    [Fact]
    public async Task Params_ContainsNoContent_Fields()
    {
        var seed = await SeedAsync();
        var ports = PortsFor(seed);
        await using var db = Open(seed.UserId);
        var handler = Handler(db, ports);

        var result = await handler.Handle(Command(seed, ETags.Episode(seed.EpisodeVersion)), Ct);
        result.IsError.ShouldBeFalse();
        await db.SaveChangesAsync(Ct);

        var stored = (await Database.QueryStringsAsync(
            $"SELECT params::text FROM render_jobs WHERE id = '{result.Value.RenderJobId}'", Ct)).Single();
        var imagesNeeded = (await Database.QueryStringsAsync(
            $"SELECT images_needed::text FROM render_jobs WHERE id = '{result.Value.RenderJobId}'", Ct)).Single();

        stored.ShouldNotContain("video_script");
        stored.ShouldNotContain("video_terms");
        stored.ShouldNotContain("quiet street");
        stored.ShouldNotContain("The hook. The body. The ending.");

        using var document = JsonDocument.Parse(stored);
        document.RootElement.GetProperty("schema").GetString().ShouldBe(RenderParamsV1.SchemaName);
        var needed = document.RootElement.GetProperty("image_plan").GetProperty("needed").GetInt32();
        int.Parse(imagesNeeded, CultureInfo.InvariantCulture).ShouldBe(needed);

        await using var read = Open(seed.UserId);
        var job = await read.RenderJobs.AsNoTracking().SingleAsync(row => row.Id == result.Value.RenderJobId, Ct);
        job.ImagesNeeded.ShouldBe(job.Params.ImagePlan.Needed);
    }

    [Fact]
    public async Task OtherUsersEpisode_ReturnsNotFound()
    {
        var seed = await SeedAsync();
        var stranger = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct);
        var ports = PortsFor(seed);
        await using var db = Open(stranger.Id);
        var handler = Handler(db, ports);

        var result = await handler.Handle(Command(seed, ETags.Episode(seed.EpisodeVersion)), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.NotFound);
        ports.StoryInput.CallCount.ShouldBe(0);
        ports.Reserves.ShouldBeEmpty();
    }

    [Fact]
    public async Task EpisodeNotApproved_ReturnsInvalidState()
    {
        var seed = await SeedAsync(EpisodeStatus.StoryReview);
        var ports = PortsFor(seed);
        await using var db = Open(seed.UserId);
        var handler = Handler(db, ports);

        var result = await handler.Handle(Command(seed, ETags.Episode(seed.EpisodeVersion)), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.EpisodeInvalidState);
        ports.StoryInput.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task NullApprovedPointer_ReturnsInvalidState_WithoutStoryRead()
    {
        var seed = await SeedAsync(withApprovedPointer: false);
        var ports = PortsFor(seed);
        await using var db = Open(seed.UserId);
        var handler = Handler(db, ports);

        var result = await handler.Handle(Command(seed, ETags.Episode(seed.EpisodeVersion)), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.EpisodeInvalidState);
        ports.StoryInput.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task StaleIfMatch_ReturnsEpisodeConflict()
    {
        var seed = await SeedAsync();
        var ports = PortsFor(seed);
        await using var db = Open(seed.UserId);
        var handler = Handler(db, ports);

        var result = await handler.Handle(Command(seed, ETags.Episode(seed.EpisodeVersion + 1)), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.EpisodeConflict);
        ports.StoryInput.CallCount.ShouldBe(0);
    }

    private static RenderEpisodeCommand Command(Seed seed, string ifMatch) =>
        new(seed.EpisodeId, ifMatch, $"idem-{seed.EpisodeId:N}");

    private static StoryRenderInput StoryInput(
        Guid storyVersionId,
        IReadOnlyList<SegmentKeywords> segments,
        string? body = null) =>
        new(
            storyVersionId,
            body ?? "The hook. The body. The ending.",
            [.. segments.SelectMany(segment => segment.Keywords)],
            "The approved story",
            "vi",
            LengthTier.Short,
            segments);

    private static RenderEpisodeCommandHandler Handler(IAppDbContext db, Ports ports) =>
        new(
            db,
            ports.StoryInput,
            ports.NameCorpus,
            ports.CostEstimator,
            ports.PriceCatalog,
            ports.BudgetGuard,
            ports.Ledger,
            ports.RecentMusic,
            SubtitlePresetCatalog.LoadEmbedded().Value,
            new ImagesOptions(),
            new FakeClock(Now));

    private static Ports PortsFor(
        Seed seed,
        StoryRenderInput? story = null,
        ErrorOr<Success>? budget = null,
        ReservationId? reservation = null)
    {
        var calls = new List<string>();
        var reserves = new List<(CreditOperation Operation, Guid ReferenceId)>();

        return new Ports(
            new RecordingStoryInput(story ?? StoryInput(seed.StoryVersionId, DefaultSegments), calls),
            new RecordingNameCorpus(NameCorpus.Empty, calls),
            new RecordingPriceCatalog(calls),
            new RecordingCostEstimator(calls),
            new RecordingBudgetGuard(budget ?? Result.Success, calls),
            new RecordingCreditLedger(reservation ?? new ReservationId(Guid.CreateVersion7()), calls, reserves),
            new RecordingRecentMusic(calls),
            calls,
            reserves);
    }

    private AppDbContext Open(Guid userId) =>
        Database.CreateAppDbContext(new TestCurrentUser { Id = userId }, new FakeClock(Now));

    private async Task<long> CountRenderJobsAsync(Seed seed)
    {
        var counts = await Database.QueryStringsAsync(
            $"SELECT count(*)::text FROM render_jobs WHERE episode_id = '{seed.EpisodeId}'", Ct);

        return long.Parse(counts.Single(), CultureInfo.InvariantCulture);
    }

    private async Task<Seed> SeedAsync(
        EpisodeStatus status = EpisodeStatus.StoryApproved,
        bool withApprovedPointer = true)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor)
            .CreateAsync(user.Id, series.Id, status: status, ct: Ct);

        var version = StoryVersionOf(user.Id, episode.Id);
        await using (var seedWriter = Open(user.Id))
        {
            seedWriter.StoryVersions.Add(version);
            await seedWriter.SaveChangesAsync(Ct);
        }

        if (withApprovedPointer)
        {
            await Database.ExecuteAsync(
                $"UPDATE episodes SET approved_story_version_id = '{version.Id}' WHERE id = '{episode.Id}'", Ct);
        }

        // xmin is read after the pointer UPDATE: that write is what bumps the row version the If-Match compares.
        var episodeVersion = uint.Parse(
            (await Database.QueryStringsAsync($"SELECT xmin::text FROM episodes WHERE id = '{episode.Id}'", Ct)).Single(),
            CultureInfo.InvariantCulture);

        return new Seed(user.Id, series.Id, episode.Id, version.Id, episodeVersion);
    }

    private static StoryVersion StoryVersionOf(Guid userId, Guid episodeId)
    {
        var draft = new StoryContentV1(
            "The approved story",
            new StorySegmentV1("The hook opens the episode.", ["hook keyword"], []),
            [new StorySegmentV1("The body continues it.", ["body keyword"], [])],
            new StorySegmentV1("The ending closes it.", ["ending keyword"], []),
            "A persisted story for the render hand-off.",
            [],
            [],
            new StoryStructureV1("ghost story", "old house", "a midnight call", "the narrator", "first_person"));

        return StoryVersion.Create(
            userId,
            episodeId,
            1,
            StoryVersionSource.Generated,
            draft,
            StoryContentJson.Write(draft, new JsonArray()),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            Now);
    }

    /// <summary>The ids one seeded render world carries, plus the episode's <c>xmin</c> for the <c>If-Match</c>.</summary>
    private sealed record Seed(Guid UserId, Guid SeriesId, Guid EpisodeId, Guid StoryVersionId, uint EpisodeVersion);

    /// <summary>The six ports the handler calls, plus the shared call-order and reserve recorders.</summary>
    private sealed record Ports(
        RecordingStoryInput StoryInput,
        RecordingNameCorpus NameCorpus,
        RecordingPriceCatalog PriceCatalog,
        RecordingCostEstimator CostEstimator,
        RecordingBudgetGuard BudgetGuard,
        RecordingCreditLedger Ledger,
        RecordingRecentMusic RecentMusic,
        List<string> Calls,
        List<(CreditOperation Operation, Guid ReferenceId)> Reserves);

    private sealed class RecordingStoryInput(ErrorOr<StoryRenderInput> story, List<string> calls) : IStoryRenderInputProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<ErrorOr<StoryRenderInput>> GetAsync(Guid storyVersionId, CancellationToken ct)
        {
            CallCount++;
            calls.Add("storyInput");

            return ValueTask.FromResult(story);
        }
    }

    private sealed class RecordingNameCorpus(NameCorpus corpus, List<string> calls) : INameCorpusProvider
    {
        public ValueTask<NameCorpus> LoadAsync(Guid episodeId, CancellationToken ct)
        {
            calls.Add("nameCorpus");

            return ValueTask.FromResult(corpus);
        }
    }

    private sealed class RecordingPriceCatalog(List<string> calls) : IPriceCatalog
    {
        public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct)
        {
            calls.Add("priceCatalog");

            return Task.FromResult<ErrorOr<PriceVersion>>(
                PriceVersion.Create(1, Now.AddDays(-1), "{}", "{}", "hash", Now.AddDays(-1)));
        }

        public Task<PriceVersion> GetAsync(int version, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RecordingCostEstimator(List<string> calls) : IImageCostEstimator
    {
        public Task<CostBound> WorstCaseAsync(Aspect aspect, int maxTerms, int priceVersion, CancellationToken ct)
        {
            calls.Add("imageCostEstimator");

            return Task.FromResult(new CostBound(1.23m));
        }
    }

    private sealed class RecordingBudgetGuard(ErrorOr<Success> result, List<string> calls) : IBudgetGuard
    {
        public ValueTask<ErrorOr<Success>> CheckAsync(CostBound worstCaseUsd, CancellationToken ct, Guid? excludingRunId = null)
        {
            calls.Add("budgetGuard");

            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingCreditLedger(
        ReservationId reservation,
        List<string> calls,
        List<(CreditOperation Operation, Guid ReferenceId)> reserves) : ICreditLedger
    {
        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct)
        {
            calls.Add("creditLedger");
            reserves.Add((op, referenceId));

            return ValueTask.FromResult<ErrorOr<ReservationId>>(reservation);
        }

        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRecentMusic(List<string> calls) : IRecentMusicQuery
    {
        public Task<IReadOnlyList<string>> GetRecentTrackIdsAsync(Guid seriesId, int take, CancellationToken ct)
        {
            calls.Add("recentMusic");

            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }
}
