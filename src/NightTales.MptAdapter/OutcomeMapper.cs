using NightTales.RenderNode.Contract;

namespace NightTales.MptAdapter;

/// <summary>
/// The two mapping tables of the poller, kept in one place because both answer "what does this MPT observation
/// mean?" under 05 D4/D5: the <c>failed_stage</c> → outcome code map, and the lost reasons.
/// </summary>
/// <remarks>
/// The mapping is on MPT's <c>failed_stage</c> and never on its <c>error</c> text (05 C8 correction in
/// <c>mpt-api.md</c> S10): the text is free-form, the stage is a closed set. The fixed D15 S4 sentence comes
/// from <see cref="OutcomeMessages"/> — MPT's own words stay in the journal (S4).
/// </remarks>
public static class OutcomeMapper
{
    /// <summary>05 D5's closed detail length; a longer stage name is truncated, never dropped.</summary>
    public const int MaxDetailLength = 64;

    /// <summary>D6's first lost rule: MPT answers 404 for a task it accepted.</summary>
    public const string MptTaskMissing = "mpt_task_missing";

    /// <summary>D6's second lost rule: MPT's process start is newer than the task's <c>backend_started_at</c>.</summary>
    public const string MptRestarted = "mpt_restarted";

    /// <summary><c>failed_stage</c> was absent, so the adapter cannot say where MPT stopped.</summary>
    public const string UnknownStage = "unknown_stage";

    /// <summary>MPT reported a numeric state outside the pinned <c>-1/1/4</c> set (05 C6).</summary>
    public const string UnknownState = "unknown_state";

    /// <summary>
    /// 05 D5: <c>failed_stage</c> → the adapter outcome. <c>audio</c>, <c>materials</c> and <c>video</c> keep
    /// their own codes (03.4.2-T1 reclassifies a <c>materials</c> failure from <c>images.v1</c>); every other
    /// stage — including <c>preflight</c>, <c>script</c>, <c>terms</c> and <c>pipeline</c> — is a node error
    /// with the stage as its detail.
    /// </summary>
    public static RenderOutcomeV1 MapFailure(string? failedStage)
    {
        // SEC-11.3.5-T2-02: the stage is MPT-supplied free text on the unknown-stage path, so it is
        // neutralised and bounded before it becomes a stored detail or a log argument (T69's closed enum).
        var stage = string.IsNullOrWhiteSpace(failedStage)
            ? null
            : AdapterText.Sanitize(failedStage.Trim(), MaxDetailLength);

        var (code, detail) = stage switch
        {
            null => (RenderOutcomeCodes.RenderNodeError, UnknownStage),
            "audio" => (RenderOutcomeCodes.TtsFailed, stage),
            "materials" => (RenderOutcomeCodes.ImageGenerationFailed, stage),
            "video" => (RenderOutcomeCodes.RenderAssemblyFailed, stage),
            "preflight" or "script" or "terms" or "pipeline" => (RenderOutcomeCodes.RenderNodeError, stage),
            _ => (RenderOutcomeCodes.RenderNodeError, stage),
        };

        return new RenderOutcomeV1(code, detail, OutcomeMessages.For(code));
    }

    /// <summary>D6's lost outcome: <c>render_lost</c> with the reason as its detail (05 D5).</summary>
    public static RenderOutcomeV1 Lost(string reason) =>
        new(RenderOutcomeCodes.RenderLost, reason, OutcomeMessages.For(RenderOutcomeCodes.RenderLost));

    /// <summary>05 D4's last resort for a state the mapping does not know.</summary>
    public static RenderOutcomeV1 UnknownStateError() =>
        new(RenderOutcomeCodes.RenderNodeError, UnknownState, OutcomeMessages.For(RenderOutcomeCodes.RenderNodeError));
}
