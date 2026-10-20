using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Domain.Catalog;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Gates;

/// <summary>
/// <see cref="ScenePromptExpander"/> (02.8.2-T2, 03 README D1): one prompt per keyword in keyword order, built by
/// the shared <see cref="ScenePromptBuilder"/>, moderating the styled text without the platform negative clause
/// (03 CR-3) and never skipping a scene whose template broke the catalog rule.
/// </summary>
public sealed class ScenePromptExpanderTests
{
    /// <summary>A rule-valid template (03.1.1-T2), so <see cref="StyleTemplate.Create"/> accepts it.</summary>
    private const string Template = "Dark folklore ink illustration of {term}. Black ink wash on aged rice paper, muted grey tones.";

    [Fact]
    public void ScenePromptExpander_ReplacesTerm_ForEveryKeyword_InOrder()
    {
        var style = Style();
        var expander = new ScenePromptExpander(NullLogger<ScenePromptExpander>.Instance);

        var prompts = expander.Expand(style, [" cổng làng ", "con sông", "empty rocking chair"]);

        prompts.Count.ShouldBe(3);
        prompts[0].ShouldBe(Template.Replace("{term}", "cổng làng", StringComparison.Ordinal), "the builder trims the term");
        prompts[1].ShouldBe(Template.Replace("{term}", "con sông", StringComparison.Ordinal));
        prompts[2].ShouldBe(Template.Replace("{term}", "empty rocking chair", StringComparison.Ordinal));
        prompts.ShouldAllBe(prompt => !prompt.Contains("{term}", StringComparison.Ordinal));
    }

    [Fact]
    public void ScenePromptExpander_ModerationText_NeverContainsNegativeClause()
    {
        var style = Style();
        var expander = new ScenePromptExpander(NullLogger<ScenePromptExpander>.Instance);
        var keywords = new[] { "cổng làng", "con sông" };

        var prompts = expander.Expand(style, keywords);

        prompts.ShouldAllBe(prompt => !prompt.Contains(NegativeClause.Current, StringComparison.Ordinal));
        ScenePromptBuilder.Build(style, keywords[0]).Text.ShouldContain(
            NegativeClause.Current,
            Case.Sensitive,
            "the clause the image model receives is the platform's, never the moderated text");
    }

    [Fact]
    public void ScenePromptExpander_UnknownTemplateShape_SendsFullText()
    {
        const string NoPlaceholder =
            "Dark folklore ink illustration of an empty room. Black ink wash on aged rice paper, muted grey tones, weathered paper edges";
        var style = Style();

        // A row that lost its placeholder (a 03-side data error): the table CHECK and the seeder refuse it, but an
        // already stored row can reach the gate, and then the full template is still moderated once per keyword.
        style.UpdateFromCatalog(NoPlaceholder, null, null, active: true);
        var log = new RecordingLogger();
        var expander = new ScenePromptExpander(log);

        var prompts = expander.Expand(style, ["cổng làng", "con sông"]);

        prompts.ShouldBe([NoPlaceholder, NoPlaceholder]);
        log.Lines.ShouldNotBeEmpty("the broken row is reported, never silently skipped");
        log.Lines.ShouldContain(line => line.Contains(style.Id.ToString(), StringComparison.Ordinal));
        log.Lines.ShouldAllBe(line => !line.Contains("cổng làng", StringComparison.Ordinal));
    }

    /// <summary>
    /// A null keyword keeps its own position (C9): the expander emits <see cref="string.Empty"/> there instead of
    /// dropping the entry, so <c>scene:&lt;i&gt;</c> always names the keyword the owner edited — the gate skips a
    /// blank text, and a flag on "con sông" reads <c>scene:2</c>, not <c>scene:1</c>.
    /// </summary>
    [Fact]
    public void ScenePromptExpander_NullKeyword_KeepsTheSceneIndex()
    {
        var expander = new ScenePromptExpander(NullLogger<ScenePromptExpander>.Instance);

        var prompts = expander.Expand(Style(), ["cổng làng", null!, "con sông"]);

        prompts.Count.ShouldBe(3, "one entry per keyword, in keyword order");
        prompts[1].ShouldBe(string.Empty, "the null keyword holds its position behind a blank text");
        prompts[0].ShouldContain("cổng làng", Case.Sensitive);
        prompts[2].ShouldContain("con sông", Case.Sensitive);
    }

    [Fact]
    public void ScenePromptExpander_EmptyKeywordList_ExpandsNothing()
    {
        var expander = new ScenePromptExpander(NullLogger<ScenePromptExpander>.Instance);

        expander.Expand(Style(), []).ShouldBeEmpty();
    }

    private static StyleTemplate Style() => StyleTemplate.Create("dark-folklore-ink", Template);

    /// <summary>Captures every formatted log line plus the named values, which is what a log query would see (D19).</summary>
    private sealed class RecordingLogger : ILogger<ScenePromptExpander>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));

            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                Lines.AddRange(values.Select(value => value.Value?.ToString() ?? string.Empty));
            }
        }
    }
}
