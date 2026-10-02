namespace NightTales.RenderNode.Contract;

/// <summary>
/// The ten job outcome codes of 05.4.1-T2 §5 (05 D5). They end up in <c>render_jobs.error_code</c>, never
/// in an HTTP status. <see cref="OutcomeMessages.For"/> is the fixed D15 S4 sentence for each of them.
/// </summary>
public static class RenderOutcomeCodes
{
    public const string TtsFailed = "tts_failed";
    public const string ImageModerated = "image_moderated";
    public const string ImageGenerationFailed = "image_generation_failed";
    public const string RenderAssemblyFailed = "render_assembly_failed";
    public const string RenderNodeError = "render_node_error";
    public const string RenderLost = "render_lost";
    public const string RenderTimeout = "render_timeout";
    public const string RenderOutputInvalid = "render_output_invalid";
    public const string RenderQualityFailed = "render_quality_failed";
    public const string RenderCancelled = "render_cancelled";

    /// <summary>Every outcome code, in the table's order; the fixed message of each one is mandatory (D15 S4).</summary>
    public static readonly IReadOnlyList<string> All =
    [
        TtsFailed,
        ImageModerated,
        ImageGenerationFailed,
        RenderAssemblyFailed,
        RenderNodeError,
        RenderLost,
        RenderTimeout,
        RenderOutputInvalid,
        RenderQualityFailed,
        RenderCancelled,
    ];
}
