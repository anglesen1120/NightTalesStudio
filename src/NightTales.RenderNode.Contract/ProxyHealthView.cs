using System.Text.Json;

namespace NightTales.RenderNode.Contract;

/// <summary>
/// The read-side view of the <c>proxy-health.v1</c> body embedded in health (04 D8): the adapter keys off
/// the status string and the reasons, never off the rest of the object. Fail closed by design — a missing,
/// malformed or truncated element is "not ok" with no reason text, so a broken proxy body can never make a
/// node dispatchable. A missing/null <c>reasons</c> or a non-string element is not the same as a valid
/// empty list: the shape itself must be right before <c>Ok</c> can be true.
/// </summary>
public static class ProxyHealthView
{
    private const string StatusProperty = "status";
    private const string ReasonsProperty = "reasons";

    /// <summary>
    /// Reads <c>status</c> and <c>reasons</c>. <c>Ok</c> is true only for <c>status = "ok"</c> with
    /// <c>reasons</c> present as an array of strings and empty (04 D8's own rule, re-checked here so a
    /// producer bug cannot open the gate). Any other shape is not ok.
    /// </summary>
    public static (bool Ok, IReadOnlyList<string> Reasons) From(JsonElement health)
    {
        if (health.ValueKind != JsonValueKind.Object || !TryReadReasons(health, out var reasons))
        {
            return (false, []);
        }

        return (ReadStatus(health) && reasons.Count == 0, reasons);
    }

    private static bool ReadStatus(JsonElement health) =>
        health.TryGetProperty(StatusProperty, out var status)
        && status.ValueKind == JsonValueKind.String
        && status.ValueEquals(RenderNodeHealthStatuses.Ok);

    /// <summary>
    /// Reads <c>reasons</c> only when it is present as an array whose every element is a string. A missing
    /// member, a null and a non-string element all answer false, so the caller fails closed.
    /// </summary>
    private static bool TryReadReasons(JsonElement health, out IReadOnlyList<string> reasons)
    {
        reasons = [];
        if (!health.TryGetProperty(ReasonsProperty, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsed = new List<string>();
        foreach (var reason in value.EnumerateArray())
        {
            if (reason.ValueKind != JsonValueKind.String)
            {
                reasons = [];
                return false;
            }

            parsed.Add(reason.GetString()!);
        }

        reasons = parsed;
        return true;
    }
}
