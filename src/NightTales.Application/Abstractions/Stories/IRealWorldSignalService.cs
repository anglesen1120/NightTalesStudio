using NightTales.Domain.Common;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Gates;

namespace NightTales.Application.Abstractions.Stories;

/// <summary>
/// Check 8 of the story gate (02.8.2 plan §3.1, task 02.8.2-T2 step 4): it assembles the two inputs only this
/// layer knows how to read — the version's own generated text in D22 addressing and the release's framing lexicon
/// for the series language — and hands them to the Domain's <see cref="RealWorldSignalDetector"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Generated content only.</b> The text is the stored <c>story.v1</c> document (<see cref="StoryTextSegments.From"/>),
/// so the owner's brief, notes and fact statements never reach the detector (F31, task §"Failure handling"): a
/// person who says "this happened to me" in their brief has not asked the episode to claim it.
/// </para>
/// <para>
/// <b>Unknown is not clean.</b> <see langword="null"/> answers "the gate cannot say", and the caller passes it on
/// to <see cref="StoryGateInput.RealWorld"/>, whose null fails check 8 closed with
/// <see cref="GateDetailCodes.FieldAbsent"/> exactly as 02.8.2-T1 did. Two cases are unknown: claims the audit
/// report did not let the caller read (an absent review, an absent or non-array <c>real_world_claims</c> member),
/// and a content document with no text at all — a detector over nothing cannot certify a story as fiction.
/// </para>
/// <para>
/// The implementation is <c>NightTales.Application.Features.Autopilot.Gates.RealWorldSignalService</c>; it reads
/// the phrases through <see cref="Prompts.IRealWorldLexicon"/> and is registered with the rest of the gate engine.
/// </para>
/// </remarks>
public interface IRealWorldSignalService
{
    /// <summary>Detects the real-world signals of one story version.</summary>
    /// <param name="content">The version's stored <c>story.v1</c> content, or <see langword="null"/> when it could not be read.</param>
    /// <param name="language">The series language, which picks the lexicon.</param>
    /// <param name="modelClaims">
    /// The review model's claims reduced to their D22 addresses, or <see langword="null"/> when the stored audit
    /// report did not let the caller read them.
    /// </param>
    /// <param name="options">The profile's <c>RealWorld</c> block: co-occurrence rule and incident words.</param>
    /// <returns>The signal, or <see langword="null"/> when the gate cannot decide (see the type remarks).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    RealWorldSignal? Detect(
        StoryContentV1? content,
        Language language,
        IReadOnlyList<RealWorldClaim>? modelClaims,
        RealWorldOptions options);
}
