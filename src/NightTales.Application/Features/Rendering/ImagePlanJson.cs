using System.Text.Json.Nodes;
using NightTales.Domain.Rendering.Images;

namespace NightTales.Application.Features.Rendering;

/// <summary>
/// Serialises the <c>render_jobs.params.image_plan</c> document (03.2 plan §3.2). Only counts, hint codes and
/// segment addresses are written - never keyword text (P8): the type reads no <see cref="PlannedTerm.Keyword"/>.
/// </summary>
public static class ImagePlanJson
{
    /// <summary>The document of one plan.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="stylePromptVersion">The <c>style=…;neg=…</c> prompt version string of the style template (03.1.1).</param>
    /// <returns>Compact JSON.</returns>
    public static string Serialize(RenderImagePlan plan, string stylePromptVersion)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(stylePromptVersion);

        var hints = new JsonArray();
        foreach (var hint in plan.HintCodes)
        {
            hints.Add(hint);
        }

        var slotSegments = new JsonArray();
        foreach (var term in plan.Terms)
        {
            slotSegments.Add(term.Segment);
        }

        return new JsonObject
        {
            ["clip_seconds"] = plan.ClipSeconds,
            ["needed"] = plan.NeededImages,
            ["max_terms"] = plan.MaxTerms,
            ["planned"] = plan.Terms.Count,
            ["trimmed"] = plan.TrimmedCount,
            ["hints"] = hints,
            ["slot_segments"] = slotSegments,
            ["style_prompt_version"] = stylePromptVersion,
        }.ToJsonString();
    }
}
