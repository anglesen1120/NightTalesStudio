namespace NightTales.Domain.Stories.Length;

/// <summary>
/// The resolved length contract of one writing/reviewing/revising call (02 README D21, D4): the token
/// and segment bands of the tier × language cell plus the three <c>max_output_tokens</c> caps the
/// pipeline sends. Reproduced verbatim from D21; <see cref="LengthTargetResolver"/> resolves it from the
/// active price version's limits.
/// </summary>
/// <param name="MinTokens">The lower bound of the token band (D4; a vi token is a syllable).</param>
/// <param name="MaxTokens">The upper bound of the token band.</param>
/// <param name="MinSegments">The lower bound of the segment band (hook + paragraphs + ending).</param>
/// <param name="MaxSegments">The upper bound of the segment band.</param>
/// <param name="WriterMaxOutput">The <c>max_output_tokens</c> of the writing call.</param>
/// <param name="ReviewMaxOutput">The <c>max_output_tokens</c> of the reviewing call.</param>
/// <param name="RevisionMaxOutput">The <c>max_output_tokens</c> of the revising call.</param>
public sealed record LengthTarget(
    int MinTokens,
    int MaxTokens,
    int MinSegments,
    int MaxSegments,
    int WriterMaxOutput,
    int ReviewMaxOutput,
    int RevisionMaxOutput);
