using System.Text.Json.Serialization;
using NightTales.Domain.Rendering.Images;

namespace NightTales.Domain.Rendering;

/// <summary>
/// The <c>image_plan</c> block of <c>render-params.v1</c> (03.2 plan §3.2, P8): counts, hint codes and segment
/// addresses only — never keyword or prompt text. Same document <c>ImagePlanJson</c> writes; 03.4.2-T1 reads
/// <see cref="Needed"/> and <see cref="SlotSegments"/> back.
/// </summary>
/// <param name="ClipSeconds">Seconds per image.</param>
/// <param name="Needed">The images the narration needs.</param>
/// <param name="MaxTerms">The term cap, also the relay's <c>max_images</c>.</param>
/// <param name="Planned">The terms planned after the cap.</param>
/// <param name="Trimmed">The keywords the cap removed.</param>
/// <param name="Hints">The advisory hint codes.</param>
/// <param name="SlotSegments">The owning segment address of every slot, in slot order.</param>
/// <param name="StylePromptVersion">The style prompt version the plan was made for.</param>
public sealed record ImagePlanSummary(
    [property: JsonPropertyName("clip_seconds")][property: JsonRequired] int ClipSeconds,
    [property: JsonPropertyName("needed")][property: JsonRequired] int Needed,
    [property: JsonPropertyName("max_terms")][property: JsonRequired] int MaxTerms,
    [property: JsonPropertyName("planned")][property: JsonRequired] int Planned,
    [property: JsonPropertyName("trimmed")][property: JsonRequired] int Trimmed,
    [property: JsonPropertyName("hints")][property: JsonRequired] IReadOnlyList<string> Hints,
    [property: JsonPropertyName("slot_segments")][property: JsonRequired] IReadOnlyList<string> SlotSegments,
    [property: JsonPropertyName("style_prompt_version")][property: JsonRequired] string StylePromptVersion)
{
    /// <summary>Projects <paramref name="plan"/>; reads no <see cref="PlannedTerm.Keyword"/>.</summary>
    /// <param name="plan">The planner's output.</param>
    /// <param name="stylePromptVersion">The <c>style=…;neg=…</c> version string.</param>
    public static ImagePlanSummary From(RenderImagePlan plan, string stylePromptVersion)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(stylePromptVersion);

        return new ImagePlanSummary(
            plan.ClipSeconds,
            plan.NeededImages,
            plan.MaxTerms,
            plan.Terms.Count,
            plan.TrimmedCount,
            [.. plan.HintCodes],
            [.. plan.Terms.Select(term => term.Segment)],
            stylePromptVersion);
    }
}
