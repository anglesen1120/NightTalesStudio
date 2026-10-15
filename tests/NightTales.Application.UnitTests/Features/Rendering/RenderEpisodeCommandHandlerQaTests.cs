using System.Globalization;
using System.Text.Json.Nodes;
using ErrorOr;
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
/// QA negative and boundary checks for <see cref="RenderEpisodeCommandHandler"/> (09.6.1-T1) against a real
/// <c>AppDbContext</c>: refused code points in the script, the NFC-then-count boundary, the second-request
/// conflict, the weak and unquoted <c>If-Match</c>, and the price-catalogue fail-closed path. Written by the QA
/// agent; the helpers and recording fakes are copied from <c>RenderEpisodeCommandHandlerTests</c> because they are
/// private there.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RenderEpisodeCommandHandlerQaTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    private static readonly SegmentKeywords[] DefaultSegments =
    [
        new("hook", ["quiet street at night"]),
        new("p:0", ["empty corridor at dawn"]),
        new("ending", ["closed door in the hall"]),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Repeat(string value, int count) => string.Concat(Enumerable.Repeat(value, count));

    [Theory]
    [InlineData("\uD800")]
    [InlineData("\uDFFF")]
    [InlineData("\uFFFE")]
    [InlineData("\uFDD0")]
    [InlineData("\U0010FFFF")]
    public async Task Script_WithRefusedCodePoint_DoesNotThrow_AndIsCountedAsOneSpace(string refused)
    {
        var seed = await SeedAsync();
        var body = "The hook. The body. The ending. " + refused;
        var ports = PortsFor(seed, StoryInput(seed.StoryVersionId, DefaultSegments, body));
        await using var db = Open(seed.UserId);
        var handler = Handler(db, ports);

        var result = await handler.Handle(Command(seed, ETags.Episode(seed.EpisodeVersion)), Ct);

        result.IsError.ShouldBeFalse();
        result.Value.Status.ShouldBe(RenderJob.QueuedStage);
    }

    [Theory]
    [InlineData("\uFFFE")]
    [InlineData("\U0001FFFE")]
    public async Task Keyword_WithNoncharacter_DoesNotThrow_AndKeepsTheFlagIndex(string refused)
    {
        var seed = await SeedAsync();
        var ports = PortsFor(
            seed,
            StoryInput(
                seed.StoryVersionId,
                [new SegmentKeywords("hook", ["quiet street" + refused + "at night", "two" + refused + "words"])]));
        await using var db = Open(seed.UserId);
        var handler = Handler(db, ports);

        var result = await handler.Handle(Command(seed, ETags.Episode(seed.EpisodeVersion)), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.KeywordsFlagged);
        var flags = result.FirstError.Metadata![ErrorMetadata.Flags].ShouldBeAssignableTo<IReadOnlyList<KeywordFlag>>();
        flags.ShouldBe([new KeywordFlag("hook", 1, StoryDraftValidator.KeywordInvalid)]);
    }

    [Fact]
    public async Task Script_NoncharacterAtTheLimit_Passes_AfterReplacement()
    {
        var seed = await SeedAsync();
        // 19,999 letters plus one noncharacter: the noncharacter becomes one space, so the body is exactly 20,000.
        var body = Repeat("a", RenderEpisodeCommandHandler.MaxScriptCodePoints - 1) + "\uFFFF";
        var ports = PortsFor(seed, StoryInput(seed.StoryVersionId, DefaultSegments, body));
        await using var db = Open(seed.UserId);
        var handler = Handler(db, ports);

        var result = await handler.Handle(Command(seed, ETags.Episode(seed.EpisodeVersion)), Ct);

        result.IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task Script_NoncharacterOverTheLimit_ReturnsValidationFailed_FieldScript()
    {
        var seed = await SeedAsync();
        var body = Repeat("a", RenderEpisodeCommandHandler.MaxScriptCodePoints) + "\uFFFF";
        var ports = PortsFor(seed, StoryInput(seed.StoryVersionId, DefaultSegments, body));
        await using var db = Open(seed.UserId);
        var handler = Handler(db, ports);

        var result = await handler.Handle(Command(seed, ETags.Episode(seed.EpisodeVersion)), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.ValidationFailed);
        result.FirstError.Metadata![ErrorMetadata.Field].ShouldBe(RenderEpisodeCommandHandler.ScriptField);
        ports.Reserves.ShouldBeEmpty();
    }

    [Fact]
    public async Task Script_DecomposedAccents_AtTheLimitAfterNfc_Passes()
    {
        var seed = await SeedAsync();
        // "e" + combining acute is two code points that NFC composes into one; the bound is on the composed count.
        var body = Repeat("e\u0301", RenderEpisodeCommandHandler.MaxScriptCodePoints);
        var ports = PortsFor(seed, StoryInput(seed.StoryVersionId, DefaultSegments, body));
        await using var db = Open(seed.UserId);
        var handler = Handler(db, ports);

        var result = await handler.Handle(Command(seed, ETags.Episode(seed.EpisodeVersion)), Ct);

        result.IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task Script_DecomposedAccents_OneOverTheLimitAfterNfc_ReturnsValidationFailed()
    {
        var seed = await SeedAsync();
        var body = Repeat("e\u0301", RenderEpisodeCommandHandler.MaxScriptCodePoints + 1);
        var ports = PortsFor(seed, StoryInput(seed.StoryVersionId, DefaultSegments, body));
        await using var db = Open(seed.UserId);
        var handler = Handler(db, ports);

        var result = await handler.Handle(Command(seed, ETags.Episode(seed.EpisodeVersion)), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.ValidationFailed);
        result.FirstError.Metadata![ErrorMetadata.Field].ShouldBe(RenderEpisodeCommandHandler.ScriptField);
    }

    [Fact]
    public async Task SecondRequest_DifferentKey_AfterASuccessfulRender_Returns409_EpisodeInvalidState()
    {
        var seed = await SeedAsync();
        var firstPorts = PortsFor(seed);
        await using (var first = Open(seed.UserId))
        {
            var accepted = await Handler(first, firstPorts).Handle(Command(seed, ETags.Episode(seed.EpisodeVersion)), Ct);
            accepted.IsError.ShouldBeFalse();
            // The handler does not save; the pipeline does. Persist here so the second request sees the Rendering state.
            await first.SaveChangesAsync(Ct);
        }

        var secondPorts = PortsFor(seed);
        await using var second = Open(seed.UserId);
        var result = await Handler(second, secondPorts).Handle(
            new RenderEpisodeCommand(seed.EpisodeId, ETags.Episode(seed.EpisodeVersion), $"idem-second-{seed.EpisodeId:N}"),
            Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.EpisodeInvalidState);
        secondPorts.StoryInput.CallCount.ShouldBe(0);
        secondPorts.Reserves.ShouldBeEmpty();
        (await CountRenderJobsAsync(seed)).ShouldBe(1);
    }

    [Fact]
    public async Task WeakIfMatch_ReturnsEpisodeConflict_WithoutAProviderCall()
    {
        var seed = await SeedAsync();
        var ports = PortsFor(seed);
        await using var db = Open(seed.UserId);
        var handler = Handler(db, ports);

        var result = await handler.Handle(Command(seed, "W/" + ETags.Episode(seed.EpisodeVersion)), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.EpisodeConflict);
        ports.StoryInput.CallCount.ShouldBe(0);
        ports.Reserves.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnquotedIfMatch_ReturnsEpisodeConflict_WithoutAProviderCall()
    {
        var seed = await SeedAsync();
        var ports = PortsFor(seed);
        await using var db = Open(seed.UserId);
        var handler = Handler(db, ports);

        var result = await handler.Handle(
            Command(seed, $"ep-{seed.EpisodeVersion.ToString(CultureInfo.InvariantCulture)}"),
            Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.EpisodeConflict);
        ports.StoryInput.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task PriceCatalogMissing_ReturnsServiceUnavailable_BeforeEstimatingOrReserving()
    {
        var seed = await SeedAsync();
        var ports = PortsFor(seed, priceFails: true);
        await using var db = Open(seed.UserId);
        var handler = Handler(db, ports);

        var result = await handler.Handle(Command(seed, ETags.Episode(seed.EpisodeVersion)), Ct);

        result.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
        ports.Calls.ShouldNotContain("imageCostEstimator");
        ports.Calls.ShouldNotContain("creditLedger");
        ports.Reserves.ShouldBeEmpty();
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

    private static RenderEpisodeCommandHandler Handler(IAppDbContext db, QaPorts ports) =>
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

    private static QaPorts PortsFor(Seed seed, StoryRenderInput? story = null, bool priceFails = false)
    {
        var calls = new List<string>();
        var reserves = new List<(CreditOperation Operation, Guid ReferenceId)>();

        return new QaPorts(
            new RecordingStoryInput(story ?? StoryInput(seed.StoryVersionId, DefaultSegments), calls),
            new RecordingNameCorpus(NameCorpus.Empty, calls),
            new RecordingPriceCatalog(calls, priceFails),
            new RecordingCostEstimator(calls),
            new RecordingBudgetGuard(Result.Success, calls),
            new RecordingCreditLedger(new ReservationId(Guid.CreateVersion7()), calls, reserves),
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

    private async Task<Seed> SeedAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor)
            .CreateAsync(user.Id, series.Id, status: EpisodeStatus.StoryApproved, ct: Ct);

        var version = StoryVersionOf(user.Id, episode.Id);
        await using (var seedWriter = Open(user.Id))
        {
            seedWriter.StoryVersions.Add(version);
            await seedWriter.SaveChangesAsync(Ct);
        }

        await Database.ExecuteAsync(
            $"UPDATE episodes SET approved_story_version_id = '{version.Id}' WHERE id = '{episode.Id}'", Ct);

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

    private sealed record Seed(Guid UserId, Guid SeriesId, Guid EpisodeId, Guid StoryVersionId, uint EpisodeVersion);

    private sealed record QaPorts(
        RecordingStoryInput StoryInput,
        INameCorpusProvider NameCorpus,
        IPriceCatalog PriceCatalog,
        IImageCostEstimator CostEstimator,
        IBudgetGuard BudgetGuard,
        ICreditLedger Ledger,
        IRecentMusicQuery RecentMusic,
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

    private sealed class RecordingPriceCatalog(List<string> calls, bool fails) : IPriceCatalog
    {
        public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct)
        {
            calls.Add("priceCatalog");

            if (fails)
            {
                return Task.FromResult<ErrorOr<PriceVersion>>(Error.NotFound("price_catalog.missing", "No active price version."));
            }

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

/// <summary>QA pure checks on the rendering error builders (09.6.1-T1): no database.</summary>
public sealed class RenderErrorsQaTests
{
    [Fact]
    public void KeywordsFlagged_CapsTheRowsAt100_KeepingTheFirstHundredInOrder()
    {
        var flags = Enumerable.Range(0, 150).Select(i => new KeywordFlag("hook", i, "blocked_term")).ToList();

        var error = Errors.KeywordsFlagged(flags);

        error.Code.ShouldBe(ErrorCodes.KeywordsFlagged);
        var rows = ((IEnumerable<KeywordFlag>)error.Metadata![ErrorMetadata.Flags]).ToList();
        rows.Count.ShouldBe(100);
        rows[0].Index.ShouldBe(0);
        rows[99].Index.ShouldBe(99);
    }

    [Fact]
    public void NoScenes_CarriesExactlyOneMetaRow_WithTheNoScenesRule()
    {
        var error = Errors.NoScenes();

        error.Code.ShouldBe(ErrorCodes.KeywordsFlagged);
        var rows = ((IEnumerable<KeywordFlag>)error.Metadata![ErrorMetadata.Flags]).ToList();
        rows.Count.ShouldBe(1);
        rows[0].Segment.ShouldBe("meta");
        rows[0].Index.ShouldBe(0);
        rows[0].Rule.ShouldBe("no_scenes");
    }
}
