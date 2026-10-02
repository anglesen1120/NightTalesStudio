namespace NightTales.RenderNode.Contract;

/// <summary>
/// The fixed outcome sentences of 05.4.1-T2 §5 (D15 S4): one ≤ 200-character sentence per outcome code,
/// with no script, prompt, path or upstream text. The adapter puts the sentence of the mapped code into
/// <see cref="RenderOutcomeV1.Message"/>, so every consumer shows the same words.
/// </summary>
public static class OutcomeMessages
{
    private const string UnknownCodeMessage = "The render failed on the node.";

    /// <summary>The fixed sentence for a 05 D5 outcome code. An unknown code gets the generic sentence (never an exception).</summary>
    public static string For(string outcomeCode) => outcomeCode switch
    {
        RenderOutcomeCodes.TtsFailed => "Text-to-speech synthesis failed on the node.",
        RenderOutcomeCodes.ImageModerated => "One or more scene prompts were rejected by the image provider's moderation.",
        RenderOutcomeCodes.ImageGenerationFailed => "Image generation failed for all required scenes.",
        RenderOutcomeCodes.RenderAssemblyFailed => "The node failed to assemble the video from the generated material.",
        RenderOutcomeCodes.RenderNodeError => "The render node failed before media generation (internal stage).",
        RenderOutcomeCodes.RenderLost => "The render task is no longer known to the node (MPT restart or lost task state).",
        RenderOutcomeCodes.RenderTimeout => "The render did not reach a terminal state within the configured time limit.",
        RenderOutcomeCodes.RenderOutputInvalid => "The node output failed integrity or type checks after download.",
        RenderOutcomeCodes.RenderQualityFailed => "The video failed the render-quality policy.",
        RenderOutcomeCodes.RenderCancelled => "The render was cancelled by the user.",
        _ => UnknownCodeMessage,
    };
}
