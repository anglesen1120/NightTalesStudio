using System.Text.Json.Serialization;

namespace NightTales.RenderNode.Contract;

/// <summary>
/// The error envelope of 05.4.1-T2 §1: every non-2xx answer of the render-node interface is exactly this
/// shape. <c>message</c> is ≤ 200 characters and never carries script, prompt, path or upstream text
/// (D15 S4); <c>errors</c> is present only on validation answers.
/// </summary>
public sealed record AdapterErrorV1(
    [property: JsonPropertyName("error")][property: JsonRequired] AdapterErrorBody Error);

/// <summary>The envelope body.</summary>
public sealed record AdapterErrorBody(
    [property: JsonPropertyName("code")][property: JsonRequired] string Code,
    [property: JsonPropertyName("message")][property: JsonRequired] string Message,
    [property: JsonPropertyName("errors")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<AdapterFieldErrorV1>? Errors);

/// <summary>
/// One validation violation: <c>field</c> echoes a caller key name only when it matches
/// <c>^[a-z_]{1,64}$</c> (anything else becomes <c>&lt;redacted&gt;</c> or its parent field, §10 T69), and
/// <c>rule</c> is the closed rule name the validator reported.
/// </summary>
public sealed record AdapterFieldErrorV1(
    [property: JsonPropertyName("field")][property: JsonRequired] string Field,
    [property: JsonPropertyName("rule")][property: JsonRequired] string Rule);

/// <summary>The <c>202</c>/<c>200</c> body of <c>POST /v1/renders</c>.</summary>
public sealed record AdapterAcceptedV1(
    [property: JsonPropertyName("task_id")][property: JsonRequired] Guid TaskId,
    [property: JsonPropertyName("state")][property: JsonRequired] string State);

/// <summary>
/// The body of <c>POST /v1/probe-image</c> (12 CR-19). The frozen contract ships no probe fixture and no
/// schema literal, so <see cref="Schema"/> and <see cref="ErrorCode"/> are written only when a probe
/// backend sets them; <see cref="ErrorCode"/> is the closed reason enum of 05.4.1-T2 §2 on the wire
/// (<c>reason</c>), never raw provider text.
/// </summary>
public sealed record ProbeImageResultV1(
    [property: JsonPropertyName("schema")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Schema,
    [property: JsonPropertyName("funded")][property: JsonRequired] bool Funded,
    [property: JsonPropertyName("reason")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorCode);
