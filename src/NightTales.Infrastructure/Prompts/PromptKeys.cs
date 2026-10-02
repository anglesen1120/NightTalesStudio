using System.Text.Json;
using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Prompts;

namespace NightTales.Infrastructure.Prompts;

/// <summary>
/// The D5 key convention of the prompt catalog (02 README D5): how an enum member becomes the manifest
/// key of a file under <c>prompts/</c>.
/// </summary>
/// <remarks>
/// One place, because three rules are written in keys: the start-up gate requires a preset per
/// <c>topic × language</c> and reports it as <c>ghost.vi.v1</c>, and the catalog resolves a
/// <see cref="Topic"/>, a <see cref="Language"/>, a <see cref="PromptFile"/> or a
/// <see cref="PromptStep"/> to the very same key. Keys are relative to <c>prompts/</c> and use <c>/</c>
/// on every OS (02 README T27).
/// </remarks>
internal static class PromptKeys
{
    /// <summary>The shared writer system prompt every run's version names.</summary>
    internal const string SystemPrompt = "system/writer.v1.md";

    /// <summary>The one output schema every run is validated against.</summary>
    internal const string StorySchema = "schemas/story.v1.json";

    /// <summary>The structure vocabulary, the one shared block whose file is JSON.</summary>
    internal const string StructureVocab = "shared/structure-vocab.v1.json";

    private const string SchemaDirectory = "schemas/";
    private const string StoryDirectory = "story/";

    /// <summary>The snake_case spelling of a topic, the form its preset's file name uses.</summary>
    /// <param name="topic">The narrative topic.</param>
    /// <returns>For example <c>true_crime</c>.</returns>
    internal static string Spelling(Topic topic) => Snake(topic.ToString());

    /// <summary>The snake_case spelling of a language, the form its preset's file name uses.</summary>
    /// <param name="language">The content language.</param>
    /// <returns>For example <c>vi</c>.</returns>
    internal static string Spelling(Language language) => Snake(language.ToString());

    /// <summary>The preset key without directory or extension, the form a violation names it in.</summary>
    /// <param name="topic">The narrative topic.</param>
    /// <param name="language">The content language.</param>
    /// <returns>For example <c>ghost.vi.v1</c>.</returns>
    internal static string PresetName(Topic topic, Language language) =>
        $"{Spelling(topic)}.{Spelling(language)}.v1";

    /// <summary>The manifest key of a preset, for example <c>story/ghost.vi.v1.md</c>.</summary>
    /// <param name="topic">The narrative topic.</param>
    /// <param name="language">The content language.</param>
    /// <returns>The preset's key relative to <c>prompts/</c>.</returns>
    internal static string Preset(Topic topic, Language language) =>
        $"{StoryDirectory}{PresetName(topic, language)}.md";

    /// <summary>The manifest key of a schema file name, for example <c>story.v1.json</c>.</summary>
    /// <param name="name">The schema's file name.</param>
    /// <returns>The schema's key relative to <c>prompts/</c>.</returns>
    internal static string Schema(string name) => SchemaDirectory + name;

    /// <summary>Whether a manifest key names a file under <c>prompts/schemas/</c>.</summary>
    /// <param name="key">The key relative to <c>prompts/</c>.</param>
    /// <returns><see langword="true"/> for a schema.</returns>
    internal static bool IsSchema(string key) =>
        key.StartsWith(SchemaDirectory, StringComparison.Ordinal)
        && key.EndsWith(".json", StringComparison.Ordinal);

    /// <summary>The manifest key of one shared rule block.</summary>
    /// <param name="file">The shared block.</param>
    /// <param name="language">The content language; ignored by the two blocks that do not vary.</param>
    /// <returns>The block's key relative to <c>prompts/</c>.</returns>
    internal static string Shared(PromptFile file, Language language) => file switch
    {
        PromptFile.EarRules => $"shared/ear-rules.{Spelling(language)}.v1.md",
        PromptFile.Overused => $"shared/overused.{Spelling(language)}.v1.txt",
        PromptFile.KeywordRules => "shared/keyword-rules.v1.md",
        PromptFile.StructureVocab => StructureVocab,
        _ => throw new ArgumentOutOfRangeException(nameof(file), file, "Unknown shared prompt file."),
    };

    /// <summary>The manifest key of one generation step's prompt.</summary>
    /// <param name="step">The step.</param>
    /// <returns>The step prompt's key relative to <c>prompts/</c>.</returns>
    internal static string Step(PromptStep step) => step switch
    {
        PromptStep.Writer => SystemPrompt,
        PromptStep.Review => "review/v1.md",
        PromptStep.Revision => "revise/v1.md",
        PromptStep.Facts => "facts/v1.md",
        PromptStep.Bible => "bible/propose.v1.md",
        PromptStep.Ideas => "ideas/generate.v1.md",
        PromptStep.Novelty => "ideas/novelty.v1.md",
        PromptStep.Finder => "finder/v1.md",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Unknown prompt step."),
    };

    private static string Snake(string memberName) => JsonNamingPolicy.SnakeCaseLower.ConvertName(memberName);
}
