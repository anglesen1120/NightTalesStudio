using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Length;
using NightTales.Domain.Stories.Prompts;
using NightTales.Domain.Stories.Structure;
using NightTales.TestKit.Fixtures;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories.Writing;

/// <summary>
/// 02.2.3-T2 QA: negative, boundary and exploratory cases the implementer's suite leaves open — containment
/// of every fixture in every slot at once, raw-wire escaping, null/empty handling, determinism.
/// </summary>
public sealed class PromptAssemblerQaTests
{
    private const string PresetKey = "story/ghost.vi.v1.md";

    private static readonly LengthTarget ViStandard = new(1300, 2080, 20, 32, 6000, 2500, 6000);

    private static readonly AssemblerContext Context = new(Topic.Ghost, Language.Vi, PresetKey, 6000);

    private static readonly string[] Languages = ["vi", "en"];

    private static readonly string[] Fields = ["brief", "bible.summary", "facts[0].statement", "avoid_plots[0]", "structure_plan.required.opening"];

    private static readonly string[] AllSlots =
        ["/brief", "/bible/episode_summaries/0/summary", "/facts/0/statement", "/avoid_plots/0", "/structure_plan/required/opening"];

    public static TheoryData<string> FixtureIds()
    {
        var data = new TheoryData<string>();
        foreach (var id in InjectionFixture.Ids())
        {
            data.Add(id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FixtureIds))]
    public void QA_Fixture_IsWellFormed(string id)
    {
        var fixture = InjectionFixture.Load(id);

        fixture.Id.ShouldBe(id);
        fixture.Canary.ShouldStartWith("NTS-CANARY-");
        fixture.Value.ShouldContain(fixture.Canary);
        fixture.Expect.SchemaValid.ShouldBeTrue();
        fixture.Expect.NoPayloadEcho.ShouldBeTrue();
        fixture.Expect.MustNotContain.ShouldNotBeEmpty();
        fixture.Expect.MustNotContain.ShouldAllBe(s => !string.IsNullOrWhiteSpace(s));
        Languages.ShouldContain(fixture.Language);
        Fields.ShouldContain(fixture.Field);
    }

    [Fact]
    public void QA_Fixture_LongBriefIsOver1000AndCoversTheTask5000Chars()
    {
        var fixture = InjectionFixture.Load("long-brief-truncation");

        fixture.Field.ShouldBe("brief");
        fixture.Value.Length.ShouldBeGreaterThanOrEqualTo(5000);
    }

    /// <summary>
    /// The hostile text goes into every untrusted slot at once; no property may appear, vanish or repeat,
    /// no control value may move, and the canary may be found only at those slots.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixtureIds))]
    public void QA_Fixture_InAllSlotsAtOnce_ChangesNoStructureNoControlValueNoSystemPrompt(string id)
    {
        var fixture = InjectionFixture.Load(id);
        var context = new AssemblerContext(Topic.Ghost, fixture.Language == "vi" ? Language.Vi : Language.En, PresetKey, 6000);

        var baseline = Assemble(AllSlotsRequest("benign"), context);
        var attacked = Assemble(AllSlotsRequest(fixture.Value), context);

        TopLevelNames(attacked.UserDataJson).ShouldBe(TopLevelNames(baseline.UserDataJson));
        PropertyNameSequence(attacked.UserDataJson).ShouldBe(PropertyNameSequence(baseline.UserDataJson));

        using var doc = JsonDocument.Parse(attacked.UserDataJson);
        using var reference = JsonDocument.Parse(baseline.UserDataJson);
        doc.RootElement.GetProperty("task").GetString().ShouldBe("write_story");
        doc.RootElement.GetProperty("length").GetRawText().ShouldBe(reference.RootElement.GetProperty("length").GetRawText());

        var truncatedBrief = fixture.Value.Length > 1000 ? fixture.Value[..1000] : fixture.Value;
        foreach (var path in AllSlots)
        {
            var value = At(doc.RootElement, path).GetString();
            value.ShouldBe(path == "/brief" ? truncatedBrief : fixture.Value, path);
        }

        attacked.SystemPrompt.ShouldBe(baseline.SystemPrompt);
        attacked.SystemPrompt.ShouldNotContain(fixture.Canary);
        attacked.PromptVersion.ShouldBe(baseline.PromptVersion);
        attacked.PromptCacheKey.ShouldBe(baseline.PromptCacheKey);
    }

    /// <summary>The wire form stays JSON-safe: no raw HTML-sensitive or control characters leave the serialiser.</summary>
    [Theory]
    [MemberData(nameof(FixtureIds))]
    public void QA_Fixture_RawWire_HasNoUnescapedHtmlSensitiveOrControlCharacters(string id)
    {
        var fixture = InjectionFixture.Load(id);
        var raw = Assemble(AllSlotsRequest(fixture.Value)).UserDataJson;

        raw.ShouldNotContain("<");
        raw.ShouldNotContain(">");
        raw.ShouldNotContain("&");
        raw.Any(c => c < 0x20).ShouldBeFalse("raw control character (newline/tab) on the wire");
        raw.ShouldNotContain("<|im_start|>");
        raw.ShouldNotContain("```" + "\n");
    }

    [Fact]
    public void QA_Determinism_SameLogicalInput_IsByteIdenticalAcrossFreshIdsAndInstances()
    {
        var first = new PromptAssembler(new QaCatalog(), NullLogger<PromptAssembler>.Instance)
            .Assemble(AllSlotsRequest("x {{brief}} \"q\" Ngôi nhà"), Context);
        var second = new PromptAssembler(new QaCatalog(), NullLogger<PromptAssembler>.Instance)
            .Assemble(AllSlotsRequest("x {{brief}} \"q\" Ngôi nhà"), Context);

        second.ShouldBe(first);
        second.UserDataJson.ShouldBe(first.UserDataJson);
        Utf8Bytes(second.SystemPrompt).ShouldBe(Utf8Bytes(first.SystemPrompt));
    }

    [Fact]
    public void QA_Determinism_RepeatedCallsOnOneAssembler_AreIdentical()
    {
        var assembler = new PromptAssembler(new QaCatalog(), NullLogger<PromptAssembler>.Instance);
        var request = AllSlotsRequest("repeat");

        var results = Enumerable.Range(0, 5).Select(_ => assembler.Assemble(request, Context)).ToList();

        results.Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public void QA_Determinism_DataObjectPropertyOrderIsStable()
    {
        var names = PropertyNameSequence(Assemble(AllSlotsRequest("o", attempt: 2, violated: ["r1"])).UserDataJson);

        names.ShouldBe(
            [
                "task", "brief", "length", "tier", "tokens", "segments", "units_per_segment", "target_tokens",
                "bible", "version", "recurring_characters", "places", "rules", "episode_summaries", "title", "summary", "truncated",
                "facts", "id", "statement", "sources", "avoid_plots",
                "structure_plan", "required", "opening", "forbidden", "corrective_note", "violated_rules",
            ]);
    }

    [Fact]
    public void QA_Null_BibleWithNullMembers_OmitsThemAndWritesNoNullLiteral()
    {
        var bible = new BibleSnapshot(3, null, [new BibleCharacterSnapshot("Lan", "A ghost")], [new BiblePlaceSnapshot("Well", "Old")], ["rule"], [new BibleEpisodeSummarySnapshot("T", null)], true);

        var json = Assemble(Request(bible: bible)).UserDataJson;

        json.ShouldNotContain("null");
        using var doc = JsonDocument.Parse(json);
        var b = doc.RootElement.GetProperty("bible");
        b.TryGetProperty("narrator", out _).ShouldBeFalse();
        b.GetProperty("recurring_characters")[0].GetProperty("name").GetString().ShouldBe("Lan");
        b.GetProperty("episode_summaries")[0].TryGetProperty("summary", out _).ShouldBeFalse();
        b.GetProperty("truncated").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void QA_Null_NarratorNullMembers_AreOmitted()
    {
        var bible = new BibleSnapshot(1, new BibleNarratorSnapshot(null, "Calm", null), [], [], [], [], false);

        using var doc = JsonDocument.Parse(Assemble(Request(bible: bible)).UserDataJson);

        doc.RootElement.GetProperty("bible").GetProperty("narrator").EnumerateObject().Select(p => p.Name).ShouldBe(["persona"]);
    }

    [Fact]
    public void QA_Null_EverythingOptionalAbsent_ProducesExactlyTheStableShape()
    {
        using var doc = JsonDocument.Parse(Assemble(Request()).UserDataJson);

        doc.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(["task", "brief", "length", "facts", "avoid_plots"]);
        doc.RootElement.GetProperty("facts").GetArrayLength().ShouldBe(0);
        doc.RootElement.GetProperty("avoid_plots").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void QA_Null_StructurePlanWithNoConstraints_SerialisesEmptyObjectsNotNulls()
    {
        var plan = new StructurePlan(new StructureRequirements(null, null, null, null), new StructureForbidden([], [], [], []));

        var json = Assemble(Request(episode: Episode(structure: plan))).UserDataJson;

        json.ShouldNotContain("null");
        using var doc = JsonDocument.Parse(json);
        var structure = doc.RootElement.GetProperty("structure_plan");
        structure.GetProperty("required").EnumerateObject().ShouldBeEmpty();
        structure.GetProperty("forbidden").EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    public void QA_Null_EmptyBrief_IsSentAsEmptyStringNotOmitted()
    {
        using var doc = JsonDocument.Parse(Assemble(Request(episode: Episode(""))).UserDataJson);

        doc.RootElement.GetProperty("brief").GetString().ShouldBe("");
    }

    [Fact]
    public void QA_Null_FactWithEmptySourceAliases_KeepsEmptySourcesArray()
    {
        using var doc = JsonDocument.Parse(Assemble(Request(facts: [new FactForWriter("f1", "S", [])])).UserDataJson);

        doc.RootElement.GetProperty("facts")[0].GetProperty("sources").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void QA_Corrective_Attempt3_IsNotMistakenForAttempt2()
    {
        using var doc = JsonDocument.Parse(Assemble(Request(attempt: 3, violated: ["length_out_of_range"])).UserDataJson);

        doc.RootElement.TryGetProperty("corrective_note", out _).ShouldBeFalse();
    }

    [Fact]
    public void QA_Corrective_Attempt2WithEmptyRuleList_SendsEmptyArray()
    {
        using var doc = JsonDocument.Parse(Assemble(Request(attempt: 2, violated: [])).UserDataJson);

        doc.RootElement.GetProperty("corrective_note").GetProperty("violated_rules").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void QA_Truncation_1001Chars_CutsToExactly1000AndLogsOnce()
    {
        var logger = new CountingLogger();
        var brief = new string('x', 1001);

        var prompt = new PromptAssembler(new QaCatalog(), logger).Assemble(Request(episode: Episode(brief)), Context);

        using var doc = JsonDocument.Parse(prompt.UserDataJson);
        doc.RootElement.GetProperty("brief").GetString()!.Length.ShouldBe(1000);
        logger.Messages.ShouldHaveSingleItem().ShouldContain("1001");
    }

    [Fact]
    public void QA_Truncation_PairEndingExactlyAtLimit_IsKept()
    {
        var brief = new string('a', 998) + "\U0001F47B" + "tail";

        using var doc = JsonDocument.Parse(Assemble(Request(episode: Episode(brief))).UserDataJson);

        var cut = doc.RootElement.GetProperty("brief").GetString()!;
        cut.Length.ShouldBe(1000);
        cut.ShouldEndWith("\U0001F47B");
    }

    [Fact]
    public void QA_Truncation_CutBriefNeverLeavesLoneSurrogate()
    {
        var brief = new string('a', 999) + "\U0001F47B" + "tail";

        var cut = JsonDocument.Parse(Assemble(Request(episode: Episode(brief))).UserDataJson).RootElement.GetProperty("brief").GetString()!;

        cut.Any(char.IsSurrogate).ShouldBeFalse();
    }

    [Fact]
    public void QA_Truncation_LogCarriesLengthNeverText()
    {
        var logger = new CountingLogger();
        var brief = "TOPSECRETPAYLOAD" + new string('z', 2000);

        new PromptAssembler(new QaCatalog(), logger).Assemble(Request(episode: Episode(brief)), Context);

        logger.Messages.ShouldHaveSingleItem().ShouldNotContain("TOPSECRET");
        logger.Messages[0].ShouldContain((16 + 2000).ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void QA_Placeholders_InUntrustedSlots_StayVerbatimInDataAndAbsentFromSystem()
    {
        const string Hostile = "{{language_name}}{{token_range}}{{structure_vocab}}{{unknown_thing}}";
        var prompt = Assemble(AllSlotsRequest(Hostile));

        prompt.SystemPrompt.ShouldNotContain("{{unknown_thing}}");
        prompt.SystemPrompt.ShouldNotContain(Hostile);
        using var doc = JsonDocument.Parse(prompt.UserDataJson);
        foreach (var path in AllSlots)
        {
            At(doc.RootElement, path).GetString().ShouldBe(Hostile, path);
        }
    }

    [Fact]
    public void QA_Placeholders_InSharedBlock_AreNotRescannedAndNotThrown()
    {
        var catalog = new QaCatalog { Keyword = "see {{language_name}} and {{brief}} and {{nonsense}}" };

        var prompt = new PromptAssembler(catalog, NullLogger<PromptAssembler>.Instance).Assemble(Request(), Context);

        prompt.SystemPrompt.ShouldContain("see {{language_name}} and {{brief}} and {{nonsense}}");
    }

    [Fact]
    public void QA_Placeholders_UnknownInSystemPrompt_Throws()
    {
        var catalog = new QaCatalog { Writer = "writer {{brief}}" };

        Should.Throw<InvalidOperationException>(
            () => new PromptAssembler(catalog, NullLogger<PromptAssembler>.Instance).Assemble(Request(), Context));
    }

    [Fact]
    public void QA_Request_NullArguments_Throw()
    {
        var assembler = new PromptAssembler(new QaCatalog(), NullLogger<PromptAssembler>.Instance);

        Should.Throw<ArgumentNullException>(() => assembler.Assemble(null!, Context));
        Should.Throw<ArgumentNullException>(() => assembler.Assemble(Request(), null!));
    }

    [Fact]
    public void QA_Request_UnknownTierOrLanguage_Throws()
    {
        var assembler = new PromptAssembler(new QaCatalog(), NullLogger<PromptAssembler>.Instance);

        Should.Throw<ArgumentOutOfRangeException>(() => assembler.Assemble(Request(episode: Episode(tier: (LengthTier)99)), Context));
        Should.Throw<ArgumentOutOfRangeException>(() => assembler.Assemble(Request(), new AssemblerContext(Topic.Ghost, (Language)99, PresetKey, 6000)));
    }

    [Fact]
    public void QA_CacheKey_UsesPresetStemForEveryTopicAndLanguage()
    {
        var assembler = new PromptAssembler(new QaCatalog(), NullLogger<PromptAssembler>.Instance);

        assembler.Assemble(Request(), new AssemblerContext(Topic.TrueCrime, Language.En, "story/true_crime.en.v1.md", 6000))
            .PromptCacheKey.ShouldBe("writer:true_crime.en.v1");
    }

    [Fact]
    public void QA_Length_BoundaryRoundingIsHalfToEven()
    {
        // target = (1000 + 1001) / 2 = 1000.5 -> 1000 (even); units = 1000 / 3 = 333.33 -> 333
        using var a = JsonDocument.Parse(Assemble(Request(length: new LengthTarget(1000, 1001, 3, 4, 6000, 2500, 6000))).UserDataJson);
        a.RootElement.GetProperty("length").GetProperty("target_tokens").GetInt32().ShouldBe(1000);
        a.RootElement.GetProperty("length").GetProperty("units_per_segment").GetInt32().ShouldBe(333);

        // target = (1001 + 1002) / 2 = 1001.5 -> 1002 (even); units = 1002 / 4 = 250.5 -> 250 (even)
        using var b = JsonDocument.Parse(Assemble(Request(length: new LengthTarget(1001, 1002, 4, 5, 6000, 2500, 6000))).UserDataJson);
        b.RootElement.GetProperty("length").GetProperty("target_tokens").GetInt32().ShouldBe(1002);
        b.RootElement.GetProperty("length").GetProperty("units_per_segment").GetInt32().ShouldBe(250);
    }

    private static byte[] Utf8Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private static JsonElement At(JsonElement root, string pointer)
    {
        var current = root;
        foreach (var segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.ValueKind == JsonValueKind.Array
                ? current[int.Parse(segment, CultureInfo.InvariantCulture)]
                : current.GetProperty(segment);
        }

        return current;
    }

    /// <summary>Top-level property names as read from the raw token stream, so a duplicate key cannot hide.</summary>
    private static List<string> TopLevelNames(string json)
    {
        var names = new List<string>();
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
            {
                names.Add(reader.GetString()!);
            }
        }

        return names;
    }

    /// <summary>Every property name in document order, duplicates included.</summary>
    private static List<string> PropertyNameSequence(string json)
    {
        var names = new List<string>();
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                names.Add(reader.GetString()!);
            }
        }

        return names;
    }

    private static AssembledPrompt Assemble(StoryWriteRequest req, AssemblerContext? context = null) =>
        new PromptAssembler(new QaCatalog(), NullLogger<PromptAssembler>.Instance).Assemble(req, context ?? Context);

    private static EpisodeSnapshot Episode(string brief = "A brief", LengthTier tier = LengthTier.Standard, StructurePlan? structure = null) =>
        new(Guid.NewGuid(), brief, tier, structure);

    private static StoryWriteRequest AllSlotsRequest(string payload, int attempt = 1, IReadOnlyList<string>? violated = null) =>
        Request(
            episode: Episode(
                payload,
                structure: new StructurePlan(new StructureRequirements(null, null, payload, null), new StructureForbidden([], [], [], []))),
            bible: new BibleSnapshot(1, null, [], [], [], [new BibleEpisodeSummarySnapshot("Episode 1", payload)], false),
            facts: [new FactForWriter("f1", payload, ["s1"])],
            avoid: [payload],
            attempt: attempt,
            violated: violated);

    private static StoryWriteRequest Request(
        EpisodeSnapshot? episode = null,
        LengthTarget? length = null,
        BibleSnapshot? bible = null,
        IReadOnlyList<FactForWriter>? facts = null,
        IReadOnlyList<string>? avoid = null,
        int attempt = 1,
        IReadOnlyList<string>? violated = null) =>
        new(
            new RunContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new ReservationId(Guid.NewGuid()), GenerationTrigger.User, attempt, "1"),
            new SeriesSnapshot(Guid.NewGuid(), Topic.Ghost, Language.Vi),
            episode ?? Episode(),
            length ?? ViStandard,
            bible,
            facts,
            avoid ?? [],
            violated);

    private sealed class QaCatalog : IPromptCatalog
    {
        public string Writer { get; init; } = "WRITER SYSTEM v1";

        public string Keyword { get; init; } = "KEYWORD-RULES-BLOCK";

        public string Release => "2026.10.1";

        public IReadOnlyDictionary<string, PromptFileInfo> Files { get; } = new Dictionary<string, PromptFileInfo>();

        public string GetSystemPrompt() => Writer;

        public string GetPreset(Topic topic, Language language) =>
            "PRESET {{language_name}}\nSCALARS {{language_name}}|{{tier_minutes}}|{{token_range}}|{{segment_range}}\n" +
            "K {{keyword_rules}}\nE {{ear_rules}}\nO {{overused_phrases}}\nV {{structure_vocab}}";

        public string GetShared(PromptFile file, Language language) => file switch
        {
            PromptFile.KeywordRules => Keyword,
            PromptFile.EarRules => "EAR-RULES-BLOCK",
            PromptFile.Overused => "OVERUSED-BLOCK",
            PromptFile.StructureVocab => "{\"sub_genre\":[\"haunted_house\"]}",
            _ => throw new InvalidOperationException(file.ToString()),
        };

        public string GetStepPrompt(PromptStep promptStep) => throw new NotSupportedException();

        public ReadOnlyMemory<byte> GetSchema(string name) => throw new NotSupportedException();

        public string BuildPromptVersion(PromptVersionParts parts) => $"version({parts.SystemKey}|{parts.PresetKey}|{parts.SchemaKey})";
    }

    private sealed class CountingLogger : Microsoft.Extensions.Logging.ILogger<PromptAssembler>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
