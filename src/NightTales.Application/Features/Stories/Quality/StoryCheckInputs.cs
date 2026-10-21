using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering.Images;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Keywords;
using NightTales.Domain.Stories.Quality;

namespace NightTales.Application.Features.Stories.Quality;

/// <summary>
/// The inputs the two production callers of <see cref="StoryQualityChecker.Check"/> build besides the draft
/// itself (03.2.2-T2, 03 CR-14): the render image plan whose hint codes surface as advisory issues in
/// <c>audit_report.local.issues</c>.
/// </summary>
/// <remarks>
/// <para>
/// Pure and total (02.3.4-T1): the segments are the <see cref="SegmentKeywords"/> list the render builds —
/// <c>hook</c>, then <c>p:&lt;i&gt;</c> in story order, then <c>ending</c> — so the edit hints and the render
/// plan count the same keywords in the same order. A partial edit draft (a null segment, a null paragraph or a
/// null keyword list) yields the plan of what is there and never throws; the validator reports the structural
/// problems, this helper only sizes the video.
/// </para>
/// <para>
/// The options are <see cref="ImagePlanOptions.Defaults"/> as a whole, exactly as
/// <c>RenderEpisodeCommandHandler</c> does: only the plan's hint codes are read here, and
/// <c>Story:Length:Rate:{language}</c> is not bound to configuration anywhere (03 D3/D8).
/// </para>
/// </remarks>
public static class StoryCheckInputs
{
    /// <summary>Plans the images of one draft's own keywords, or null when the episode has no length tier.</summary>
    /// <param name="draft">The draft as the caller holds it; a partial draft is tolerated and never throws.</param>
    /// <param name="language">The series language, which picks the speaking rate of the plan.</param>
    /// <param name="tier">The episode's length tier, or null when none is chosen (Q-TIER-01 skips the plan).</param>
    /// <returns>The plan whose <see cref="RenderImagePlan.HintCodes"/> become advisory issues, or null.</returns>
    public static RenderImagePlan? PlanFor(StoryContentV1 draft, Language language, LengthTier? tier)
    {
        if (draft is null || tier is not { } chosen)
        {
            return null;
        }

        // The D3 video script is the derived body, the same token count the render plans on (02 D4).
        return RenderImagePlanner.Plan(new RenderImagePlanInput(
            Segments(draft),
            DerivedStoryFields.From(draft).WordCount,
            language,
            chosen,
            ImagePlanOptions.Defaults));
    }

    /// <summary>The story's segments with their keywords, in the 02 D22 address order the render reads.</summary>
    private static List<SegmentKeywords> Segments(StoryContentV1 draft)
    {
        var paragraphs = draft.Paragraphs ?? [];
        var segments = new List<SegmentKeywords>(paragraphs.Count + 2)
        {
            new SegmentKeywords(SegmentAddress.Hook, draft.Hook?.Keywords ?? []),
        };
        for (var index = 0; index < paragraphs.Count; index++)
        {
            segments.Add(new SegmentKeywords(SegmentAddress.Paragraph(index), paragraphs[index]?.Keywords ?? []));
        }

        segments.Add(new SegmentKeywords(SegmentAddress.Ending, draft.Ending?.Keywords ?? []));

        return segments;
    }
}
