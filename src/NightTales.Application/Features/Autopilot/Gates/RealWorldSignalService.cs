using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Stories;
using NightTales.Domain.Common;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Gates;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// The <see cref="IRealWorldSignalService"/> of the story gate (02.8.2-T2): it composes the detector's three inputs
/// from what the gate already read and nothing else — the version's stored content, the release's lexicon for the
/// series language and the review model's claims — and never touches the owner's inputs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two sources, one detector.</b> The text is the version's own <c>story.v1</c> document, addressed by the D22
/// segment ids the editor and the audit report share, and the claims come from the stored <c>audit.v1</c>; the
/// phrases come from <see cref="IRealWorldLexicon"/>, whose files the release manifest already validated (D5).
/// The detector itself (<see cref="RealWorldSignalDetector"/>) stays in the Domain, pure and clock-free, so T3's
/// calibration reads exactly the code the gate runs.
/// </para>
/// <para>
/// <b>The current text, always.</b> Detection runs over the content that is being gated, never over a text the
/// claims were written about first: a revision that introduces "dựa trên sự kiện có thật" is found on the revised
/// text, which closes STATUS <c>S67-1</c> (T1 could only read the claims).
/// </para>
/// <para>
/// <b>The lexicon's own contract is not swallowed.</b> A language the release ships no lexicon for is a broken
/// release that fails start-up (D5), so no gate ever reaches this path with one; if it did, the
/// <see cref="InvalidOperationException"/> is a defect in the release, not a verdict.
/// </para>
/// </remarks>
/// <param name="lexicon">The release's framing phrases, one list per language.</param>
public sealed class RealWorldSignalService(IRealWorldLexicon lexicon) : IRealWorldSignalService
{
    /// <inheritdoc />
    public RealWorldSignal? Detect(
        StoryContentV1? content,
        Language language,
        IReadOnlyList<RealWorldClaim>? modelClaims,
        RealWorldOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (modelClaims is null)
        {
            // F6: only a review that stored the member can assert "no claims", so an unreadable one is unknown.
            return null;
        }

        var segments = StoryTextSegments.From(content);
        if (segments.Count == 0)
        {
            // Nothing to detect in: a detector over no text would report "clean fiction" for a story it never read.
            return null;
        }

        return RealWorldSignalDetector.Detect(segments, lexicon.PhrasesFor(language), modelClaims, options);
    }
}
