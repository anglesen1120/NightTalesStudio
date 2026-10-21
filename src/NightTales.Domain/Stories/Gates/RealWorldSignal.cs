namespace NightTales.Domain.Stories.Gates;

/// <summary>
/// A place where a draft presents something as a real event, person or organisation (02.8.2-T2, F31), as gate check
/// 8 reads it. <see cref="RealWorldSignalDetector"/> produces all three signals; T1's Application factory fills the
/// same record from a stored <c>audit_report</c> alone, so a signal whose text cannot be re-read still fails the
/// check closed rather than passing it.
/// </summary>
/// <param name="Hit">True when at least one signal fired.</param>
/// <param name="Hits">
/// The signals, ordered by segment order, then by <see cref="RealWorldSignalKind"/>, then by position in the
/// segment — the order the story reads.
/// </param>
public sealed record RealWorldSignal(bool Hit, IReadOnlyList<RealWorldSignalHit> Hits);

/// <summary>One real-world signal (02.8.2-T2 plan §"Real-world detection").</summary>
/// <param name="Segment">The D22 segment address: <c>title</c>, <c>hook</c>, <c>p:&lt;i&gt;</c>, <c>ending</c> or <c>meta</c>.</param>
/// <param name="Kind">Which of the three signals fired.</param>
/// <param name="MatchedPhrase">
/// The lexicon phrase or the incident word that matched, as the configuration spells it (never longer than 100
/// characters). A <see cref="RealWorldSignalKind.ModelClaim"/> hit carries the empty string: nothing matched in the
/// text, and the review model's own wording is never copied here (D19/D22).
/// </param>
/// <param name="Evidence">
/// At most 200 characters of our own draft around the match (D22), never source text and never the review's
/// wording; empty when a claim's address does not resolve to a segment of the evaluated version.
/// </param>
public sealed record RealWorldSignalHit(
    string Segment,
    RealWorldSignalKind Kind,
    string MatchedPhrase,
    string Evidence);

/// <summary>The three real-world signals of F31; there is deliberately no named-entity signal.</summary>
public enum RealWorldSignalKind
{
    /// <summary>A phrase from the framing lexicon (<c>real-world-framing.{vi|en}.txt</c>).</summary>
    FramingLexicon,

    /// <summary>A date pattern and an incident word in the same segment (a bare year alone is never a hit).</summary>
    DatedIncident,

    /// <summary>A non-empty <c>model_review.real_world_claims[]</c> (D16).</summary>
    ModelClaim,
}
