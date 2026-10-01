using System.Text.Json.Serialization;

namespace NightTales.RenderNode.Contract;

/// <summary>
/// The <c>render-submit.v1</c> body of 05.4.1-T2 §3: the envelope the backend sends to
/// <c>POST /v1/renders</c>. <see cref="Mpt"/> is forwarded to MPT (plus <c>n_threads</c>, which the
/// adapter adds); <see cref="ImagePlan"/> bounds the image relay and is never sent to MPT (03 D4).
/// </summary>
/// <param name="Schema">Always <see cref="RenderNodeSchemas.RenderSubmit"/>.</param>
/// <param name="RenderJobId">The backend's render job id — the idempotency key together with the body sha256.</param>
/// <param name="Mpt">The MPT field set of 05 D3.</param>
/// <param name="ImagePlan">The image-relay plan (03 D4).</param>
public sealed record AdapterSubmitRequestV1(
    [property: JsonPropertyName("schema")][property: JsonRequired] string Schema,
    [property: JsonPropertyName("render_job_id")][property: JsonRequired] Guid RenderJobId,
    [property: JsonPropertyName("mpt")][property: JsonRequired] MptSubmitFields Mpt,
    [property: JsonPropertyName("image_plan")][property: JsonRequired] ImagePlanV1 ImagePlan);

/// <summary>The <c>image_plan</c> block of 05.4.1-T2 §3 (03 D4): scene-prompt hashes and the relay cap.</summary>
/// <param name="MaxImages">The cap the relay enforces on claimed slots.</param>
/// <param name="PromptSha256">One sha256 per planned scene prompt, in send order (= the slot index).</param>
public sealed record ImagePlanV1(
    [property: JsonPropertyName("max_images")][property: JsonRequired] int MaxImages,
    [property: JsonPropertyName("prompt_sha256")][property: JsonRequired] IReadOnlyList<string> PromptSha256);
