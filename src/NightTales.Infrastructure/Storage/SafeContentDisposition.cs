using System.Globalization;
using System.Text;

namespace NightTales.Infrastructure.Storage;

/// <summary>
/// Builds the <c>Content-Disposition</c> of a presigned download (09.7.2 plan §5, RFC 6266/5987).
/// </summary>
/// <remarks>
/// <para>
/// The value is derived from an episode title, i.e. from user-controlled text, and it travels back to
/// the browser inside a response header: a CR, an LF, a quote or a backslash that reached it would be a
/// header-injection hole (BD16). The ASCII parameter is therefore reduced to
/// <c>[A-Za-z0-9 ._-]</c> and the UTF-8 parameter is percent-encoded — a percent-encoded byte cannot
/// be a control character, so neither parameter can break the header. No title text and no user id
/// beyond the name itself is part of the result.
/// </para>
/// <para>
/// The name arrives already folded by <c>VideoSlug</c>, but this helper never trusts it: the folding is
/// repeated here so the invariant is local to the one place that writes a header.
/// </para>
/// </remarks>
public static class SafeContentDisposition
{
    /// <summary>The longest ASCII file name the header carries (WF16 §"safe header").</summary>
    public const int MaxFileNameLength = 80;

    /// <summary>The name used when the input folds away to nothing, so the header is never empty.</summary>
    public const string FallbackFileName = "download";

    /// <summary>
    /// Reduces <paramref name="fileName"/> and formats the two parameters of RFC 6266/5987.
    /// </summary>
    /// <param name="fileName">The requested download name, or <see langword="null"/> for no disposition.</param>
    /// <returns>
    /// <c>attachment; filename="&lt;ascii&gt;"; filename*=UTF-8''&lt;percent-encoded&gt;</c>, or
    /// <see langword="null"/> when <paramref name="fileName"/> is <see langword="null"/> or blank.
    /// </returns>
    public static string? Attachment(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var ascii = AsciiName(fileName);
        var encoded = PercentEncode(fileName);

        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{encoded}";
    }

    /// <summary>
    /// Percent-encodes the UTF-8 bytes of <paramref name="fileName"/> as an RFC 5987 <c>ext-value</c>.
    /// <see cref="Uri.EscapeDataString(string)"/> leaves a handful of characters alone that RFC 5987
    /// does <em>not</em> list as <c>attr-char</c> — the apostrophe that delimits the value among them —
    /// so those are escaped here as well.
    /// </summary>
    private static string PercentEncode(string fileName) =>
        Uri.EscapeDataString(fileName)
            .Replace("'", "%27", StringComparison.Ordinal)
            .Replace("*", "%2A", StringComparison.Ordinal)
            .Replace("!", "%21", StringComparison.Ordinal)
            .Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal);

    /// <summary>
    /// The ASCII parameter: every character outside <c>[A-Za-z0-9 ._-]</c> becomes an underscore, runs
    /// of underscores collapse, and the result is trimmed and truncated.
    /// </summary>
    private static string AsciiName(string fileName)
    {
        var builder = new StringBuilder(fileName.Length);
        var lastWasUnderscore = false;

        foreach (var character in fileName.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var mapped = character switch
            {
                'đ' => 'd',
                'Đ' => 'D',
                _ => character,
            };

            if (IsAllowed(mapped))
            {
                builder.Append(mapped);
                lastWasUnderscore = false;
                continue;
            }

            if (!lastWasUnderscore)
            {
                builder.Append('_');
                lastWasUnderscore = true;
            }
        }

        var name = builder.ToString().Trim('_', ' ', '.');

        if (name.Length > MaxFileNameLength)
        {
            name = name[..MaxFileNameLength].TrimEnd('_', ' ', '.');
        }

        return name.Length == 0 ? FallbackFileName : name;
    }

    private static bool IsAllowed(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is ' ' or '.' or '_' or '-';
}
