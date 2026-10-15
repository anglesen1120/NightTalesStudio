using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.SeriesBible;
using NightTales.Application.Features.SeriesBible.Abstractions;
using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Prompts;
using NightTales.Domain.Stories.Structure;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Bible;
using NightTales.Infrastructure.OpenAI;
using NightTales.TestKit.Text;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.OpenAI;

/// <summary>
/// 02.6.3-T2: the bible-proposal step over a scripted <see cref="ITextClient"/> — the request shape (step,
/// schema, cache key, the ruled output cap), the one D6 data object (ids stripped, newest summaries, body cut
/// from the end), the untouched provider outcomes and the D14 usage row.
/// </summary>
public sealed class OpenAiBibleProposalGeneratorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GenerateAsync_UsesTheBibleStep_TheProposalSchema_AndTheRuledOutputCap()
    {
        var catalog = new StubCatalog();
        var draft = Draft();
        var client = new FakeTextClient().EnqueueOk(TextStep.Bible, draft);
        var generator = Generator(client, new OpenAiBibleProposalMetrics(), catalog: catalog);

        var result = await generator.GenerateAsync(Request(Body()), Ct);

        result.ShouldBeOfType<TextCallOk<BibleProposalV1>>().Draft.ShouldBeSameAs(draft);
        var call = client.Requests.ShouldHaveSingleItem();
        call.Step.ShouldBe(TextStep.Bible);
        call.SchemaName.ShouldBe(BibleProposalPrompts.SchemaName);
        call.Schema.ToArray().ShouldBe(StubCatalog.SchemaBytes);
        call.SystemPrompt.ShouldBe(StubCatalog.Prompt);
        call.PromptCacheKey.ShouldBe("bible:propose");

        // README T56: the cap is the price version's, carried by the bound, never a configuration key.
        call.MaxOutputTokens.ShouldBe(1_200);

        var parts = catalog.Parts.ShouldNotBeNull();
        parts.SystemKey.ShouldBe("bible/propose.v1.md");
        parts.SchemaKey.ShouldBe("schemas/bible-proposal.v1.json");
    }

    [Fact]
    public async Task GenerateAsync_TheDataObject_CarriesTheBibleAndTheEpisode_AndNoId()
    {
        var client = new FakeTextClient().EnqueueOk(TextStep.Bible, Draft());
        var generator = Generator(client, new OpenAiBibleProposalMetrics());

        await generator.GenerateAsync(Request(Body()), Ct);

        var json = client.Requests.ShouldHaveSingleItem().UserDataJson;
        var root = JsonDocument.Parse(json).RootElement;
        root.EnumerateObject().Select(property => property.Name).ShouldBe(["task", "language", "topic", "bible", "episode"]);
        root.GetProperty("task").GetString().ShouldBe("propose_bible_update");
        root.GetProperty("language").GetString().ShouldBe("vi");
        root.GetProperty("topic").GetString().ShouldBe("ghost");

        var bible = root.GetProperty("bible");
        bible.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["narrator", "recurring_characters", "places", "rules", "recent_episodes"]);
        bible.GetProperty("narrator").GetProperty("name").GetString().ShouldBe("The narrator");
        bible.GetProperty("rules").EnumerateArray().Select(rule => rule.GetString()).ShouldBe(["Never name a village."]);
        bible.GetProperty("recent_episodes").EnumerateArray().Select(episode => episode.GetProperty("title").GetString())
            .ShouldBe(["Newer episode", "Older episode"]);

        root.GetProperty("episode").EnumerateObject().Select(property => property.Name).ShouldBe(["title", "structure", "body"]);
        root.GetProperty("episode").GetProperty("title").GetString().ShouldBe("Episode one");

        // D6: no server id travels — not a character/place/rule id and not an episode id.
        json.ShouldNotContain("ch1");
        json.ShouldNotContain("pl1");
        json.ShouldNotContain("rl1");
        json.ShouldNotContain(OlderEpisodeId.ToString());
        json.ShouldNotContain(NewerEpisodeId.ToString());
        json.ShouldNotContain("schema_version");
    }

    [Fact]
    public async Task GenerateAsync_TheSummaryWindow_IsTheNewestTwo_AndDropsTheRest()
    {
        var client = new FakeTextClient().EnqueueOk(TextStep.Bible, Draft());
        var generator = Generator(client, new OpenAiBibleProposalMetrics(), maxSummaries: 1);

        await generator.GenerateAsync(Request(Body()), Ct);

        var episodes = JsonDocument.Parse(client.Requests.ShouldHaveSingleItem().UserDataJson).RootElement
            .GetProperty("bible").GetProperty("recent_episodes");
        episodes.EnumerateArray().Select(episode => episode.GetProperty("title").GetString()).ShouldBe(["Newer episode"]);
    }

    [Fact]
    public async Task GenerateAsync_CutsTheBodyFromTheEnd_AndCountsItOnTheFeatureMeter()
    {
        long measurements = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, events) =>
        {
            if (instrument.Name == OpenAiBibleProposalMetrics.BodyTruncatedTotalName)
            {
                events.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => Interlocked.Add(ref measurements, measurement));
        listener.Start();

        var client = new FakeTextClient().EnqueueOk(TextStep.Bible, Draft());
        var generator = Generator(client, new OpenAiBibleProposalMetrics(), maxStoryChars: 8);

        await generator.GenerateAsync(Request("0123456789"), Ct);

        JsonDocument.Parse(client.Requests.ShouldHaveSingleItem().UserDataJson).RootElement
            .GetProperty("episode").GetProperty("body").GetString().ShouldBe("01234567");
        measurements.ShouldBe(1);
    }

    [Fact]
    public async Task GenerateAsync_AShortBody_IsNotCounted()
    {
        long measurements = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, events) =>
        {
            if (instrument.Name == OpenAiBibleProposalMetrics.BodyTruncatedTotalName)
            {
                events.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => Interlocked.Add(ref measurements, measurement));
        listener.Start();

        var client = new FakeTextClient().EnqueueOk(TextStep.Bible, Draft());
        var generator = Generator(client, new OpenAiBibleProposalMetrics());

        await generator.GenerateAsync(Request("short"), Ct);

        measurements.ShouldBe(0);
    }

    [Fact]
    public async Task GenerateAsync_MetersOneUsageRow_UnderTheBibleProposalOperation()
    {
        var meter = new RecordingMeter();
        var usage = new TokenUsage(1_000, 200, 300, 120, 40);
        var client = new FakeTextClient().EnqueueOk(TextStep.Bible, Draft(), usage);
        var generator = Generator(client, new OpenAiBibleProposalMetrics(), meter);
        var request = Request(Body());

        await generator.GenerateAsync(request, Ct);

        var item = meter.Items.ShouldHaveSingleItem();
        item.ShouldSatisfyAllConditions(
            i => i.Operation.ShouldBe(BibleProposalOperations.BibleProposal),
            i => i.Provider.ShouldBe(TextCallResultUsageExtensions.OpenAiProvider),
            i => i.Model.ShouldBe(FakeTextClient.Model),
            i => i.UserId.ShouldBe(request.Run.UserId),
            i => i.SeriesId.ShouldBe(request.Run.SeriesId),
            i => i.GenerationRunId.ShouldBe(request.Run.GenerationRunId),
            i => i.EpisodeId.ShouldBe(request.Run.EpisodeId),
            i => i.ReservationId.ShouldBe(request.Run.Reservation.Value),
            // The provider reported usage, so the row carries it and is not an estimate; the settled cost is
            // still the D1 bound the run was admitted with (what the ledger settles the reservation against).
            i => i.InputTokens.ShouldBe(usage.InputTokens),
            i => i.OutputTokens.ShouldBe(usage.OutputTokens),
            i => i.Estimated.ShouldBeFalse(),
            i => i.CostUsd.ShouldBe(3m));
    }

    [Fact]
    public async Task GenerateAsync_ReturnsAProviderOutcomeUntouched()
    {
        var outcome = TextCallResult<BibleProposalV1>.InvalidOutput(
            FakeTextClient.Model,
            null,
            "r1",
            [TextCallInvalidOutput<BibleProposalV1>.SchemaInvalid]);
        var client = new FakeTextClient().Enqueue(TextStep.Bible, outcome);
        var generator = Generator(client, new OpenAiBibleProposalMetrics());

        var result = await generator.GenerateAsync(Request(Body()), Ct);

        result.ShouldBeSameAs(outcome);
        client.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task GenerateAsync_WithoutAnActivePriceVersion_ThrowsBeforeTheCall()
    {
        var estimator = new StubEstimator(bound: 3m) { Fails = true };
        var client = new FakeTextClient();
        var generator = Generator(client, new OpenAiBibleProposalMetrics(), new RecordingMeter(), estimator: estimator);

        await Should.ThrowAsync<InvalidOperationException>(async () => await generator.GenerateAsync(Request(Body()), Ct));

        client.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task GenerateAsync_CancelledAfterDispatch_RecordsTheBound_ThenRethrows()
    {
        var meter = new RecordingMeter();
        var generator = Generator(
            new CancellingTextClient(dispatch: true),
            new OpenAiBibleProposalMetrics(),
            meter);
        var request = Request(Body());

        await Should.ThrowAsync<OperationCanceledException>(async () => await generator.GenerateAsync(request, Ct));

        var item = meter.Items.ShouldHaveSingleItem();
        item.ShouldSatisfyAllConditions(
            i => i.Estimated.ShouldBeTrue("the response never arrived, so the row carries the D1 bound"),
            i => i.CostUsd.ShouldBe(3m),
            i => i.Operation.ShouldBe(BibleProposalOperations.BibleProposal),
            i => i.EpisodeId.ShouldBe(request.Run.EpisodeId));
        meter.Tokens.ShouldHaveSingleItem().CanBeCanceled.ShouldBeFalse("the settlement must survive the caller's cancellation");
    }

    [Fact]
    public async Task GenerateAsync_CancelledBeforeDispatch_RecordsNothing()
    {
        var meter = new RecordingMeter();
        var generator = Generator(new CancellingTextClient(dispatch: false), new OpenAiBibleProposalMetrics(), meter);

        await Should.ThrowAsync<OperationCanceledException>(async () => await generator.GenerateAsync(Request(Body()), Ct));

        meter.Items.ShouldBeEmpty();
    }

    private static readonly Guid OlderEpisodeId = Guid.Parse("0197a000-0000-7000-8000-0000000000a1");

    private static readonly Guid NewerEpisodeId = Guid.Parse("0197a000-0000-7000-8000-0000000000a2");

    private static OpenAiBibleProposalGenerator Generator(
        ITextClient client,
        OpenAiBibleProposalMetrics metrics,
        IUsageMeter? meter = null,
        StubEstimator? estimator = null,
        int maxSummaries = 2,
        int maxStoryChars = 24_000,
        StubCatalog? catalog = null) =>
        new(
            client,
            catalog ?? new StubCatalog(),
            Microsoft.Extensions.Options.Options.Create(new BibleOptions
            {
                Proposal = new BibleProposalOptions { MaxStoryChars = maxStoryChars },
                Prompt = new BiblePromptOptions { MaxSummaries = maxSummaries },
            }),
            meter ?? new RecordingMeter(),
            estimator ?? new StubEstimator(bound: 3m),
            metrics);

    private static BibleProposalRequest Request(string body) =>
        new(
            new RunContext(
                Guid.Parse("0197a000-0000-7000-8000-000000000001"),
                Guid.Parse("0197a000-0000-7000-8000-000000000002"),
                Guid.Parse("0197a000-0000-7000-8000-000000000003"),
                Guid.Parse("0197a000-0000-7000-8000-000000000004"),
                new ReservationId(Guid.Parse("0197a000-0000-7000-8000-000000000005")),
                GenerationTrigger.System,
                1,
                "1"),
            "vi",
            "ghost",
            Content(),
            Structure(),
            "Episode one",
            body);

    private static string Body() => new('b', 40);

    private static SeriesBibleContent Content() =>
        new(
            SeriesBibleContent.V1,
            new BibleNarrator("The narrator", "Calm and slow.", string.Empty),
            [new BibleCharacter("ch1", "Lan", "A widow.", null)],
            [new BiblePlace("pl1", "The well", "Behind the house.")],
            [new BibleRule("rl1", "Never name a village.")],
            [
                new BibleEpisodeSummary(OlderEpisodeId, "Older episode", "Older summary.", StoryStructureTags.Empty, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)),
                new BibleEpisodeSummary(NewerEpisodeId, "Newer episode", "Newer summary.", StoryStructureTags.Empty, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)),
            ]);

    private static StoryStructureTags Structure() =>
        new("haunted_house", "rural_house", "night", "revenant", "first_person", ["the hook", "the twist"]);

    private static BibleProposalV1 Draft() => new([new BibleProposalOpV1("add_rule", null, null, null, "A rule.", null)]);

    /// <summary>A prompt catalog whose two proposal files are canned strings; the real one is 02.2.3-T1's.</summary>
    private sealed class StubCatalog : IPromptCatalog
    {
        internal const string Prompt = "BIBLE-PROPOSAL-SYSTEM-PROMPT";

        internal static readonly byte[] SchemaBytes = Encoding.UTF8.GetBytes("""{"type":"object","additionalProperties":false,"required":["ops"]}""");

        internal PromptVersionParts? Parts { get; private set; }

        public string Release => "test";

        public IReadOnlyDictionary<string, PromptFileInfo> Files { get; } = new Dictionary<string, PromptFileInfo>(StringComparer.Ordinal);

        public string GetSystemPrompt() => Prompt;

        public string GetPreset(Topic topic, Language language) => Prompt;

        public string GetShared(PromptFile file, Language language) => Prompt;

        public string GetStepPrompt(PromptStep promptStep) => Prompt;

        public AttestationText GetAttestation(Language language) => new(string.Empty, string.Empty);

        public ReadOnlyMemory<byte> GetSchema(string name) => SchemaBytes;

        public string BuildPromptVersion(PromptVersionParts parts)
        {
            Parts = parts;

            return "rel=test";
        }
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

    /// <summary>The bound port: the ruled cap the generator sends, and the cost the row is written at.</summary>
    private sealed class StubEstimator(decimal bound) : IBibleProposalCostEstimator
    {
        internal bool Fails { get; init; }

        public Task<ErrorOr<UsageBound>> WorstCaseAsync(CancellationToken ct) =>
            Task.FromResult(Fails
                ? Error.Failure(ErrorCodes.PriceCatalogEmpty, "no active price version")
                : (ErrorOr<UsageBound>)new UsageBound(bound, 1_000, PriceLimits.DefaultBibleProposalMaxOutput));
    }
}

/// <summary>02.6.3-T2 plan §7.1 step 3: the body is cut from the end and never splits a surrogate pair.</summary>
public sealed class BibleProposalDataObjectJsonTests
{
    [Fact]
    public void TruncateBody_WithinTheCeiling_KeepsItWhole()
    {
        var (body, truncated) = BibleProposalDataObjectJson.TruncateBody("short", 5);

        body.ShouldBe("short");
        truncated.ShouldBeFalse();
    }

    [Fact]
    public void TruncateBody_LongerThanTheCeiling_KeepsTheOpening()
    {
        var (body, truncated) = BibleProposalDataObjectJson.TruncateBody("the opening carries the hook", 11);

        body.ShouldBe("the opening");
        truncated.ShouldBeTrue();
    }

    [Fact]
    public void TruncateBody_NeverSplitsASurrogatePair()
    {
        // A four-byte character is two UTF-16 units; cutting between them would leave half a pair.
        var body = string.Concat(Enumerable.Repeat("\U0001F600", 4));

        var (cut, truncated) = BibleProposalDataObjectJson.TruncateBody(body, 3);

        truncated.ShouldBeTrue();
        cut.Length.ShouldBe(2);
        cut.ShouldBe("\U0001F600");
        cut.Any(char.IsSurrogate).ShouldBeTrue();
    }

    [Fact]
    public void TruncateBody_NonPositiveCeiling_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => BibleProposalDataObjectJson.TruncateBody("body", 0));
}
