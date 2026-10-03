using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Money;
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
using NightTales.Infrastructure.Prompts;
using NightTales.TestKit.Fixtures;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Prompts;

/// <summary>
/// 02.2.3-T2 QA: the assembler against the real embedded prompt tree (all topic × language presets), so a
/// placeholder or data-object key that the shipped prompts do not know is caught here and not at the first live call.
/// </summary>
public sealed class PromptAssemblerRealCatalogQaTests
{
    private static readonly PromptCatalog Catalog = new(new EmbeddedPromptResourceSource());

    private static readonly LengthTarget Target = new(1300, 2080, 20, 32, 6000, 2500, 6000);

    public static TheoryData<Topic, Language> Cells()
    {
        var data = new TheoryData<Topic, Language>();
        foreach (var topic in Enum.GetValues<Topic>())
        {
            foreach (var language in Enum.GetValues<Language>())
            {
                data.Add(topic, language);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void QA_RealCatalog_EveryPresetAssemblesWithNoPlaceholderLeftOver(Topic topic, Language language)
    {
        var prompt = new PromptAssembler(Catalog, NullLogger<PromptAssembler>.Instance).Assemble(Request(topic, language, "brief"), Context(topic, language));

        prompt.SystemPrompt.ShouldNotContain("{{");
        prompt.SystemPrompt.ShouldStartWith(Catalog.GetSystemPrompt().Split("{{")[0]);
        prompt.PromptVersion.ShouldContain("sys=writer.v1");
        prompt.PromptVersion.ShouldContain("schema=story.v1");
        prompt.PromptCacheKey.ShouldStartWith("writer:");
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void QA_RealCatalog_SharedBlocksAppearOncePerPresetThatNamesThem(Topic topic, Language language)
    {
        var preset = Catalog.GetPreset(topic, language);
        var system = new PromptAssembler(Catalog, NullLogger<PromptAssembler>.Instance).Assemble(Request(topic, language, "b"), Context(topic, language)).SystemPrompt;

        foreach (var (placeholder, file) in new[]
        {
            ("{{keyword_rules}}", PromptFile.KeywordRules),
            ("{{ear_rules}}", PromptFile.EarRules),
            ("{{overused_phrases}}", PromptFile.Overused),
            ("{{structure_vocab}}", PromptFile.StructureVocab),
        })
        {
            var uses = Count(preset, placeholder);
            Count(system, Catalog.GetShared(file, language)).ShouldBe(uses, $"{topic}.{language} {placeholder}");
        }
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void QA_RealCatalog_SystemPromptIsIndependentOfEveryUntrustedInput(Topic topic, Language language)
    {
        var assembler = new PromptAssembler(Catalog, NullLogger<PromptAssembler>.Instance);
        var baseline = assembler.Assemble(Request(topic, language, "benign"), Context(topic, language));

        foreach (var fixture in InjectionFixture.LoadAll())
        {
            var attacked = assembler.Assemble(Request(topic, language, fixture.Value), Context(topic, language));

            attacked.SystemPrompt.ShouldBe(baseline.SystemPrompt, fixture.Id);
            attacked.SystemPrompt.ShouldNotContain(fixture.Canary);
        }
    }

    /// <summary>D6 snake_case claim: every data-object key the model is told to follow is spelled the way the shipped prompts spell it.</summary>
    [Fact]
    public void QA_RealCatalog_ControlKeysOfTheDataObjectAreNamedInTheWriterSystemPrompt()
    {
        var writer = Catalog.GetSystemPrompt();

        foreach (var key in new[] { "task", "length", "structure_plan", "facts", "avoid_plots", "brief", "bible", "length.units_per_segment", "length.target_tokens" })
        {
            writer.ShouldContain(key.Split('.')[^1], customMessage: $"writer.v1.md does not mention '{key}'");
        }

        writer.ShouldNotContain("avoidPlots");
        writer.ShouldNotContain("structurePlan");
        writer.ShouldNotContain("unitsPerSegment");
    }

    [Fact]
    public void QA_RealCatalog_DataObjectKeysAreExactlyTheD6Set()
    {
        var req = Request(Topic.Ghost, Language.Vi, "b") with
        {
            Bible = new BibleSnapshot(1, null, [], [], [], [], false),
            Facts = [new FactForWriter("f1", "S", ["s1"])],
            AvoidPlots = ["p"],
        };
        using var doc = JsonDocument.Parse(new PromptAssembler(Catalog, NullLogger<PromptAssembler>.Instance).Assemble(req, Context(Topic.Ghost, Language.Vi)).UserDataJson);

        doc.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(["task", "brief", "length", "bible", "facts", "avoid_plots"]);
        doc.RootElement.GetProperty("length").EnumerateObject().Select(p => p.Name)
            .ShouldBe(["tier", "tokens", "segments", "units_per_segment", "target_tokens"]);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static AssemblerContext Context(Topic topic, Language language)
    {
        var stem = topic switch { Topic.Ghost => "ghost", Topic.Spiritual => "spiritual", _ => "true_crime" };
        return new AssemblerContext(topic, language, $"story/{stem}.{(language == Language.Vi ? "vi" : "en")}.v1.md", 6000);
    }

    private static StoryWriteRequest Request(Topic topic, Language language, string brief) =>
        new(
            new RunContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new ReservationId(Guid.NewGuid()), GenerationTrigger.User, 1, "1"),
            new SeriesSnapshot(Guid.NewGuid(), topic, language),
            new EpisodeSnapshot(Guid.NewGuid(), brief, LengthTier.Standard, null),
            Target,
            null,
            null,
            [],
            null);
}
