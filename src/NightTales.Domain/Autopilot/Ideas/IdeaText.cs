namespace NightTales.Domain.Autopilot.Ideas;

/// <summary>
/// The markup rule of the idea fields (02.8.1 plan §3.2, "no markdown/list markers"): the marker characters
/// an idea title, logline, hook or setting must not carry and the list markers a line must not start with.
/// </summary>
/// <remarks>
/// <para>
/// The strict schema cannot forbid these, so the validator does, exactly as the D3 draft rule does for a
/// story (02 T72): an idea is prose for a narration channel, never a document, and a marker that survives
/// into the prompt or the episode is a defect the reader sees. The <c>&lt;</c>/<c>&gt;</c> rule is a
/// separate, named one in <see cref="IdeaValidator"/> — those two characters are HTML, not markdown — and
/// the beat values and structure tags have their own vocabularies, so none of them is checked here.
/// </para>
/// <para>
/// The marker set is deliberately narrower than <c>StoryText.FindMarkup</c>'s: brackets, parentheses and
/// the two angle brackets are not "markdown emphasis or a heading" and would refuse ordinary Vietnamese
/// text such as <c>(phần 2)</c>, so this rule refuses only what the 02.8.1 contract names.
/// </para>
/// </remarks>
public static class IdeaText
{
    /// <summary>The emphasis, code-font and heading markers (<c>*</c>, <c>_</c>, <c>`</c>, <c>#</c>).</summary>
    public static IReadOnlySet<char> Markers { get; } = new HashSet<char> { '*', '_', '`', '#' };

    /// <summary>Whether <paramref name="text"/> carries a marker <see cref="FindMarkup"/> refuses.</summary>
    /// <param name="text">The text to scan; <see langword="null"/> and empty carry none.</param>
    /// <returns>True when <see cref="FindMarkup"/> finds something.</returns>
    public static bool ContainsMarkup(string? text) => FindMarkup(text) is not null;

    /// <summary>
    /// The first marker <paramref name="text"/> carries, or <see langword="null"/>: one of
    /// <see cref="Markers"/>, or a line starting with a bullet (<c>-</c>, <c>*</c>, <c>+</c> and a space) or
    /// with <c>&lt;digits&gt;. </c>.
    /// </summary>
    /// <remarks>
    /// The returned value is the <see cref="IdeaFieldError.Detail"/> of the violation, never a fragment of
    /// the text (D19): the marker itself, or <c>list_marker</c>.
    /// </remarks>
    /// <param name="text">The text to scan; <see langword="null"/> and empty carry none.</param>
    /// <returns>The marker, or <see langword="null"/>.</returns>
    public static string? FindMarkup(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        foreach (var character in text)
        {
            if (Markers.Contains(character))
            {
                return character.ToString();
            }
        }

        return HasListMarker(text) ? "list_marker" : null;
    }

    /// <summary>Whether any line of <paramref name="text"/> starts with a bullet or an ordered-list marker.</summary>
    /// <param name="text">The text to scan.</param>
    /// <returns>Whether a list marker was found.</returns>
    private static bool HasListMarker(string text)
    {
        var lineStart = 0;
        while (lineStart <= text.Length)
        {
            var lineEnd = lineStart;
            while (lineEnd < text.Length && text[lineEnd] is not ('\n' or '\r'))
            {
                lineEnd++;
            }

            if (IsListMarker(text.AsSpan(lineStart, lineEnd - lineStart)))
            {
                return true;
            }

            if (lineEnd >= text.Length)
            {
                return false;
            }

            // A CRLF pair is one line break; a lone CR or LF is its own.
            lineStart = text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n'
                ? lineEnd + 2
                : lineEnd + 1;
        }

        return false;
    }

    /// <summary>The bullet or ordered-list marker of one line, if it starts with one.</summary>
    /// <param name="line">The line, without its terminator.</param>
    /// <returns>Whether the line starts with a list marker.</returns>
    private static bool IsListMarker(ReadOnlySpan<char> line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length >= 2 && (trimmed[0] is '-' or '*' or '+') && trimmed[1] == ' ')
        {
            return true;
        }

        var digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits]))
        {
            digits++;
        }

        return digits > 0 && digits + 1 < trimmed.Length && trimmed[digits] == '.' && trimmed[digits + 1] == ' ';
    }
}
