using NightTales.Domain.Stories.Structure;

namespace NightTales.Domain.Autopilot.Ideas;

/// <summary>
/// One idea the model proposed for a series (02.8.1 plan §3.2 <c>ideas.v1</c>): the four text fields of the
/// idea and its D3 structure block, before <see cref="IdeaValidator"/> has accepted it and before anything
/// has scored it.
/// </summary>
/// <remarks>
/// This is the response shape, not a row: the validator drops a violating candidate, and a surviving one is
/// persisted by <see cref="EpisodeIdea.CreateProposed"/>. The four texts are free text; the structure tags
/// are the D3 vocabulary (or <c>other:&lt;text&gt;</c>), which the shared 02.2.1-T1 mapper writes.
/// </remarks>
/// <param name="Title">The episode title as proposed (≤ 100 characters).</param>
/// <param name="Logline">The one-line premise (≤ 300 characters).</param>
/// <param name="Hook">The opening hook (≤ 300 characters).</param>
/// <param name="Setting">The setting in words; the setting <em>tag</em> lives in <paramref name="Structure"/> (≤ 120 characters).</param>
/// <param name="Structure">The D3 structure block of the idea, its <c>beats</c> included.</param>
/// <param name="Beats">The outline as beat names in narrative order (3–8, at least one <c>twist</c> or <c>reveal</c>).</param>
public sealed record IdeaCandidate(
    string Title,
    string Logline,
    string Hook,
    string Setting,
    StoryStructureTags Structure,
    IReadOnlyList<string> Beats);
