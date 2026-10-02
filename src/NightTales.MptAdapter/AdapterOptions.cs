namespace NightTales.MptAdapter;

/// <summary>
/// The adapter's own configuration (11 README D6), bound from the <c>Adapter</c> section (<c>Adapter__*</c>
/// environment names in <c>mpt-adapter.service</c>). The server core's keys stay in
/// <c>RenderNodeServerOptions</c>; the relay's in <c>ImageRelay:*</c> (03.4.3-T1).
/// </summary>
/// <remarks>
/// <see cref="MptThreads"/> is <c>int?</c> on purpose: unset means "derive it" (05 D15 P6), and only a
/// positive value is treated as configured, so a bound <c>0</c> cannot ask MPT for zero threads.
/// </remarks>
public sealed class AdapterOptions
{
    /// <summary>The configuration section every key binds to (D6).</summary>
    public const string SectionName = "Adapter";

    /// <summary><c>Adapter:MptBaseUrl</c>; loopback only, because MPT never leaves the node (D8/D11).</summary>
    public string MptBaseUrl { get; set; } = "http://127.0.0.1:8080";

    /// <summary><c>Adapter:MptRoot</c>; <c>storage/tasks</c> under it is 05 D8's task-dir root.</summary>
    public string MptRoot { get; set; } = "/opt/nts/current/mpt";

    /// <summary>05 D15 P6's <c>n_threads</c>; null derives it from the cores and the concurrency cap.</summary>
    public int? MptThreads { get; set; }

    /// <summary><c>Adapter:PollSeconds</c>; the poller's per-task cadence (D6 pins 2).</summary>
    public int PollSeconds { get; set; } = 2;

    /// <summary><c>Adapter:DataDir</c>; <c>tasks.db</c> and the adapter's own task dirs live here (D6).</summary>
    public string DataDir { get; set; } = "/var/lib/nts/adapter";

    /// <summary>
    /// 05 D8's fixed relay state dir. The relay (03.4.3-T1) owns the files; the adapter deletes the two
    /// per-task ones at cleanup, so the path is configuration here too.
    /// </summary>
    public string RelayDir { get; set; } = "/var/lib/nts/relay";

    /// <summary>The MPT HTTP connect timeout (D6: 2 s), applied to the handler the host builds.</summary>
    public int MptConnectTimeoutSeconds { get; set; } = 2;

    /// <summary>The MPT HTTP read timeout (D6: 30 s), enforced per request by <c>MptClient</c>.</summary>
    public int MptReadTimeoutSeconds { get; set; } = 30;

    /// <summary>The systemd unit whose start time is the lost rule's second half (D6).</summary>
    public string MptServiceName { get; set; } = MptProcessStartReader.DefaultServiceName;

    /// <summary>D15 P4's window: a cleaned row is purged this many days after <c>cleaned_at</c>.</summary>
    public int PurgeAfterDays { get; set; } = 7;

    /// <summary>The store's database file (plan §3.1: <c>DataDir/tasks.db</c>).</summary>
    public string TasksDbPath => Path.Combine(DataDir, "tasks.db");

    /// <summary>MPT's task-dir root (plan §3.3: <c>MptRoot/storage/tasks</c>).</summary>
    public string MptTasksDir => Path.Combine(MptRoot, "storage", "tasks");

    /// <summary>The adapter's per-task dirs, where T3 copies the check report (plan §3.3).</summary>
    public string AdapterTasksDir => Path.Combine(DataDir, "tasks");

    /// <summary>
    /// 05 D15 P6: the configured value, or one thread per core divided by the node's task concurrency —
    /// never lower than one.
    /// </summary>
    public int ResolveMptThreads(int maxConcurrentTasks) =>
        MptThreads is > 0
            ? MptThreads.Value
            : Math.Max(1, Environment.ProcessorCount / Math.Max(1, maxConcurrentTasks));
}
