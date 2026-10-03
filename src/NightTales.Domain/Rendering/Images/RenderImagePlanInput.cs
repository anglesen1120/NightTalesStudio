using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Keywords;

namespace NightTales.Domain.Rendering.Images;

/// <summary>What <see cref="RenderImagePlanner.Plan"/> needs: the story's keywords and its narration size.</summary>
/// <param name="Segments">The segments with their keywords, in story order.</param>
/// <param name="ScriptTokens"><c>StoryText.Tokenize(videoScript).Count</c> (02 D4 token rule).</param>
/// <param name="Language">The narration language; picks the speaking rate.</param>
/// <param name="Tier">The episode's length tier; its maximum minutes cap the image count.</param>
/// <param name="Options">The planner options.</param>
public sealed record RenderImagePlanInput(
    IReadOnlyList<SegmentKeywords> Segments,
    int ScriptTokens,
    Language Language,
    LengthTier Tier,
    ImagePlanOptions Options);
