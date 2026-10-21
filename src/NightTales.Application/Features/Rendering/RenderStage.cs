using NightTales.RenderNode.Contract;

namespace NightTales.Application.Features.Rendering;

/// <summary>
/// The stages of one render (05 README D4, 05 CR-2): the closed set the realtime progress payload and the
/// <c>render_duration_seconds{stage}</c> label use.
/// </summary>
/// <remarks>
/// It is deliberately <em>not</em> the type of <c>render_jobs.stage</c>: that column stores the adapter's own
/// stage text verbatim (BD5, 00 §8), so a stage a later adapter adds still travels instead of being refused.
/// The enum is what a bounded label and a typed payload member are built from.
/// </remarks>
public enum RenderStage
{
    /// <summary>Accepted, not started (<c>queued</c>).</summary>
    Queued,

    /// <summary>Narration synthesis (<c>voice</c>).</summary>
    Voice,

    /// <summary>Subtitle cues (<c>subtitles</c>).</summary>
    Subtitles,

    /// <summary>Scene images (<c>images</c>).</summary>
    Images,

    /// <summary>Video assembly (<c>assembling</c>).</summary>
    Assembling,

    /// <summary>The adapter's own post-processing (<c>checking</c>; the only non-terminal stage that may report 100).</summary>
    Checking,

    /// <summary>The task finished (<c>done</c>).</summary>
    Done,
}

/// <summary>
/// The text of a <see cref="RenderStage"/> on the wire and in a metric label, plus the two readings of an
/// adapter's raw stage text (05 D4's <c>render-status.v1</c>, <see cref="RenderStages"/>).
/// </summary>
public static class RenderStageWire
{
    /// <summary>The label an adapter stage this wave does not know is counted under.</summary>
    public const string Unknown = "unknown";

    /// <summary>The wire and label text of <paramref name="stage"/>.</summary>
    /// <param name="stage">The stage.</param>
    /// <returns>The text of 05.4.1-T2 §4, or <see cref="Unknown"/> for a value outside the enum.</returns>
    public static string Wire(RenderStage stage) => stage switch
    {
        RenderStage.Queued => RenderStages.Queued,
        RenderStage.Voice => RenderStages.Voice,
        RenderStage.Subtitles => RenderStages.Subtitles,
        RenderStage.Images => RenderStages.Images,
        RenderStage.Assembling => RenderStages.Assembling,
        RenderStage.Checking => RenderStages.Checking,
        RenderStage.Done => RenderStages.Done,
        _ => Unknown,
    };

    /// <summary>Reads the adapter's stage text back.</summary>
    /// <param name="stage">The adapter's <c>stage</c> member, or <c>null</c>.</param>
    /// <param name="parsed">The stage, or <see cref="RenderStage.Queued"/> when the text is not one of the seven.</param>
    /// <returns><c>true</c> when the text is a stage of this wave.</returns>
    public static bool TryParse(string? stage, out RenderStage parsed)
    {
        switch (stage)
        {
            case RenderStages.Queued:
                parsed = RenderStage.Queued;

                return true;
            case RenderStages.Voice:
                parsed = RenderStage.Voice;

                return true;
            case RenderStages.Subtitles:
                parsed = RenderStage.Subtitles;

                return true;
            case RenderStages.Images:
                parsed = RenderStage.Images;

                return true;
            case RenderStages.Assembling:
                parsed = RenderStage.Assembling;

                return true;
            case RenderStages.Checking:
                parsed = RenderStage.Checking;

                return true;
            case RenderStages.Done:
                parsed = RenderStage.Done;

                return true;
            default:
                parsed = RenderStage.Queued;

                return false;
        }
    }

    /// <summary>
    /// The stage text a <c>render_progress</c> payload carries: the wire text of a stage this wave knows, the
    /// adapter's own text otherwise, and <see cref="Unknown"/> when the adapter sent none.
    /// </summary>
    /// <remarks>
    /// An unknown stage travels rather than being replaced (09.8.1's <c>RealtimeData.Stage</c> is a string for
    /// exactly that reason), and the result is never blank, which
    /// <c>RealtimeData.ForRenderProgress</c> refuses.
    /// </remarks>
    /// <param name="adapterStage">The adapter's <c>stage</c> member, or <c>null</c>.</param>
    /// <returns>The text the payload carries.</returns>
    public static string ForPayload(string? adapterStage) =>
        TryParse(adapterStage, out var known)
            ? Wire(known)
            : string.IsNullOrWhiteSpace(adapterStage) ? Unknown : adapterStage;

    /// <summary>
    /// The bounded stage label of a metric observation: the wire text of a stage this wave knows, and
    /// <see cref="Unknown"/> for every other text, so the label's cardinality cannot grow with adapter input.
    /// </summary>
    /// <param name="adapterStage">The adapter's <c>stage</c> member, or <c>null</c>.</param>
    /// <returns>The label.</returns>
    public static string ForMetric(string? adapterStage) =>
        TryParse(adapterStage, out var known) ? Wire(known) : Unknown;
}
