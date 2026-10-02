using ErrorOr;
using NightTales.RenderNode.Contract;

namespace NightTales.RenderNode.Server;

/// <summary>
/// The health seam (11 README D5, D7): returns the cached <c>render-node-health.v1</c> body. It must never
/// compute anything per request — status and health never touch MPT, the disk or the checks (D19 Q1) — so the
/// adapter's implementation reads its caches and the mock scripts a body.
/// </summary>
public interface INodeHealthSource
{
    /// <summary>The current cached health body. A throwing source fails closed to 503 (Failure handling).</summary>
    ValueTask<RenderNodeHealthV1> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The image-plan seam (11 README D5): the adapter binds 03.4.3-T1's relay, the mock binds its scripted
/// report. The core only ever registers a plan before submitting, releases it on cancel/delete/failure, and
/// serves the report stored on the row.
/// </summary>
public interface IImagePlanSink
{
    /// <summary>Bounds the relay to this plan's <c>max_images</c> and accepted prompt hashes (03 D4/D5).</summary>
    void Register(Guid taskId, ImagePlanV1 plan);

    /// <summary>Frees the plan's slots; idempotent per task because the core releases exactly once.</summary>
    void Release(Guid taskId);

    /// <summary>The task's <c>images.v1</c> report, or null when there is nothing to report yet.</summary>
    ImageReportV1? BuildReport(Guid taskId);
}

/// <summary>
/// The fifth seam (12 CR-19): the probe-only path behind <c>POST /v1/probe-image</c>. It is implemented by
/// the MPT backend in 11.3.5-T2 — the same task that owns every OpenAI-facing call — so this core only maps
/// the route, applies the same auth/throttle and defines the contract. Until a host registers one, the route
/// answers <c>503 adapter_probe_unavailable</c> (12.8.3-T3 treats that as fail-closed).
/// </summary>
public interface IImageProbeBackend
{
    Task<ErrorOr<ProbeImageResultV1>> ProbeAsync(CancellationToken cancellationToken);
}
