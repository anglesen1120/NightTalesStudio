namespace NightTales.RenderNode.Contract;

/// <summary>
/// The lifecycle states of 05.4.1-T2 §4. <c>queued</c> is accepted with MPT progress 0; the other three
/// are terminal, which is what <see cref="IsTerminal"/> answers — the capacity gate counts non-terminal
/// tasks, and the store keeps a terminal row from being overwritten by a late observation.
/// </summary>
public static class RenderStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    /// <summary>Every state, in §4 order.</summary>
    public static readonly IReadOnlyList<string> All = [Queued, Running, Succeeded, Failed, Cancelled];

    /// <summary>True for <c>succeeded</c>, <c>failed</c> and <c>cancelled</c> — the states after which a task never moves again.</summary>
    public static bool IsTerminal(string state) =>
        state is Succeeded or Failed or Cancelled;
}
