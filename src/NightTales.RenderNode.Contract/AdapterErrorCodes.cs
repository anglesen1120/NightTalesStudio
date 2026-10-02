namespace NightTales.RenderNode.Contract;

/// <summary>
/// The closed <c>adapter_*</c> error-code set of 05.4.1-T2 §1 (the HTTP status per code is in that
/// table; the envelope never carries a platform HTTP code as a job outcome, 05 D5/CR-3).
/// </summary>
public static class AdapterErrorCodes
{
    public const string Unauthorized = "adapter_unauthorized";
    public const string AuthThrottled = "adapter_auth_throttled";
    public const string BodyTooLarge = "adapter_body_too_large";
    public const string RouteNotFound = "adapter_route_not_found";
    public const string TaskUnknown = "adapter_task_unknown";
    public const string ContractViolation = "adapter_contract_violation";
    public const string IdempotencyConflict = "adapter_idempotency_conflict";
    public const string CapacityFull = "adapter_capacity_full";
    public const string NodeUnhealthy = "adapter_node_unhealthy";
    public const string OutputNotReady = "adapter_output_not_ready";
    public const string OutputAbsent = "adapter_output_absent";
    public const string TaskBusy = "adapter_task_busy";
    public const string ProbeUnavailable = "adapter_probe_unavailable";
    public const string ProbeThrottled = "adapter_probe_throttled";

    /// <summary>
    /// The last-resort answer for an unexpected server fault: the envelope rule of §1 ("every non-2xx is
    /// an envelope") also covers a 500, and the frozen table has no code for a fault the node never
    /// expects to raise (11.3.5-T1 [INFERENCE]: the code is additive to the table, message only).
    /// </summary>
    public const string InternalError = "adapter_internal_error";
}
