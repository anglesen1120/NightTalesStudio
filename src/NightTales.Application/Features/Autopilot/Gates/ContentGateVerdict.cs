namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// The content gate's verdict (02.8.2-T2): what check 7 of the story gate stores and what routes a failing story
/// to the owner. Ids and codes only — a category name is a provider vocabulary word, never story text (D19).
/// </summary>
/// <remarks>
/// <para>
/// <b>Flagged is the verdict; Categories is the evidence.</b> A provider flag whose score stays under the
/// profile's ceiling is not a failing chunk, so its category never reaches <see cref="Categories"/>: the caller
/// reads <see cref="Flagged"/> and never infers a verdict from a list being non-empty.
/// </para>
/// <para>
/// <b>Fail closed.</b> A moderator outage and an empty request are both <see cref="Flagged"/> with
/// <see cref="UnavailableDetail"/> set; nothing moderated is not a pass.
/// </para>
/// </remarks>
/// <param name="Flagged">Whether the story gate fails check 7.</param>
/// <param name="Categories">
/// The categories the story failed on: the provider's flagged categories, plus every category whose score
/// breached its ceiling without being flagged. Empty on a pass.
/// </param>
/// <param name="Scores">The union's highest score per category, as the moderator reported them (evidence, 0…1).</param>
/// <param name="UnavailableDetail">
/// <c>moderator_unavailable</c> when the endpoint failed after the retry budget, or
/// <c>field_absent</c> when there was nothing to moderate; <see langword="null"/> otherwise.
/// </param>
/// <param name="FlaggedSegments">
/// The D22 ids of the segments the failure was attributed to (<c>title</c>, <c>hook</c>, <c>p:&lt;i&gt;</c>,
/// <c>ending</c>, <c>meta</c>) plus <c>scene:&lt;i&gt;</c> for a flagged scene prompt, in content order and
/// without duplicates. Ids only.
/// </param>
public sealed record ContentGateVerdict(
    bool Flagged,
    IReadOnlyList<string> Categories,
    IReadOnlyDictionary<string, double> Scores,
    string? UnavailableDetail,
    IReadOnlyList<string> FlaggedSegments);
