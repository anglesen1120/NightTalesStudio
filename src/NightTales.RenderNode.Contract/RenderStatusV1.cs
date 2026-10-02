using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightTales.RenderNode.Contract;

/// <summary>
/// The <c>render-status.v1</c> body of 05.4.1-T2 §4: the only shape <c>GET /v1/renders/{task_id}</c>
/// returns. <see cref="Check"/> and <see cref="Images"/> stay <see cref="JsonElement"/> because they are
/// by-reference blocks owned elsewhere (05 D9 and 03 D6); the adapter stores and re-serves them verbatim.
/// </summary>
public sealed record RenderStatusV1(
    [property: JsonPropertyName("schema")][property: JsonRequired] string Schema,
    [property: JsonPropertyName("task_id")][property: JsonRequired] Guid TaskId,
    [property: JsonPropertyName("render_job_id")][property: JsonRequired] Guid RenderJobId,
    [property: JsonPropertyName("state")][property: JsonRequired] string State,
    [property: JsonPropertyName("stage")][property: JsonRequired] string Stage,
    [property: JsonPropertyName("percent")][property: JsonRequired] int Percent,
    [property: JsonPropertyName("cancel_requested")][property: JsonRequired] bool CancelRequested,
    [property: JsonPropertyName("submitted_at")][property: JsonRequired] DateTimeOffset SubmittedAt,
    [property: JsonPropertyName("started_at")][property: JsonRequired] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("finished_at")][property: JsonRequired] DateTimeOffset? FinishedAt,
    [property: JsonPropertyName("outcome")][property: JsonRequired] RenderOutcomeV1? Outcome,
    [property: JsonPropertyName("output")][property: JsonRequired] RenderOutputV1? Output,
    [property: JsonPropertyName("check")][property: JsonRequired] JsonElement? Check,
    [property: JsonPropertyName("images")][property: JsonRequired] JsonElement? Images,
    [property: JsonPropertyName("mpt")][property: JsonRequired] MptStateV1? Mpt);

/// <summary>The <c>output</c> block of a successful status (05.4.1-T2 §4): measured file facts only.</summary>
public sealed record RenderOutputV1(
    [property: JsonPropertyName("video")][property: JsonRequired] RenderFileV1 Video,
    [property: JsonPropertyName("subtitles")][property: JsonRequired] RenderFileV1? Subtitles,
    [property: JsonPropertyName("narration_seconds")][property: JsonRequired] double NarrationSeconds);

/// <summary>One served artefact. <see cref="Cues"/> is set for subtitles only.</summary>
public sealed record RenderFileV1(
    [property: JsonPropertyName("bytes")][property: JsonRequired] long Bytes,
    [property: JsonPropertyName("sha256")][property: JsonRequired] string Sha256,
    // A video block carries only bytes+sha256; the cue count belongs to the subtitles block.
    [property: JsonPropertyName("cues")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Cues);

/// <summary>
/// The <c>outcome</c> block of a failed status: a 05 D5 outcome code, a closed-enum detail and the fixed
/// D15 S4 sentence from <see cref="OutcomeMessages"/>.
/// </summary>
public sealed record RenderOutcomeV1(
    [property: JsonPropertyName("code")][property: JsonRequired] string Code,
    [property: JsonPropertyName("detail")][property: JsonRequired] string? Detail,
    [property: JsonPropertyName("message")][property: JsonRequired] string Message);

/// <summary>
/// The raw MPT observables carried by every status (05.4.1-T2 §4). <see cref="State"/> is null for
/// <c>render_lost</c>, where the last observed values are reported with no MPT state (§10 T63) — the
/// frozen fixture <c>render-status.v1.lost.json</c> is the proof.
/// </summary>
public sealed record MptStateV1(
    [property: JsonPropertyName("state")][property: JsonRequired] int? State,
    [property: JsonPropertyName("progress")][property: JsonRequired] int Progress,
    [property: JsonPropertyName("failed_stage")][property: JsonRequired] string? FailedStage,
    [property: JsonPropertyName("warnings")][property: JsonRequired] IReadOnlyList<string> Warnings);
