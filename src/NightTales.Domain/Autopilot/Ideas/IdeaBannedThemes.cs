using System.Buffers;
using System.Text;

namespace NightTales.Domain.Autopilot.Ideas;

/// <summary>
/// The deterministic, free half of the content policy (02.8.1-T2, task Design "Policy" 1): which of the
/// series' banned themes a candidate's text contains, matched as whole words.
/// </summary>
/// <remarks>
/// <para>
/// Both sides are normalised the same way — NFC, then invariant lower — so a theme matches whatever case
/// and whatever Unicode form the caller wrote. Diacritics are <b>kept</b>: <c>tự tử</c> and <c>tu tu</c>
/// are different themes in Vietnamese, and stripping marks would ban one for the other as well. The
/// normalisation is the policy's own, not <c>TextNormalizer</c>'s: that one sanitises untrusted text for
/// shingling (it deletes separators the policy has to keep apart) while the policy only has to compare
/// words faithfully.
/// </para>
/// <para>
/// Matching is whole-word on the token sequence: a theme's tokens must appear as a contiguous run of the
/// candidate's tokens, so the theme <c>ma</c> does not match <c>mama</c> — the false positive the task's
/// failure table names. Tokens are maximal runs of Unicode letters and digits, so a punctuation-only or
/// combining-only difference never joins two words.
/// </para>
/// <para>
/// This type also hosts the policy token primitives (<see cref="NormalizePolicyText"/>,
/// <see cref="TokenizePolicyText"/>, <see cref="PolicyToken"/> and the sequence matchers) that
/// <see cref="IdeaTrueCrimeFraming"/> reuses: both are the same whole-word rule over the same candidate
/// text, and a second tokenizer would let the two checks disagree about what a word is.
/// </para>
/// </remarks>
public static class IdeaBannedThemes
{
    /// <summary>
    /// The banned themes the text contains, in the order the list gave them and in their original form.
    /// </summary>
    /// <remarks>
    /// The original spelling is returned — not the normalised one — because the value is stored in the
    /// policy verdict and read back by an operator; a theme edited in configuration must be recognisable
    /// in the answer. A blank theme is skipped rather than matched: an empty token sequence would match
    /// every candidate. A theme repeated in the list is reported once.
    /// </remarks>
    /// <param name="candidateText">The candidate's text (<c>title + " " + logline + " " + hook + " " + setting</c>), before normalisation.</param>
    /// <param name="bannedThemes">The series' banned themes, in configuration order.</param>
    /// <returns>The matched themes in their original form; empty when none matched.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidateText"/> or <paramref name="bannedThemes"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> Match(string candidateText, IReadOnlyList<string> bannedThemes)
    {
        ArgumentNullException.ThrowIfNull(candidateText);
        ArgumentNullException.ThrowIfNull(bannedThemes);

        if (bannedThemes.Count == 0)
        {
            return [];
        }

        var tokens = TokenizePolicyText(NormalizePolicyText(candidateText));
        if (tokens.Count == 0)
        {
            return [];
        }

        var matched = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var theme in bannedThemes)
        {
            if (string.IsNullOrWhiteSpace(theme) || !seen.Add(theme))
            {
                continue;
            }

            var needle = TokenizePolicyText(NormalizePolicyText(theme));
            if (ContainsTokenSequence(tokens, needle))
            {
                matched.Add(theme);
            }
        }

        return [.. matched];
    }

    /// <summary>
    /// The policy form of a text: NFC, invariant lower, diacritics kept.
    /// </summary>
    /// <remarks>
    /// NFC first, so a decomposed diacritic reaches the tokenizer as the precomposed character; lower after
    /// it, because the case mapping of a precomposed character is the one that keeps the Vietnamese vowels
    /// intact; and NFC once more, since casing can reorder combining marks. Lowering is invariant, never
    /// culture-sensitive, so a configuration value cannot change its meaning with the request's culture.
    /// </remarks>
    /// <param name="text">The raw text.</param>
    /// <returns>The normalised text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    internal static string NormalizePolicyText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Normalize(NormalizationForm.FormC)
            .ToLowerInvariant()
            .Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// The words of a normalised text: maximal runs of Unicode letters and digits, with their offsets.
    /// </summary>
    /// <remarks>
    /// Runs are decoded per <see cref="Rune"/>, so an astral letter is one character of one token, and a
    /// malformed UTF-16 code unit is a separator rather than token content. Nothing here is a regular
    /// expression: the scan is linear in the text and has no backtracking, which is what makes it safe over
    /// model output of any shape.
    /// </remarks>
    /// <param name="normalized">A text from <see cref="NormalizePolicyText"/>.</param>
    /// <returns>The tokens in text order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="normalized"/> is <see langword="null"/>.</exception>
    internal static IReadOnlyList<PolicyToken> TokenizePolicyText(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);

        var tokens = new List<PolicyToken>();
        var start = -1;
        var offset = 0;
        while (offset < normalized.Length)
        {
            var status = Rune.DecodeFromUtf16(normalized.AsSpan(offset), out var rune, out var consumed);
            if (status == OperationStatus.Done && Rune.IsLetterOrDigit(rune))
            {
                if (start < 0)
                {
                    start = offset;
                }
            }
            else if (start >= 0)
            {
                tokens.Add(new PolicyToken(normalized[start..offset], start));
                start = -1;
            }

            offset += status == OperationStatus.Done ? consumed : 1;
        }

        if (start >= 0)
        {
            tokens.Add(new PolicyToken(normalized[start..], start));
        }

        return tokens;
    }

    /// <summary>Whether <paramref name="tokens"/> contains <paramref name="needle"/> as a contiguous run.</summary>
    /// <param name="tokens">The candidate's tokens.</param>
    /// <param name="needle">The phrase's tokens; an empty one never matches.</param>
    /// <returns><see langword="true"/> when the phrase appears as whole words.</returns>
    internal static bool ContainsTokenSequence(
        IReadOnlyList<PolicyToken> tokens,
        IReadOnlyList<PolicyToken> needle)
    {
        if (needle.Count == 0 || needle.Count > tokens.Count)
        {
            return false;
        }

        for (var index = 0; index + needle.Count <= tokens.Count; index++)
        {
            if (MatchesTokenSequenceAt(tokens, needle, index))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="needle"/> sits at <paramref name="index"/> of <paramref name="tokens"/>.</summary>
    /// <param name="tokens">The candidate's tokens.</param>
    /// <param name="needle">The phrase's tokens; the caller guarantees it is not empty.</param>
    /// <param name="index">The candidate token the phrase would start at.</param>
    /// <returns><see langword="true"/> when every token of the phrase matches at its place.</returns>
    internal static bool MatchesTokenSequenceAt(
        IReadOnlyList<PolicyToken> tokens,
        IReadOnlyList<PolicyToken> needle,
        int index)
    {
        for (var offset = 0; offset < needle.Count; offset++)
        {
            if (!string.Equals(tokens[index + offset].Text, needle[offset].Text, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One word of a policy text: its characters and where they start in the normalised text.</summary>
    /// <remarks>
    /// The offset is what lets <see cref="IdeaTrueCrimeFraming"/> measure how far apart a dated incident
    /// and a crime noun are without re-scanning the text.
    /// </remarks>
    /// <param name="Text">The token's characters.</param>
    /// <param name="Start">The token's offset in the normalised text.</param>
    internal readonly record struct PolicyToken(string Text, int Start)
    {
        /// <summary>The offset just past the token's last character.</summary>
        internal int End => Start + Text.Length;
    }
}
