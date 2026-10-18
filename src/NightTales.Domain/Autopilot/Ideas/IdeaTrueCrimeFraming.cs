namespace NightTales.Domain.Autopilot.Ideas;

/// <summary>
/// The real-world-framing signal of the content policy (02.8.1-T2, task Design "Policy" 3; README F31/F33):
/// whether a candidate is written as if it were about a real, dated incident, so the story gate has to ask
/// the owner for research before anything is written.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes trip the signal. A phrasing from the configured framing lexicon — vi
/// <c>câu chuyện có thật</c> / <c>dựa trên sự thật</c>, en <c>based on a true story</c> / <c>true crime</c> —
/// claims a real basis outright. A four-digit year of the <c>19xx</c> or <c>20xx</c> form standing within
/// forty characters of a crime noun of the configured list names a dated incident inside a case, the way a
/// true-crime angle does.
/// </para>
/// <para>
/// Nothing is rejected here: <see cref="RequiresResearch"/> only flags the candidate, because the story
/// gate and the owner's fact-sheet approval are the real control (F33). The signal is deterministic and
/// free, so it costs a run nothing to carry it.
/// </para>
/// <para>
/// Matching reuses <see cref="IdeaBannedThemes"/>' policy primitives: the same NFC-and-lower normalisation,
/// the same whole-word tokens, the same token-sequence containment. There is no regular expression here at
/// all — the year form is checked on a token's characters, which is what keeps the rule linear in the text
/// and free of backtracking.
/// </para>
/// </remarks>
public static class IdeaTrueCrimeFraming
{
    /// <summary>
    /// How far a dated incident and a crime noun may stand apart, in characters, and still be read as one
    /// mention. The distance is the count of characters strictly between the two spans, so a year
    /// immediately before or after the noun is at distance zero and two mentions forty characters apart
    /// still trip the signal.
    /// </summary>
    private const int YearProximityChars = 40;

    /// <summary>
    /// Whether the text frames the idea as a real, dated incident: a framing phrase, or a dated incident
    /// near a crime noun.
    /// </summary>
    /// <remarks>
    /// Blank and repeated list entries are skipped: a blank phrase would match every candidate and would
    /// flag every run. A phrase matches as whole words, so <c>true crime</c> in the lexicon does not fire on
    /// <c>true crimes</c> halves such as a bare <c>crime</c> elsewhere in the text — only the phrase the
    /// operator configured does. A year only counts next to a crime noun: a year on its own is a setting
    /// detail, not a claim about the world.
    /// </remarks>
    /// <param name="candidateText">The candidate's text (<c>title + " " + logline + " " + hook + " " + setting</c>), before normalisation.</param>
    /// <param name="framingLexicon">The phrases that claim a real basis (vi and en data of <c>Autopilot:Ideas:Policy:FramingLexicon</c>).</param>
    /// <param name="crimeNouns">The nouns that make a dated mention an incident (<c>Autopilot:Ideas:Policy:CrimeNouns</c>).</param>
    /// <returns><see langword="true"/> when the candidate needs research before it can be written.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    public static bool RequiresResearch(
        string candidateText,
        IReadOnlyList<string> framingLexicon,
        IReadOnlyList<string> crimeNouns)
    {
        ArgumentNullException.ThrowIfNull(candidateText);
        ArgumentNullException.ThrowIfNull(framingLexicon);
        ArgumentNullException.ThrowIfNull(crimeNouns);

        if (framingLexicon.Count == 0 && crimeNouns.Count == 0)
        {
            // The default configuration ships empty lists (task Design "Policy" 5): nothing configured,
            // nothing flagged.
            return false;
        }

        var tokens = IdeaBannedThemes.TokenizePolicyText(IdeaBannedThemes.NormalizePolicyText(candidateText));
        if (tokens.Count == 0)
        {
            return false;
        }

        return ContainsFramingPhrase(tokens, framingLexicon)
            || HasDatedIncidentNearACrimeNoun(tokens, crimeNouns);
    }

    /// <summary>Whether one of the lexicon's phrases appears as whole words.</summary>
    private static bool ContainsFramingPhrase(
        IReadOnlyList<IdeaBannedThemes.PolicyToken> tokens,
        IReadOnlyList<string> framingLexicon)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var phrase in framingLexicon)
        {
            if (string.IsNullOrWhiteSpace(phrase) || !seen.Add(phrase))
            {
                continue;
            }

            var needle = IdeaBannedThemes.TokenizePolicyText(IdeaBannedThemes.NormalizePolicyText(phrase));
            if (IdeaBannedThemes.ContainsTokenSequence(tokens, needle))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a dated incident of the text stands near a configured crime noun.</summary>
    private static bool HasDatedIncidentNearACrimeNoun(
        IReadOnlyList<IdeaBannedThemes.PolicyToken> tokens,
        IReadOnlyList<string> crimeNouns)
    {
        var nouns = new List<IReadOnlyList<IdeaBannedThemes.PolicyToken>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var noun in crimeNouns)
        {
            if (string.IsNullOrWhiteSpace(noun) || !seen.Add(noun))
            {
                continue;
            }

            var needle = IdeaBannedThemes.TokenizePolicyText(IdeaBannedThemes.NormalizePolicyText(noun));
            if (needle.Count > 0)
            {
                nouns.Add(needle);
            }
        }

        if (nouns.Count == 0)
        {
            return false;
        }

        foreach (var year in tokens)
        {
            if (!IsDatedIncident(year.Text))
            {
                continue;
            }

            foreach (var noun in nouns)
            {
                if (IsNear(tokens, noun, year))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether any occurrence of <paramref name="noun"/> sits within the proximity of <paramref name="year"/>.</summary>
    private static bool IsNear(
        IReadOnlyList<IdeaBannedThemes.PolicyToken> tokens,
        IReadOnlyList<IdeaBannedThemes.PolicyToken> noun,
        IdeaBannedThemes.PolicyToken year)
    {
        for (var index = 0; index + noun.Count <= tokens.Count; index++)
        {
            if (!IdeaBannedThemes.MatchesTokenSequenceAt(tokens, noun, index))
            {
                continue;
            }

            var gap = Math.Max(0, Math.Max(year.Start, tokens[index].Start)
                - Math.Min(year.End, tokens[index + noun.Count - 1].End));
            if (gap <= YearProximityChars)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether one token is a dated incident: exactly four digits, of the <c>19xx</c> or <c>20xx</c> form.
    /// </summary>
    /// <remarks>
    /// The form is the task's <c>\b(19|20)\d{2}\b</c> read as a token rule instead of a pattern: the token
    /// is already a whole word (a maximal run of letters and digits), so a longer number such as
    /// <c>19745</c> — five digits — fails on length and a token such as <c>20ab</c> fails on its digits.
    /// </remarks>
    private static bool IsDatedIncident(string token) =>
        token.Length == 4
        && ((token[0] == '1' && token[1] == '9') || (token[0] == '2' && token[1] == '0'))
        && token[2] is >= '0' and <= '9'
        && token[3] is >= '0' and <= '9';
}
