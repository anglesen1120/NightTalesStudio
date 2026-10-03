using System.Text.Json;
using NightTales.Domain.Common;
using NightTales.Domain.Series;

namespace NightTales.Application.Abstractions.Prompts;

/// <summary>The one spelling of a preset's manifest key (02 README D5), shared by the catalog and the writer.</summary>
public static class StoryPresetKeys
{
    /// <summary>The snake_case spelling of a topic or language, the form a preset's file name uses.</summary>
    /// <param name="memberName">The enum member's name.</param>
    /// <returns>For example <c>true_crime</c>.</returns>
    public static string Spelling(string memberName) => JsonNamingPolicy.SnakeCaseLower.ConvertName(memberName);

    /// <summary>The preset key without directory or extension, for example <c>ghost.vi.v1</c>.</summary>
    /// <param name="topic">The narrative topic.</param>
    /// <param name="language">The content language.</param>
    /// <returns>The preset's name.</returns>
    public static string Name(Topic topic, Language language) =>
        $"{Spelling(topic.ToString())}.{Spelling(language.ToString())}.v1";

    /// <summary>The manifest key of a preset, for example <c>story/ghost.vi.v1.md</c>.</summary>
    /// <param name="topic">The narrative topic.</param>
    /// <param name="language">The content language.</param>
    /// <returns>The preset's key relative to <c>prompts/</c>.</returns>
    public static string For(Topic topic, Language language) => $"story/{Name(topic, language)}.md";
}
