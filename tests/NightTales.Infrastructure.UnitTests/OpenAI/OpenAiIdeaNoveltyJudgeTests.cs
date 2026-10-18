using System.Globalization;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.Configuration;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Prompts;
using NightTales.Domain.Stories.Structure;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Autopilot;
using NightTales.Infrastructure.OpenAI;
using NightTales.TestKit.Text;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.OpenAI;

/// <summary>
/// 02.8.1-T2 task Design: the idea-novelty judge step over a scripted <see cref="ITextClient"/> — the request
/// shape (step, schema, cache key, the ruled output cap from the run's price version), the untouched provider
/// outcomes, the D14 usage row (<c>operation = idea</c>) with the D7/SEC-1 rules, and the two branches that must
/// fail before a call is dispatched. Nothing here reaches a provider.
/// </summary>
public sealed class OpenAiIdeaNoveltyJudgeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly StoryStructureTags Structure = new(
        "truyện ma", "ngôi nhà cũ", "một cuộc điện thoại", "người kể là nguyên nhân", "first_person",
        ["setup", "clue", "twist"]);

    [Fact]
    public async Task NoveltyJudge_Request_UsesTheNoveltyStepCapAndPrompt()
    {
        var scores = Scores();
        var client = new FakeTextClient().EnqueueOk(TextStep.Novelty, scores).EnqueueOk(TextStep.Novelty, scores);
        var rig = new Rig(client);

        var result = await rig.Judge.JudgeAsync(Request(), Ct);

        result.ShouldBeOfType<TextCallOk<NoveltyV1>>().Draft.ShouldBeSameAs(scores);

        var call = rig.Fake.Requests.ShouldHaveSingleItem();
        call.Step.ShouldBe(TextStep.Novelty);
        call.SchemaName.ShouldBe(NoveltyPrompts.SchemaName);
        call.Schema.ToArray().ShouldBe(StubCatalog.SchemaBytes);
        call.PromptCacheKey.ShouldBe(NoveltyPrompts.CacheKey);
        call.PromptCacheKey.ShouldBe("ideas:novelty");
        call.SystemPrompt.ShouldBe("NOVELTY-SYSTEM-PROMPT. Write in Vietnamese.");
        call.Run.ShouldBe(Request().Run);
        call.UserDataJson.ShouldContain("\"task\":\"score_ideas\"");

        // README T56: the cap is the price version's (`price_versions.limits.novelty.max_output_tokens`), carried
        // by the D1 bound, never a configuration key.
        call.MaxOutputTokens.ShouldBe(PriceLimits.DefaultNoveltyMaxOutput);
        call.MaxOutputTokens.ShouldBe(400);

        // D5: the composite version names the files this release shipped for the step.
        var parts = rig.Stubs.Parts.ShouldNotBeNull();
        parts.SystemKey.ShouldBe(NoveltyPrompts.SystemKey);
        parts.SchemaKey.ShouldBe(NoveltyPrompts.SchemaKey);

        // The model is the adapter's step key (`OpenAi:Models:Novelty`), read from configuration by the estimator
        // that priced this call; the request itself carries only the cap.
        rig.Estimator.NoveltyModel.ShouldBe(IdeaCostEstimator.DefaultModel);
        IdeaCostEstimator.NoveltyModelKey.ShouldBe("OpenAi:Models:Novelty");

        // A re-calibrated cap is a new price version: the next call reads it, no code change (D14).
        rig.Prices.Active = Version(2, noveltyMaxOutput: 900);

        await rig.Judge.JudgeAsync(Request(priceVersion: "2"), Ct);

        rig.Fake.Requests[1].MaxOutputTokens.ShouldBe(900);
    }

    [Fact]
    public async Task NoveltyJudge_Success_MetersOneIdeaUsageRowAtTheBound()
    {
        var rig = new Rig(new FakeTextClient().EnqueueOk(TextStep.Novelty, Scores()));
        var request = Request();

        await rig.Judge.JudgeAsync(request, Ct);

        var expectedBound = (await rig.Estimator.NoveltyWorstCaseAsync(request.Run.PriceVersion, Ct)).Value;
        expectedBound.WorstCaseUsd.ShouldBeGreaterThan(0m);

        var item = rig.Meter.Items.ShouldHaveSingleItem();
        item.ShouldSatisfyAllConditions(
            // D14: the judge is the second call of the same `idea` operation (the task's Notes), and it is a
            // series-level call, so it carries no episode (D2).
            i => i.Operation.ShouldBe(IdeaOperations.Idea),
            i => i.Operation.ShouldBe("idea"),
            i => i.EpisodeId.ShouldBeNull(),
            i => i.Provider.ShouldBe(TextCallResultUsageExtensions.OpenAiProvider),
            i => i.Model.ShouldBe(FakeTextClient.Model),
            i => i.UserId.ShouldBe(request.Run.UserId),
            i => i.SeriesId.ShouldBe(request.Run.SeriesId),
            i => i.GenerationRunId.ShouldBe(request.Run.GenerationRunId),
            i => i.ReservationId.ShouldBe(request.Run.Reservation.Value),
            i => i.PriceVersion.ShouldBe(request.Run.PriceVersion),
            i => i.CostUsd.ShouldBe(expectedBound.WorstCaseUsd));
    }

    [Fact]
    public async Task NoveltyJudge_FundsExhausted_IsReturnedUnmetered()
    {
        // SEC-1 / D7: the provider generated nothing, so the call cannot have been billed. The checker maps this
        // outcome to the run's own pause error; the judge only reports it.
        var outcome = TextCallResult<NoveltyV1>.ProviderFundsExhausted(FakeTextClient.Model, "r1");
        var rig = new Rig(new FakeTextClient().Enqueue(TextStep.Novelty, outcome));

        var result = await rig.Judge.JudgeAsync(Request(), Ct);

        result.ShouldBeSameAs(outcome);
        rig.Fake.Requests.ShouldHaveSingleItem();
        rig.Meter.Items.ShouldBeEmpty();
        rig.Meter.Tokens.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoveltyJudge_CancelledAfterDispatch_MetersTheBound_ThenRethrows()
    {
        var rig = new Rig(new CancellingTextClient(dispatch: true));
        var request = Request();
        var expectedBound = (await rig.Estimator.NoveltyWorstCaseAsync(request.Run.PriceVersion, Ct)).Value;

        await Should.ThrowAsync<OperationCanceledException>(async () => await rig.Judge.JudgeAsync(request, Ct));

        var item = rig.Meter.Items.ShouldHaveSingleItem();
        item.ShouldSatisfyAllConditions(
            i => i.Estimated.ShouldBeTrue("the response never arrived, so the row carries the D1 bound"),
            i => i.CostUsd.ShouldBe(expectedBound.WorstCaseUsd),
            i => i.Operation.ShouldBe("idea"),
            i => i.EpisodeId.ShouldBeNull());
        rig.Meter.Tokens.ShouldHaveSingleItem().CanBeCanceled.ShouldBeFalse("the settlement must survive the caller's cancellation");
    }

    [Fact]
    public async Task NoveltyJudge_CancelledBeforeDispatch_MetersNothing()
    {
        var rig = new Rig(new CancellingTextClient(dispatch: false));

        await Should.ThrowAsync<OperationCanceledException>(async () => await rig.Judge.JudgeAsync(Request(), Ct));

        rig.Meter.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoveltyJudge_AStrayPlaceholder_ThrowsBeforeTheCall()
    {
        // 02 README D5: the placeholder set of a step prompt is closed; a template that carries another one is a
        // release bug, and the call must not be sent with it.
        var client = new FakeTextClient();
        var rig = new Rig(client, stepPrompt: "NOVELTY-PROMPT. Write in {{language_name}} about {{topic}}.");

        await Should.ThrowAsync<InvalidOperationException>(async () => await rig.Judge.JudgeAsync(Request(), Ct));

        rig.Fake.Requests.ShouldBeEmpty();
        rig.Meter.Items.ShouldBeEmpty();
    }

    /// <summary>
    /// The byte ceiling trims the corpus and never the batch: whole past episodes go, oldest first, and the call
    /// is still made with what fits — the batch is judged, not failed (F2/S2).
    /// </summary>
    [Fact]
    public async Task NoveltyJudge_OversizeDataObject_TrimsTheOldestEpisodesAndStillCalls()
    {
        var scores = Scores();
        var client = new FakeTextClient().EnqueueOk(TextStep.Novelty, scores);

        // Three episodes longer than the batch, so the corpus is what the ceiling cuts.
        var request = Request() with
        {
            PastEpisodes =
            [
                new CorpusEpisode(Guid.CreateVersion7(), "Tập 0", new string('x', 600)),
                new CorpusEpisode(Guid.CreateVersion7(), "Tập 1", new string('x', 600)),
                new CorpusEpisode(Guid.CreateVersion7(), "Tập 2", new string('x', 600)),
            ],
        };
        var newestOnly = Encoding.UTF8.GetByteCount(
            NoveltyDataObjectJson.Build(request with { PastEpisodes = request.PastEpisodes.Take(1).ToArray() }));
        Encoding.UTF8.GetByteCount(NoveltyDataObjectJson.Build(request)).ShouldBeGreaterThan(newestOnly);

        var rig = new Rig(client, maxDataObjectBytes: newestOnly);

        var result = await rig.Judge.JudgeAsync(request, Ct);

        result.ShouldBeOfType<TextCallOk<NoveltyV1>>().Draft.ShouldBeSameAs(scores);

        var call = rig.Fake.Requests.ShouldHaveSingleItem();
        Encoding.UTF8.GetByteCount(call.UserDataJson).ShouldBeLessThanOrEqualTo(newestOnly);

        var root = JsonDocument.Parse(call.UserDataJson).RootElement;
        root.GetProperty("past_episodes").GetArrayLength().ShouldBe(1, "the two oldest episodes did not fit");
        root.GetProperty("past_episodes")[0].GetProperty("index").GetInt32().ShouldBe(1);
        root.GetProperty("past_episodes")[0].GetProperty("title").GetString().ShouldBe("Tập 0", "the newest episode is the one kept");
        root.GetProperty("ideas").GetArrayLength().ShouldBe(2, "the batch is what the call is for");
        rig.Meter.Items.ShouldHaveSingleItem();
    }

    /// <summary>
    /// 200 long Vietnamese episodes: the corpus is trimmed to the ceiling and the call still goes out, so a series
    /// with a real history is judged on the newest of it rather than failing the run (F2/S2).
    /// </summary>
    [Fact]
    public async Task NoveltyJudge_TwoHundredLongVietnameseEpisodes_TrimsTheCorpusAndStillCalls()
    {
        var scores = Scores();
        var client = new FakeTextClient().EnqueueOk(TextStep.Novelty, scores);
        var request = Request() with
        {
            PastEpisodes =
            [
                .. Enumerable.Range(0, 200).Select(index => new CorpusEpisode(
                    Guid.CreateVersion7(),
                    $"Tập {index} về ngôi nhà bên sông",
                    string.Concat(Enumerable.Repeat("Một câu chuyện rất dài về ngôi nhà cũ bên bờ sông. ", 4)))),
            ],
        };
        var ceiling = PromptDataBudget.DefaultMaxDataObjectBytes;
        Encoding.UTF8.GetByteCount(NoveltyDataObjectJson.Build(request)).ShouldBeGreaterThan(ceiling);

        var rig = new Rig(client);

        var result = await rig.Judge.JudgeAsync(request, Ct);

        result.ShouldBeOfType<TextCallOk<NoveltyV1>>().Draft.ShouldBeSameAs(scores);

        var call = rig.Fake.Requests.ShouldHaveSingleItem();
        Encoding.UTF8.GetByteCount(call.UserDataJson).ShouldBeLessThanOrEqualTo(ceiling);

        var root = JsonDocument.Parse(call.UserDataJson).RootElement;
        var sent = root.GetProperty("past_episodes").GetArrayLength();
        sent.ShouldBeGreaterThan(0, "the corpus is trimmed, never dropped");
        sent.ShouldBeLessThan(200);
        root.GetProperty("past_episodes")[0].GetProperty("title").GetString().ShouldBe("Tập 0 về ngôi nhà bên sông");
        root.GetProperty("past_episodes")[sent - 1].GetProperty("index").GetInt32().ShouldBe(sent, "the 1-based indices cover what was sent");
        root.GetProperty("ideas").GetArrayLength().ShouldBe(2);
        rig.Meter.Items.ShouldHaveSingleItem();
    }

    /// <summary>
    /// A batch that is over the ceiling on its own cannot be made to fit by dropping anything the model may read,
    /// so the call is refused the way a provider refusal is reported: the checker degrades the batch on its
    /// lexical signals and the run survives the paid ideas call that came before it (F2/S2).
    /// </summary>
    [Fact]
    public async Task NoveltyJudge_CandidatesAloneOverTheCeiling_DegradesWithoutACall()
    {
        var client = new FakeTextClient();
        var rig = new Rig(client, maxDataObjectBytes: 64);

        var result = await rig.Judge.JudgeAsync(Request(), Ct);

        var refused = result.ShouldBeOfType<TextCallInvalidOutput<NoveltyV1>>();
        refused.RuleIds.ShouldBe([IdeaCallFaults.DataObjectTooLarge]);
        refused.RuleIds.ShouldBe(["data_object_too_large"], "a closed detail code, never a provider message");
        refused.Model.ShouldBe(rig.Estimator.NoveltyModel, "the refusal names the step's own model");
        rig.Fake.Requests.ShouldBeEmpty();
        rig.Meter.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoveltyJudge_WithoutAUsablePriceVersion_ThrowsBeforeTheCall()
    {
        var client = new FakeTextClient();
        var rig = new Rig(client, prices: new FakePriceCatalog { Missing = true });

        await Should.ThrowAsync<InvalidOperationException>(async () => await rig.Judge.JudgeAsync(Request(), Ct));

        rig.Fake.Requests.ShouldBeEmpty();
        rig.Meter.Items.ShouldBeEmpty();
    }

    private static NoveltyV1 Scores() => new([new NoveltyScoreV1(0, null, 0.10m, 0.90m, 0.80m, "fresh")]);

    private static NoveltyJudgeRequest Request(string priceVersion = "1") =>
        new(
            new RunContext(
                Guid.Parse("0197a000-0000-7000-8000-0000000000ab"),
                Guid.Parse("0197a000-0000-7000-8000-000000000001"),
                Guid.Parse("0197a000-0000-7000-8000-0000000000ac"),
                null,
                new ReservationId(Guid.Parse("0197a000-0000-7000-8000-0000000000ad")),
                GenerationTrigger.Autopilot,
                1,
                priceVersion),
            "vi",
            "ghost",
            [
                new IdeaCandidate("Một đêm mưa", "A logline.", "A hook.", "An old house.", Structure, ["setup", "clue", "twist"]),
                new IdeaCandidate("Căn nhà cuối hành lang", "Another logline.", "Another hook.", "A flat.", Structure, ["setup", "clue", "twist"]),
            ],
            [new CorpusEpisode(Guid.Parse("0197a000-0000-7000-8000-0000000000ae"), "Episode one", "A summary.")]);

    private static IConfiguration Configuration(int maxDataObjectBytes) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [PromptDataBudget.ConfigurationKey] = maxDataObjectBytes.ToString(CultureInfo.InvariantCulture),
            })
            .Build();

    private static PriceVersion Version(int number, int noveltyMaxOutput = PriceLimits.DefaultNoveltyMaxOutput)
    {
        var activeFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rates = new PriceVersionRates(
            new Dictionary<string, PriceRates>(StringComparer.Ordinal) { ["gpt-6-luna"] = new(0.10m, 0.01m, 0.125m, 0.50m) },
            new Dictionary<string, ImagePriceRate>(),
            null).ToJson();
        var limits = PriceVersionJson.WriteLimits(PriceLimits.D4Defaults with { NoveltyMaxOutput = noveltyMaxOutput });

        return PriceVersion.Create(number, activeFrom, rates, limits, $"test-version-{number}", activeFrom);
    }

    /// <summary>
    /// One assembled judge plus the seams a test asserts on: a scripted client, a stub prompt catalog, a
    /// one-version price catalog (the bound it meters at and the cap it sends) and a recording meter.
    /// </summary>
    private sealed class Rig
    {
        internal Rig(
            ITextClient client,
            int noveltyMaxOutput = PriceLimits.DefaultNoveltyMaxOutput,
            int maxDataObjectBytes = PromptDataBudget.DefaultMaxDataObjectBytes,
            FakePriceCatalog? prices = null,
            string? stepPrompt = null)
        {
            Client = client;
            Stubs = new StubCatalog(stepPrompt ?? StubCatalog.Prompt);
            Prices = prices ?? new FakePriceCatalog();
            Prices.Active = Version(1, noveltyMaxOutput);
            Config = Configuration(maxDataObjectBytes);
            Meter = new RecordingMeter();
            Estimator = new IdeaCostEstimator(Prices, Stubs, Config);
            Judge = new OpenAiIdeaNoveltyJudge(Client, Stubs, Config, Meter, Estimator);
        }

        internal ITextClient Client { get; }

        internal StubCatalog Stubs { get; }

        internal IConfiguration Config { get; }

        internal FakePriceCatalog Prices { get; }

        internal RecordingMeter Meter { get; }

        internal IdeaCostEstimator Estimator { get; }

        internal OpenAiIdeaNoveltyJudge Judge { get; }

        /// <summary>The same client, as the scripted type whose recorded requests the tests read.</summary>
        internal FakeTextClient Fake => (FakeTextClient)Client;
    }

    /// <summary>A prompt catalog whose novelty files are canned strings; the real one is 02.2.3-T1's.</summary>
    private sealed class StubCatalog : IPromptCatalog
    {
        internal const string Prompt = "NOVELTY-SYSTEM-PROMPT. Write in {{language_name}}.";

        internal static readonly byte[] SchemaBytes = Encoding.UTF8.GetBytes("""{"type":"object","$id":"novelty.v1"}""");

        private readonly string _stepPrompt;

        public StubCatalog(string stepPrompt = Prompt) => _stepPrompt = stepPrompt;

        internal PromptVersionParts? Parts { get; private set; }

        public string Release => "test";

        public IReadOnlyDictionary<string, PromptFileInfo> Files { get; } =
            new Dictionary<string, PromptFileInfo>(StringComparer.Ordinal);

        public string GetSystemPrompt() => Prompt;

        public string GetPreset(Topic topic, Language language) => Prompt;

        public string GetShared(PromptFile file, Language language) => Prompt;

        public string GetStepPrompt(PromptStep promptStep) => _stepPrompt;

        public AttestationText GetAttestation(Language language) => new(string.Empty, string.Empty);

        public ReadOnlyMemory<byte> GetSchema(string name) => SchemaBytes;

        public string BuildPromptVersion(PromptVersionParts parts)
        {
            Parts = parts;

            return "rel=test";
        }
    }

    /// <summary>The one price version the estimator reads, addressed by the run's own version number.</summary>
    private sealed class FakePriceCatalog : IPriceCatalog
    {
        internal PriceVersion Active { get; set; } = Version(1);

        /// <summary>The version the run names does not exist: the estimator's typed `price_catalog_empty` path.</summary>
        internal bool Missing { get; set; }

        public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct) =>
            Task.FromException<ErrorOr<PriceVersion>>(new NotSupportedException());

        public Task<PriceVersion> GetAsync(int version, CancellationToken ct) =>
            Missing
                ? Task.FromException<PriceVersion>(new KeyNotFoundException($"no price version {version}"))
                : Task.FromResult(Active);
    }

    /// <summary>A client whose call is cancelled by its caller, dispatched or not (the SEC-2 seam).</summary>
    private sealed class CancellingTextClient(bool dispatch = false) : ITextClient
    {
        public ValueTask<TextCallResult<T>> CallAsync<T>(TextCallRequest req, CancellationToken ct)
            where T : class
        {
            if (dispatch)
            {
                req.MarkDispatched(FakeTextClient.Model);
            }

            return ValueTask.FromException<TextCallResult<T>>(new OperationCanceledException(ct));
        }
    }

    /// <summary>Captures the usage items the judge records and the token it records them with.</summary>
    private sealed class RecordingMeter : IUsageMeter
    {
        internal List<UsageItem> Items { get; } = [];

        internal List<CancellationToken> Tokens { get; } = [];

        public Task RecordAsync(UsageItem item, CancellationToken ct)
        {
            Items.Add(item);
            Tokens.Add(ct);

            return Task.CompletedTask;
        }
    }
}
