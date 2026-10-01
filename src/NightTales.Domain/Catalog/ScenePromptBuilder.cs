using System.Security.Cryptography;
using System.Text;

namespace NightTales.Domain.Catalog;

/// <summary>
/// Turns a style template plus a story keyword into the exact prompt text OpenAI receives
/// (03 README D1): the template with its single <see cref="Placeholder"/> replaced, then one space and
/// <see cref="NegativeClause.Current"/>. Pure and deterministic, so the same inputs always give the
/// same <see cref="ScenePrompt.Sha256"/> the relay attributes a call by.
/// </summary>
/// <remarks>
/// The keyword itself is not validated here (that is <c>KeywordRules</c>, 03.2.2-T1); the builder only
/// trims it. Only the first <c>Replace</c> pass runs, so a keyword that contains the literal
/// placeholder keeps it (harmless, and the keyword charset rejects braces anyway).
/// </remarks>
public static class ScenePromptBuilder
{
    /// <summary>The single placeholder every style template contains.</summary>
    public const string Placeholder = "{term}";

    /// <summary>Builds the prompt for <paramref name="keyword"/> in <paramref name="style"/>.</summary>
    /// <param name="style">The catalog template; its <see cref="StyleTemplate.Template"/> was rule-checked at seed time.</param>
    /// <param name="keyword">The scene term; trimmed, never otherwise rewritten.</param>
    public static ScenePrompt Build(StyleTemplate style, string keyword)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(keyword);

        var term = keyword.Trim();
        var styled = style.Template.Replace(Placeholder, term, StringComparison.Ordinal);
        var text = styled + " " + NegativeClause.Current;

        return new ScenePrompt(text, styled, Sha256Hex(text));
    }

    /// <summary>The lower-case hex SHA-256 of <paramref name="text"/> as UTF-8.</summary>
    public static string Sha256Hex(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
