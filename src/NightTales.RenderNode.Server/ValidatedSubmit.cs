using NightTales.RenderNode.Contract;

namespace NightTales.RenderNode.Server;

/// <summary>
/// What a submit body must be after validation: the idempotency key, the MPT field set and the image plan.
/// The submit path uses this type and never touches the raw body again, so <c>image_plan</c> cannot reach MPT
/// and <c>n_threads</c> stays the adapter's business (05 D3/D4, 03 D4).
/// </summary>
public sealed record ValidatedSubmit(Guid RenderJobId, MptSubmitFields Mpt, ImagePlanV1 ImagePlan);
