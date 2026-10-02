using NightTales.RenderNode.Contract;

namespace NightTales.RenderNode.Server;

/// <summary>
/// The fixed ≤ 200-character text of every <c>adapter_*</c> error code (05.4.1-T2 §1: <c>message</c> never
/// carries script, prompt, path or upstream text, D15 S4). One sentence per code, so every consumer shows the
/// same words and a code can never leak node internals through its message.
/// </summary>
internal static class AdapterErrorMessages
{
    internal static string For(string code) => code switch
    {
        AdapterErrorCodes.Unauthorized => "The x-api-key header is missing, duplicated or not accepted.",
        AdapterErrorCodes.AuthThrottled => "Too many rejected authentication attempts from this address; retry after the given delay.",
        AdapterErrorCodes.BodyTooLarge => "The request body exceeds the node's limit.",
        AdapterErrorCodes.RouteNotFound => "This route does not exist on the render node.",
        AdapterErrorCodes.TaskUnknown => "The render task is not known to this node.",
        AdapterErrorCodes.ContractViolation => "The request body violates the render-node contract.",
        AdapterErrorCodes.IdempotencyConflict => "This render job id was already submitted with a different body.",
        AdapterErrorCodes.CapacityFull => "The render node is at capacity; retry after the given delay.",
        AdapterErrorCodes.NodeUnhealthy => "The render node is not healthy and refuses new work.",
        AdapterErrorCodes.OutputNotReady => "The render output is not ready yet.",
        AdapterErrorCodes.OutputAbsent => "The render task has no such output.",
        AdapterErrorCodes.TaskBusy => "The render task is still running on the node.",
        AdapterErrorCodes.ProbeUnavailable => "The image probe is not available on this node.",
        AdapterErrorCodes.ProbeThrottled => "The image probe budget is spent; retry after the given delay.",
        _ => "The render node failed to process the request.",
    };
}
