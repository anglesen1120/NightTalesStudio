using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightTales.RenderNode.Contract;

/// <summary>
/// The <c>render-node-health.v1</c> body of 05.4.1-T2 §4 and 11 README D7 (additive <c>gpu</c> block,
/// 11 CR-4). <see cref="Status"/> decides the HTTP code (200 <c>ok</c> / 503 <c>error</c>) and
/// <see cref="Reasons"/> is the closed reason set of <see cref="NodeHealthReasons"/>. <see cref="TtsProxy"/>
/// is the unchanged <c>proxy-health.v1</c> body (04 D8) kept byte-for-byte; read it through
/// <see cref="ProxyHealthView"/>.
/// </summary>
public sealed record RenderNodeHealthV1(
    [property: JsonPropertyName("schema")][property: JsonRequired] string Schema,
    [property: JsonPropertyName("status")][property: JsonRequired] string Status,
    [property: JsonPropertyName("reasons")][property: JsonRequired] IReadOnlyList<string> Reasons,
    [property: JsonPropertyName("capacity")][property: JsonRequired] CapacityV1 Capacity,
    [property: JsonPropertyName("mpt")][property: JsonRequired] MptHealthV1 Mpt,
    [property: JsonPropertyName("tts_proxy")][property: JsonRequired] JsonElement TtsProxy,
    [property: JsonPropertyName("image_relay")][property: JsonRequired] ImageRelayHealthV1 ImageRelay,
    [property: JsonPropertyName("assets")][property: JsonRequired] AssetsHealthV1 Assets,
    [property: JsonPropertyName("disk")][property: JsonRequired] DiskHealthV1 Disk,
    [property: JsonPropertyName("gpu")][property: JsonRequired] GpuHealthV1 Gpu,
    [property: JsonPropertyName("adapter")][property: JsonRequired] AdapterInfoV1 Adapter);

/// <summary>Dispatch capacity: <see cref="Accepting"/> is false whenever the node status is not <c>ok</c>.</summary>
public sealed record CapacityV1(
    [property: JsonPropertyName("max_concurrent_tasks")][property: JsonRequired] int MaxConcurrentTasks,
    [property: JsonPropertyName("active_tasks")][property: JsonRequired] int ActiveTasks,
    [property: JsonPropertyName("accepting")][property: JsonRequired] bool Accepting);

/// <summary>MPT liveness and its provisioned media facts (05.4.1-T2 §4, D15 P5).</summary>
public sealed record MptHealthV1(
    [property: JsonPropertyName("ok")][property: JsonRequired] bool Ok,
    [property: JsonPropertyName("commit")][property: JsonRequired] string? Commit,
    [property: JsonPropertyName("config_profile")][property: JsonRequired] string? ConfigProfile,
    [property: JsonPropertyName("started_at")][property: JsonRequired] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("whisper_device")][property: JsonRequired] string? WhisperDevice,
    [property: JsonPropertyName("video_codec")][property: JsonRequired] string? VideoCodec,
    [property: JsonPropertyName("threads_per_task")][property: JsonRequired] int? ThreadsPerTask);

/// <summary>The image relay's in-process state (03 D5).</summary>
public sealed record ImageRelayHealthV1(
    [property: JsonPropertyName("ok")][property: JsonRequired] bool Ok,
    [property: JsonPropertyName("tokens_available")][property: JsonRequired] int TokensAvailable,
    [property: JsonPropertyName("active_plans")][property: JsonRequired] int ActivePlans);

/// <summary>The cached assets check (D15 P1): never re-hashed per request.</summary>
public sealed record AssetsHealthV1(
    [property: JsonPropertyName("ok")][property: JsonRequired] bool Ok,
    [property: JsonPropertyName("font")][property: JsonRequired] string? Font,
    [property: JsonPropertyName("music_revision")][property: JsonRequired] int? MusicRevision,
    [property: JsonPropertyName("music_missing")][property: JsonRequired] IReadOnlyList<string> MusicMissing,
    [property: JsonPropertyName("checked_at")][property: JsonRequired] DateTimeOffset? CheckedAt);

/// <summary>The task dir's disk facts (D20 H16); <c>disk_low</c> is <see cref="UsedPct"/> ≥ 85.</summary>
public sealed record DiskHealthV1(
    [property: JsonPropertyName("ok")][property: JsonRequired] bool Ok,
    [property: JsonPropertyName("task_dir_free_bytes")][property: JsonRequired] long TaskDirFreeBytes,
    [property: JsonPropertyName("used_pct")][property: JsonRequired] int UsedPct);

/// <summary>The additive GPU block (11 README D7, 11 CR-4); a failed sample is <c>ok = false</c> + <c>gpu_unavailable</c>.</summary>
public sealed record GpuHealthV1(
    [property: JsonPropertyName("ok")][property: JsonRequired] bool Ok,
    [property: JsonPropertyName("util_pct")][property: JsonRequired] int? UtilPct,
    [property: JsonPropertyName("mem_used_mb")][property: JsonRequired] long? MemUsedMb,
    [property: JsonPropertyName("mem_total_mb")][property: JsonRequired] long? MemTotalMb,
    [property: JsonPropertyName("temp_c")][property: JsonRequired] int? TempC,
    [property: JsonPropertyName("sampled_at")][property: JsonRequired] DateTimeOffset? SampledAt);

/// <summary>The adapter's own identity, so a health pull names the release it came from.</summary>
public sealed record AdapterInfoV1(
    [property: JsonPropertyName("version")][property: JsonRequired] string Version,
    [property: JsonPropertyName("started_at")][property: JsonRequired] DateTimeOffset StartedAt);
