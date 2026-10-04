using System.Text;

namespace NightTales.Infrastructure.Research.Extraction;

/// <summary>
/// The whitespace and Unicode normalisation of extracted text (02.5.2-T0 §1.6): NFC, runs of blanks collapsed to one
/// space, lines trimmed, and at most one blank line between paragraphs. No HTML and no markdown is added.
/// </summary>
public static class TextNormalizer
{
    /// <summary>Normalises <paramref name="text"/>; an empty or blank input returns an empty string.</summary>
    /// <param name="text">The decoded or extracted text.</param>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var blankLines = 0;
        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = CollapseBlanks(rawLine);
            if (line.Length == 0)
            {
                blankLines++;
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
                if (blankLines > 0)
                {
                    builder.Append('\n');
                }
            }

            blankLines = 0;
            builder.Append(line);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string CollapseBlanks(string line)
    {
        var builder = new StringBuilder(line.Length);
        var pendingSpace = false;
        foreach (var character in line)
        {
            if (char.IsWhiteSpace(character) || character == '\u00a0' || char.IsControl(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
