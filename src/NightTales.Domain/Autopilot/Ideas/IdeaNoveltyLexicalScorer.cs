namespace NightTales.Domain.Autopilot.Ideas;

/// <summary>
/// The candidate side of the lexical novelty pass (02.8.1-T2, task Design "Lexical"): the exact text the
/// shingle pass reads, so the lexical score and the policy checks can never disagree about what a
/// candidate's text is.
/// </summary>
/// <remarks>
/// The scoring itself is <see cref="LexicalCorpus.ScoreCandidate"/> against a corpus prepared once per
/// batch; this type only fixes the text. The name keeps the plan's <c>IdeaNoveltyLexical</c> family,
/// which the Domain already uses for the stored <see cref="IdeaNoveltyLexical"/> detail.
/// </remarks>
public static class IdeaNoveltyLexicalScorer
{
    /// <summary>
    /// The text the shingle pass scores: <c>title + " " + logline + " " + setting</c>.
    /// </summary>
    /// <remarks>
    /// The hook is deliberately not part of it (task Design "Lexical": <c>title + " " + logline + " " +
    /// setting</c>). The hook is a craft signal the judge scores as <c>hook_strength</c>; folding it into
    /// the overlap text would let two ideas that share only an opening image look like the same story.
    /// The returned text is raw — <see cref="LexicalCorpus.ScoreCandidate"/> normalises it — so a caller
    /// may also show it to the policy checks, which normalise it their own way.
    /// </remarks>
    /// <param name="candidate">The candidate whose shingle text is wanted.</param>
    /// <returns>The three texts joined by single spaces.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> is <see langword="null"/>.</exception>
    public static string CandidateText(IdeaCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return candidate.Title + " " + candidate.Logline + " " + candidate.Setting;
    }
}
