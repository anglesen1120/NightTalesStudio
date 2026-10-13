using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using NightTales.Domain.Common;

namespace NightTales.Application.Features.Research.Facts;

/// <summary>
/// The one JSON data object of a fact-extraction call (02 README D6, 02.5.3-T1): the task, the language and
/// the sources. Every string here is material the model may read, never an instruction; the
/// <c>facts/v1</c> system prompt says so.
/// </summary>
/// <param name="Task">Always <c>extract_facts</c>.</param>
/// <param name="Language">The run's language, snake_case.</param>
/// <param name="Sources">The sources, <c>alias</c>/<c>title</c>/<c>text</c> only — never a source id or a URL.</param>
public sealed record FactDataObject(string Task, string Language, IReadOnlyList<FactDataSource> Sources);

/// <summary>One source of the data object: the alias, the title and the text. No id, no URL (D6).</summary>
/// <param name="Alias">The alias, <c>s1…s10</c>.</param>
/// <param name="Title">The source title.</param>
/// <param name="Text">The source text.</param>
public sealed record FactDataSource(string Alias, string Title, string Text);

/// <summary>
/// Serialises the one data object of a fact-extraction call (02 README D6, 02.5.3-T1). One place, so the
/// input budget's token estimate is exactly the bytes the extractor sends
/// (<see cref="FactInputBudget.EstimateTokens"/> is defined on this serialisation).
/// </summary>
/// <remarks>
/// Untrusted source text is only ever a JSON string value, serialised with <c>System.Text.Json</c>; it is
/// never substituted into a template, so it cannot break the JSON or the turn structure. Non-ASCII text is
/// written readably (the encoder is pinned to all Unicode ranges), and no string is written before
/// <see cref="Sanitize"/> has neutralised the two ways a source string could break the object or the store.
/// </remarks>
public static class FactDataObjectJson
{
    /// <summary>The <c>task</c> control value of a fact-extraction call.</summary>
    public const string ExtractFactsTask = "extract_facts";

    /// <summary>The Unicode replacement character, written where a string held an unpaired UTF-16 surrogate.</summary>
    private const char Replacement = '\uFFFD';

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>The snake_case wire spelling of a language, for example <c>vi</c>.</summary>
    /// <param name="language">The content language.</param>
    /// <returns>The spelling the data object, the prompt-cache key and the preset file names use.</returns>
    public static string LanguageSpelling(Language language) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(language.ToString());

    /// <summary>Serialises the one data object, sanitising every string value first.</summary>
    /// <param name="language">The run's language.</param>
    /// <param name="sources">The sources to send.</param>
    /// <returns>The data object as JSON; the exact bytes the estimate is taken from.</returns>
    public static string Serialize(Language language, IReadOnlyList<FactSourceInput> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var data = new FactDataObject(
            ExtractFactsTask,
            LanguageSpelling(language),
            [.. sources.Select(source => new FactDataSource(source.Alias, Sanitize(source.Title), Sanitize(source.Text)))]);

        return JsonSerializer.Serialize(data, Options);
    }

    /// <summary>
    /// Neutralises the two ways a source string could break the one data object or the store it later feeds
    /// (02.5.3-T1): an unpaired UTF-16 surrogate becomes U+FFFD — <c>System.Text.Json</c> would otherwise
    /// rewrite it or throw, and PostgreSQL cannot store a lone surrogate — and a C0 control character other
    /// than <c>\n</c> and <c>\t</c>, a NUL most of all, becomes a space, so no control character reaches the
    /// provider or a <c>jsonb</c> column. <c>\n</c> and <c>\t</c> stay literal (the JSON writer escapes them).
    /// </summary>
    /// <param name="value">The string to sanitise.</param>
    /// <returns>The same instance when nothing needed changing, a rebuilt string otherwise.</returns>
    internal static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        StringBuilder? builder = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder?.Append(c).Append(value[i + 1]);
                i++;
                continue;
            }

            if (!char.IsSurrogate(c) && (c >= ' ' || c == '\n' || c == '\t'))
            {
                builder?.Append(c);
                continue;
            }

            (builder ??= new StringBuilder(value.Length).Append(value, 0, i)).Append(char.IsSurrogate(c) ? Replacement : ' ');
        }

        return builder is null ? value : builder.ToString();
    }
}
