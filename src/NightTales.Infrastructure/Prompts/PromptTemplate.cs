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
    /// first appearance and without repeats. Only a <em>closed</em> token (<c>{{name}}</c>) is read here;
    /// an opening delimiter with no closing one is a violation of its own and is reported by
    /// <see cref="UnterminatedPlaceholders"/> instead.
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
            if (end < 0)
            {
                // The token runs to the end of the text: UnterminatedPlaceholders reports it.
                break;
            }

            var name = text[(start + Open.Length)..end];
            index = end + Close.Length;

            if (!PromptPlaceholders.IsKnown(name) && seen.Add(name))
            {
                unknown.Add(name);
            }
        }

        return unknown;
    }

    /// <summary>
    /// Every unterminated placeholder of <paramref name="text"/>: the text after an opening delimiter
    /// that has no closing one, up to the end of the template. Such a token reaches a model as literal
    /// text whatever name it carries, so it is a violation independent of the closed list.
    /// </summary>
    /// <param name="text">The template text.</param>
    /// <returns>The token text after each unterminated <c>{{</c>, without the braces.</returns>
    public static IReadOnlyList<string> UnterminatedPlaceholders(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var unterminated = new List<string>();
        var index = 0;

        while (index < text.Length)
        {
            var start = text.IndexOf(Open, index, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var end = text.IndexOf(Close, start + Open.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                unterminated.Add(text[(start + Open.Length)..]);

                break;
            }

            index = end + Close.Length;
        }

        return unterminated;
    }

    /// <summary>Replaces every closed placeholder of <paramref name="text"/> with its value.</summary>
    /// <param name="text">The template text.</param>
    /// <param name="values">One value per placeholder the template uses.</param>
    /// <returns>The template with every placeholder substituted.</returns>
    /// <exception cref="PromptCatalogException">
    /// The template has an unterminated placeholder, uses a name outside the closed list, or the caller
    /// supplied no value for a name it uses.
    /// </exception>
    public static string Substitute(string text, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(values);

        // One scan on the substitution path: each token is validated against the closed list and, when
        // known, looked up and appended. Malformed tokens (unknown name or an unterminated {{) are
        // collected and thrown together, as the start-up gate reports every one of them; a known token
        // the caller supplied no value for is deferred to the end, so a malformed template is named in
        // full before the caller's values are blamed.
        var builder = new StringBuilder(text.Length);
        var malformed = new List<string>();
        var missing = new List<string>();
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
                malformed.Add(UnterminatedViolation(text[(start + Open.Length)..]));

                break;
            }

            var name = text[(start + Open.Length)..end];
            if (!PromptPlaceholders.IsKnown(name))
            {
                malformed.Add($"unknown placeholder '{Open}{name}{Close}'");
                index = end + Close.Length;

                continue;
            }

            if (!values.TryGetValue(name, out var value))
            {
                missing.Add($"no value for placeholder '{Open}{name}{Close}'");
                index = end + Close.Length;

                continue;
            }

            builder.Append(text, index, start - index).Append(value);
            index = end + Close.Length;
        }

        if (malformed.Count > 0)
        {
            throw new PromptCatalogException(malformed);
        }

        if (missing.Count > 0)
        {
            throw new PromptCatalogException(missing);
        }

        return builder.ToString();
    }

    /// <summary>The violation message of an unterminated token, for the start-up gate and for substitution.</summary>
    /// <param name="token">The text after the opening <c>{{</c>, to the end of the template.</param>
    /// <returns>The message; the caller appends the file's key when it has one.</returns>
    internal static string UnterminatedViolation(string token) => $"unterminated placeholder '{Open}{token}'";
}
