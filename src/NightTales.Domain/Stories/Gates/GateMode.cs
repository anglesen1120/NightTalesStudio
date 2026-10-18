namespace NightTales.Domain.Stories.Gates;

/// <summary>
/// Whether an autopilot gate evaluation may approve (02.8.2 plan §3.3, §3.4). <see cref="DryRun"/> records the
/// verdict and notifies the owner but never calls an approve command, which is what T3's calibration needs
/// (plan §11 rollback: set <c>Autopilot:Gates:DryRun</c> and nothing else changes).
/// </summary>
public enum GateMode
{
    /// <summary>A pass approves through the same commands a user calls, as a system actor.</summary>
    Live,

    /// <summary>A pass only records the verdict (<c>Autopilot:Gates:DryRun = true</c>).</summary>
    DryRun,
}

/// <summary>The stored <c>gates.v1</c> name of a <see cref="GateMode"/>.</summary>
public static class GateModeNames
{
    /// <summary>The wire name of <paramref name="mode"/>, <c>live</c> or <c>dry_run</c> (plan §3.4).</summary>
    /// <param name="mode">The gate mode.</param>
    /// <returns>The stored name.</returns>
    public static string ToWire(this GateMode mode) => mode == GateMode.DryRun ? "dry_run" : "live";
}
