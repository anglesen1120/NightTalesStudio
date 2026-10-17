using System.Text;
using System.Text.RegularExpressions;

namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// The dedupe key of an idea's title inside one model batch (02.8.1 plan §4 U1 step 4, task file failure table:
/// a later duplicate is dropped with reason <c>duplicate</c>).
/// </summary>
/// <remarks>
/// <para>
/// The normalisation is deliberately smaller than 02.8.1-T2's <c>TextNormalizer</c> (which strips punctuation and
/// diacritics for the lexical novelty score): two titles of one batch are the same idea when they read the same,
/// so their case and their spacing may not differ, but their letters must. NFC first, then invariant lower, then
/// every whitespace run collapsed to one space and the ends trimmed.
/// </para>
/// <para>
/// It is pure and deterministic, so the same batch always drops the same candidates in the same order — the
/// first title of a duplicate group wins, which is what makes the persistence order of a run reproducible.
/// </para>
/// </remarks>
public static partial class IdeaTitleKey
{
    /// <summary>
    /// The key of <paramref name="title"/>: NFC, invariant lower case, whitespace runs collapsed to one space.
    /// </summary>
    /// <param name="title">The candidate's title, as the model wrote it (never null: the schema requires it).</param>
    /// <returns>The dedupe key; empty for a blank title.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="title"/> is <see langword="null"/>.</exception>
    public static string Normalise(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        var composed = title.Normalize(NormalizationForm.FormC);
        var lowered = composed.ToLowerInvariant();
        var collapsed = Whitespace().Replace(lowered, " ");

        return collapsed.Trim();
    }

    /// <summary>Every run of Unicode whitespace, which the key collapses to one space.</summary>
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
