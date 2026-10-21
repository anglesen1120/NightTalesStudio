namespace NightTales.RenderNode.Server;

/// <summary>
/// The <c>{task_id}</c> guard of 05.4.1-T2 §1: the route value must be a UUID text-format string
/// (<c>D</c> format) <em>before</em> any store lookup, so a non-UUID id answers
/// <c>404 adapter_task_unknown</c> without touching the store and cannot be used to probe it.
/// </summary>
public static class TaskIdGuard
{
    private const string Format = "D";

    /// <summary>True when <paramref name="raw"/> is exactly one UUID in the <c>D</c> format.</summary>
    public static bool TryParse(string? raw, out Guid taskId) => Guid.TryParseExact(raw, Format, out taskId);
}
