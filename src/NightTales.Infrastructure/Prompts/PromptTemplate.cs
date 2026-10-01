using System.Text;
using NightTales.Application.Abstractions.Prompts;

namespace NightTales.Infrastructure.Prompts;

/// <summary>
/// The placeholder scan and substitution of one prompt template (02 README D5 rule 6, D6): a template
/// may only carry <c>{{name}}</c> tokens from <see cref="PromptPlaceholders.All"/>, and substitution
/// fills exactly those tokens.
/// </summary>
/// <remarks>
/// <para>
/// Untrusted material never passes through here (D6): the values substituted are the shared rule
/// blocks and the length scalars, which the host owns; the brief, bible, facts and source text travel
/// in the JSON data object of the user message, where the system prompt declares them data.
/// </para>
/// <para>
/// The scan is a plain ordinal search, not a regex: a template is a few kilobytes read once at
/// start-up, and <c>{{</c> has no meaning in any prompt body outside this contract.
/// </para>
/// </remarks>
public static class PromptTemplate
{
    /// <summary>The opening delimiter of a placeholder.</summary>
    internal const string Open = "{{";

    /// <summary>The closing delimiter of a placeholder.</summary>
    internal const string Close = "}}";

    /// <summary>
    /// Every placeholder name <paramref name="text"/> uses that is not in the closed list, in order of
    /// first appearance and without repeats. An unterminated <c>{{</c> counts as a name reaching the end
    /// of the text, so a truncated token is reported rather than ignored.
    /// </summary>
    /// <param name="text">The template text.</param>
    /// <returns>The unknown names, without their braces.</returns>
    public static IReadOnlyList<string> UnknownPlaceholders(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var unknown = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        while (index < text.Length)
        {
            var start = text.IndexOf(Open, index, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var end = text.IndexOf(Close, start + Open.Length, StringComparison.Ordinal);
            var name = end < 0 ? text[(start + Open.Length)..] : text[(start + Open.Length)..end];
            index = end < 0 ? text.Length : end + Close.Length;

            if (!PromptPlaceholders.IsKnown(name) && seen.Add(name))
            {
                unknown.Add(name);
            }
        }

        return unknown;
    }

    /// <summary>Replaces every closed placeholder of <paramref name="text"/> with its value.</summary>
    /// <param name="text">The template text.</param>
    /// <param name="values">One value per placeholder the template uses.</param>
    /// <returns>The template with every placeholder substituted.</returns>
    /// <exception cref="PromptCatalogException">
    /// The template uses a placeholder outside the closed list, or one the caller supplied no value for.
    /// </exception>
    public static string Substitute(string text, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(values);

        var unknown = UnknownPlaceholders(text);
        if (unknown.Count > 0)
        {
            throw new PromptCatalogException(
                unknown.Select(name => $"unknown placeholder '{Open}{name}{Close}'").ToList());
        }

        var builder = new StringBuilder(text.Length);
        var index = 0;

        while (index < text.Length)
        {
            var start = text.IndexOf(Open, index, StringComparison.Ordinal);
            if (start < 0)
            {
                builder.Append(text, index, text.Length - index);

                break;
            }

            var end = text.IndexOf(Close, start + Open.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                builder.Append(text, index, text.Length - index);

                break;
            }

            var name = text[(start + Open.Length)..end];
            if (!values.TryGetValue(name, out var value))
            {
                throw new PromptCatalogException(new List<string> { $"no value for placeholder '{Open}{name}{Close}'" });
            }

            builder.Append(text, index, start - index).Append(value);
            index = end + Close.Length;
        }

        return builder.ToString();
    }
}
