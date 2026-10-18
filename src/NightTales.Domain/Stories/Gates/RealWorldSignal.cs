namespace NightTales.Domain.Stories.Gates;

/// <summary>
/// A place where a draft presents something as a real event, person or organisation (02.8.2-T2, F31). T1 reads
/// only the persisted <c>audit_report.model_review.real_world_claims[]</c>, so the hit it ever produces is a
/// <see cref="RealWorldSignalKind.ModelClaim"/>; the lexicon and dated-incident signals are T2's detector.
/// </summary>
/// <param name="Hit">True when at least one signal fired.</param>
/// <param name="Hits">The signals, in the order the detector found them.</param>
public sealed record RealWorldSignal(bool Hit, IReadOnlyList<RealWorldSignalHit> Hits);

/// <summary>One real-world signal (02.8.2-T2 plan §"Real-world detection").</summary>
/// <param name="Segment">The D22 segment address: <c>title</c>, <c>hook</c>, <c>p:&lt;i&gt;</c>, <c>ending</c> or <c>meta</c>.</param>
/// <param name="Kind">Which of the three signals fired.</param>
/// <param name="MatchedPhrase">
/// The lexicon phrase that matched, or the review model's own wording for a claim; never source text.
/// </param>
/// <param name="Evidence">At most 200 characters of our own draft around the match (D22).</param>
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

    /// <summary>A date pattern and an incident word in the same segment.</summary>
    DatedIncident,

    /// <summary>A non-empty <c>model_review.real_world_claims[]</c>.</summary>
    ModelClaim,
}
