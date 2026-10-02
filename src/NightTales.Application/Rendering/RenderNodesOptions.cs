namespace NightTales.Application.Rendering;

/// <summary>
/// The <c>RenderNodes</c> configuration section (11 README D13): the poll cadence and hysteresis, and the
/// nodes the <c>WorkerNodeSeeder</c> upserts — in P the only registration path, because the settings UI
/// that would edit <c>worker_nodes</c> is S (F09/P4).
/// </summary>
/// <remarks>
/// The adapter key is deliberately <em>not</em> part of this object: it lives in the secret manager under
/// <c>RENDER_NODE_&lt;NAME&gt;_API_KEY</c> and is read per node through the configuration (see
/// <see cref="RenderNodeApiKey"/>), so a key never lands in a bound options object that a log or a
/// diagnostic dump could echo.
/// </remarks>
public sealed class RenderNodesOptions
{
    /// <summary>The configuration section this type binds (D13).</summary>
    public const string SectionName = "RenderNodes";

    /// <summary>How long one pull may take before it counts as no answer (D13's per-pull timeout).</summary>
    public static readonly TimeSpan PullTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How many nodes are polled at once, so one slow node cannot delay the tick (D13, bounded work).</summary>
    public const int MaxConcurrentPulls = 4;

    /// <summary>The poll interval in seconds (D13: 30).</summary>
    public int HealthPollSeconds { get; set; } = 30;

    /// <summary>How long without a valid answer an <c>active</c> node stays active (D13: 120).</summary>
    public int DownAfterSeconds { get; set; } = 120;

    /// <summary>
    /// The freshness window a snapshot must be inside to be dispatchable, in seconds (D13: 90)
    /// [RULED — README §10 T7].
    /// </summary>
    public int HealthStaleSeconds { get; set; } = 90;

    /// <summary>
    /// How many consecutive mismatches between the reported and the derived active-task count raise the
    /// drift warning (D13: three pulls).
    /// </summary>
    public int DriftAlertPulls { get; set; } = 3;

    /// <summary>The nodes this environment runs, declared by configuration (D13/F09).</summary>
    public List<RenderNodeOptions> Nodes { get; set; } = [];
}

/// <summary>
/// One entry of <c>RenderNodes:Nodes</c> (11 README D13) — the values <c>WorkerNodeSeeder</c> upserts by
/// <c>name</c>. Everything here is non-secret, so it belongs in appsettings and in the IaC module's
/// environment values.
/// </summary>
public sealed class RenderNodeOptions
{
    /// <summary>The node's name; its natural key, and the base of the key's variable name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The node's <c>mpt-adapter</c> base URL; parsed and validated by the seeder, not by the binder.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>How many renders the node accepts at once (1–8).</summary>
    public int Capacity { get; set; } = 1;

    /// <summary>The pinned SHA-256 of the adapter certificate, or <c>null</c> for the Development <c>http://</c> node (F26).</summary>
    public string? TlsCertSha256 { get; set; }

    /// <summary>The node's GPU, recorded for `nodes list` and capacity planning, or <c>null</c>.</summary>
    public string? GpuModel { get; set; }
}
