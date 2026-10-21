using System.Text.Json.Serialization;

namespace NightTales.RenderNode.Contract;

/// <summary>
/// The <c>images.v1</c> report of 03 D6 (05.4.1-T2 §5 by reference): what the node's image relay did for
/// one render task. The relay fills it from its own JSONL; <see cref="Needed"/> is null from the relay
/// (it cannot know the audio length) and is filled by the backend before classifying (03.4.2-T1).
/// </summary>
public sealed record ImageReportV1(
    [property: JsonPropertyName("schema")][property: JsonRequired] string Schema,
    [property: JsonPropertyName("planned")][property: JsonRequired] int Planned,
    [property: JsonPropertyName("needed")][property: JsonRequired] int? Needed,
    [property: JsonPropertyName("called")][property: JsonRequired] int Called,
    [property: JsonPropertyName("succeeded")][property: JsonRequired] int Succeeded,
    [property: JsonPropertyName("failed")][property: JsonRequired] IReadOnlyList<ImageFailureV1> Failed,
    [property: JsonPropertyName("unattempted")][property: JsonRequired] int Unattempted,
    [property: JsonPropertyName("quality_sent")][property: JsonRequired] string? QualitySent,
    [property: JsonPropertyName("quality_echoed")][property: JsonRequired] IReadOnlyList<string> QualityEchoed,
    [property: JsonPropertyName("size")][property: JsonRequired] string? Size,
    [property: JsonPropertyName("model")][property: JsonRequired] string? Model,
    [property: JsonPropertyName("usage")][property: JsonRequired] IReadOnlyList<ImageUsageV1> Usage,
    [property: JsonPropertyName("relay_rejections")][property: JsonRequired] ImageRelayRejectionsV1 RelayRejections);

/// <summary>
/// One final image failure (after MPT's and the relay's retries). <see cref="ErrorCode"/> carries the
/// relay's closed code set (03 D5) — <c>moderation_blocked</c> is the one 03 D7 classifies as moderation.
/// </summary>
public sealed record ImageFailureV1(
    [property: JsonPropertyName("slot")][property: JsonRequired] int Slot,
    [property: JsonPropertyName("http_status")][property: JsonRequired] int? HttpStatus,
    [property: JsonPropertyName("error_code")][property: JsonRequired] string ErrorCode);

/// <summary>One billed image call; <see cref="Estimated"/> is true when the upstream returned no usage body.</summary>
public sealed record ImageUsageV1(
    [property: JsonPropertyName("slot")][property: JsonRequired] int Slot,
    [property: JsonPropertyName("request_id")][property: JsonRequired] string RequestId,
    [property: JsonPropertyName("text_tokens")][property: JsonRequired] int TextTokens,
    [property: JsonPropertyName("image_tokens")][property: JsonRequired] int ImageTokens,
    [property: JsonPropertyName("output_tokens")][property: JsonRequired] int OutputTokens,
    [property: JsonPropertyName("estimated")][property: JsonRequired] bool Estimated);

/// <summary>The relay's own refusal counters (03 D5); node-internal, never a platform HTTP code.</summary>
public sealed record ImageRelayRejectionsV1(
    [property: JsonPropertyName("relay_prompt_unstyled")][property: JsonRequired] int RelayPromptUnstyled,
    [property: JsonPropertyName("relay_unknown_prompt")][property: JsonRequired] int RelayUnknownPrompt,
    [property: JsonPropertyName("relay_image_cap_reached")][property: JsonRequired] int RelayImageCapReached,
    [property: JsonPropertyName("relay_rate_limited")][property: JsonRequired] int RelayRateLimited);
