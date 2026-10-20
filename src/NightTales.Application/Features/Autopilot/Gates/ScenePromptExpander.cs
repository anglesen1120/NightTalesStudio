using Microsoft.Extensions.Logging;
using NightTales.Domain.Catalog;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// Turns a run's story keywords into the exact texts the content gate moderates (02.8.2-T2, 03 README D1): one
/// <see cref="ScenePrompt.ModerationText"/> per keyword, in keyword order, built by the one shared
/// <see cref="ScenePromptBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The negative clause is never moderated.</b> The builder appends the platform clause to
/// <see cref="ScenePrompt.Text"/> only; <see cref="ScenePrompt.ModerationText"/> is the styled template alone, so
/// a clause that scores on violence-adjacent categories cannot fail every episode by itself (03 CR-3, closed —
/// there is no strip setting).
/// </para>
/// <para>
/// <b>A template without the placeholder is a data error, not a refusal.</b> The catalog's rule check
/// (03.1.1-T2) requires exactly one <c>{term}</c>, and the table constraint backs it; something that reached this
/// code without one is still moderated as it is (the full text, once per keyword) and a warning names the row, so
/// the gate never silently skips a scene. The log carries ids only (D19).
/// </para>
/// </remarks>
/// <param name="log">The warning log of a template that broke the catalog rule.</param>
public sealed partial class ScenePromptExpander(ILogger<ScenePromptExpander> log)
{
    /// <summary>Expands every keyword of <paramref name="keywords"/> into its moderation text.</summary>
    /// <param name="style">The style template the run resolved (03 README D1).</param>
    /// <param name="keywords">The story's keywords, in order.</param>
    /// <returns>
    /// One moderation text per keyword, in the same order, so index <c>i</c> is always the keyword at position
    /// <c>i</c> and a flagged prompt names the scene the owner edited (<c>scene:i</c>). A <see langword="null"/>
    /// keyword yields <see cref="string.Empty"/> at its own position — the gate skips a blank text — instead of
    /// renumbering every scene after it.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="style"/> or <paramref name="keywords"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<string> Expand(StyleTemplate style, IReadOnlyList<string> keywords)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(keywords);

        if (!style.Template.Contains(ScenePromptBuilder.Placeholder, StringComparison.Ordinal))
        {
            LogMissingPlaceholder(style.Id, keywords.Count);
        }

        var prompts = new List<string>(keywords.Count);
        foreach (var keyword in keywords)
        {
            if (keyword is null)
            {
                prompts.Add(string.Empty);

                continue;
            }

            prompts.Add(ScenePromptBuilder.Build(style, keyword).ModerationText);
        }

        return prompts;
    }

    [LoggerMessage(
        EventId = 6524,
        Level = LogLevel.Warning,
        Message = "Style template {StyleTemplateId} carries no term placeholder: {KeywordCount} keywords moderated as the full template text")]
    private partial void LogMissingPlaceholder(Guid styleTemplateId, int keywordCount);
}
