namespace NightTales.Worker;

/// <summary>
/// The Worker's own configuration, bound from the <c>Worker</c> section (09.2.6-T1 Outputs).
/// </summary>
public sealed class WorkerOptions
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string SectionName = "Worker";

    /// <summary>The health listener port when <see cref="HealthPort"/> is not set (09.2.6 plan §7).</summary>
    public const int DefaultHealthPort = 8082;

    /// <summary>The port of the Kestrel health listener, bound to loopback only.</summary>
    public int HealthPort { get; set; } = DefaultHealthPort;
}
