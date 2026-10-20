using NightTales.Domain.Common;
using NightTales.Domain.Stories.Gates;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// Everything the content gate checks (02.8.2-T2): the run's owner and ids for the evidence rows, the content
/// addressed by its D22 segment ids, the expanded scene prompts, and the profile whose <c>Moderation</c> block
/// decides the thresholds.
/// </summary>
/// <param name="UserId">The owner of the run: the <c>moderation_events</c> rows carry it.</param>
/// <param name="EpisodeId">The episode the content belongs to; every event row names it.</param>
/// <param name="StoryVersionId">The version the content was read from, for the caller's own logging.</param>
/// <param name="Language">The series' language; the real-world half of T2 reads its lexicon with it.</param>
/// <param name="Segments">
/// The generated content with its D22 addresses (<c>title</c>, <c>hook</c>, <c>p:&lt;i&gt;</c>, <c>ending</c>,
/// <c>meta</c>); the narration is exactly the non-<c>meta</c> segments in this order.
/// </param>
/// <param name="ExpandedScenePrompts">
/// One moderation text per story keyword, in keyword order (<see cref="ScenePromptExpander"/> produces them from
/// the style template); index <c>i</c> is addressed as <c>scene:i</c> in the verdict and the evidence rows.
/// </param>
/// <param name="StyleTemplateName">The style template the prompts were expanded from; logging and evidence only.</param>
/// <param name="Profile">The resolved gate profile; its <see cref="AutopilotGateProfile.Moderation"/> block decides.</param>
public sealed record AutopilotContentGateRequest(
    Guid UserId,
    Guid EpisodeId,
    Guid StoryVersionId,
    Language Language,
    StoryTextSegments Segments,
    IReadOnlyList<string> ExpandedScenePrompts,
    string StyleTemplateName,
    AutopilotGateProfile Profile);
