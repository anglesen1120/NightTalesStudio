using System.Globalization;
using System.Text;

namespace NightTales.Application.Features.Assets;

/// <summary>
/// Turns an episode title into the ASCII slug of a download file name (09.7.2 plan §5).
/// </summary>
/// <remarks>
/// <para>
/// The slug is deliberately lossy and closed to a safe alphabet (lowercase ASCII letters, digits and
/// <c>-</c>) because it becomes a <c>Content-Disposition</c> filename parameter: a title that carries a
/// quote, a backslash, a CR/LF pair or any non-ASCII byte can never reach the header through it. The
/// Vietnamese diacritics are folded (<c>ậ</c> → <c>a</c>, <c>đ</c> → <c>d</c>) so "Tập một" becomes
/// <c>tap-mot</c> rather than a row of dashes.
/// </para>
/// <para>
/// The input carries no user id and no stored text: 13's export reuses the same helper, so the naming
/// rule lives here once.
/// </para>
/// </remarks>
public static class VideoSlug
{
    /// <summary>The name used when a title folds away to nothing.</summary>
    public const string Fallback = "nighttales";

    /// <summary>The maximum length of a slug, matching the 80-character cap of WF16.</summary>
    public const int MaxLength = 80;

    /// <summary>
    /// Folds <paramref name="title"/> to a slug: ASCII-fold, lowercase, runs of non-alphanumerics to a
    /// single <c>-</c>, trim the dashes, truncate to <see cref="MaxLength"/>, or <see cref="Fallback"/>
    /// when empty.
    /// </summary>
    /// <param name="title">The episode title, or <see langword="null"/>.</param>
    /// <returns>A non-empty slug of at most <see cref="MaxLength"/> safe characters.</returns>
    public static string From(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Fallback;
        }

        var builder = new StringBuilder(title.Length);
        var lastWasSeparator = false;

        foreach (var character in Fold(title))
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                lastWasSeparator = false;

                continue;
            }

            // A run of punctuation and spaces is one separator: "a,  b" reads "a-b", not "a---b".
            if (!lastWasSeparator)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }

        var slug = builder.ToString().Trim('-');

        if (slug.Length > MaxLength)
        {
            slug = slug[..MaxLength].TrimEnd('-');
        }

        return slug.Length == 0 ? Fallback : slug;
    }

    /// <summary>
    /// Removes the diacritics of <paramref name="value"/> (Unicode NFD plus the explicit
    /// <c>đ</c>/<c>Đ</c> pair, which has no canonical decomposition under NFD).
    /// </summary>
    private static string Fold(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(character switch
            {
                'đ' => 'd',
                'Đ' => 'D',
                _ => character,
            });
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
