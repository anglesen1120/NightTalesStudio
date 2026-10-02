using System.Net;

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

    /// <summary>
    /// The loopback-only rule for <see cref="MptBaseUrl"/>, enforced where the MPT key is sent.
    /// Accepts only an absolute <c>http</c> URL whose host is a loopback IP literal (<c>127.0.0.0/8</c>,
    /// <c>[::1]</c>, or IPv4-mapped loopback). Hostnames, <c>localhost</c> included, are refused because DNS
    /// could resolve them off the node (11 README H4), and <c>https</c> is refused because MPT is plain http on
    /// loopback (D6). A malformed value keeps <see cref="Uri"/>'s own <see cref="UriFormatException"/>.
    /// The configured base path is kept, so it must be the MPT root (<c>/</c>).
    /// </summary>
    /// <exception cref="ArgumentException">The URL is not a loopback IP-literal http URL; the message names the
    /// key but not the value.</exception>
    public static Uri ParseLoopbackMptBaseUrl(string value)
    {
        ArgumentNullException.ThrowIfNull(value, "Adapter:MptBaseUrl");

        var uri = new Uri(value, UriKind.Absolute);
        var host = uri.Host.Trim('[', ']');
        if (uri.Scheme != Uri.UriSchemeHttp
            || !IPAddress.TryParse(host, out var address)
            || !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException(
                "Adapter:MptBaseUrl must be an absolute http URL whose host is a loopback IP address (127.0.0.0/8 or [::1]); MPT never leaves the node.");
        }

        // QA89-LB-01: MptClient resolves "api/v1/..." against this URI, so a path such as /foo would turn every
        // MPT call into a 404. Only the root is a usable base; query and fragment are never meaningful either,
        // and user-info is refused outright: a credential in a URL can reach logs and config dumps, and the
        // node's key is only ever NTS_MPT_API_KEY.
        if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
        {
            throw new ArgumentException(
                "Adapter:MptBaseUrl must be the MPT root: no credentials, path, query or fragment.");
        }

        return uri;
    }

    /// <summary>
    /// The same loopback-IP-literal http rule as <see cref="ParseLoopbackMptBaseUrl"/>, without the root-path
    /// rule, for URLs the node only ever calls on itself (<c>Adapter:ProxyHealthUrl</c>, QA-3). A URL with
    /// user-info is refused.
    /// </summary>
    public static bool IsLoopbackHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttp
        && uri.UserInfo.Length == 0
        && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address)
        && IPAddress.IsLoopback(address);

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

    /// <summary><c>Adapter:ListenAddress</c>: the node's private address (D6, F26). Required, no default.</summary>
    public string ListenAddress { get; set; } = string.Empty;

    /// <summary><c>Adapter:Port</c>: the HTTPS port (D6, final 8090).</summary>
    public int Port { get; set; } = 8090;

    /// <summary>The node certificate (D11).</summary>
    public string CertPath { get; set; } = "/etc/nts/tls/adapter.crt";

    /// <summary>The node certificate's private key (D11).</summary>
    public string KeyPath { get; set; } = "/etc/nts/tls/adapter.key";

    /// <summary><c>Adapter:CheckConcurrency</c> (D6: 1).</summary>
    public int CheckConcurrency { get; set; } = 1;

    /// <summary><c>Adapter:CheckTimeoutSeconds</c> (D6: 900).</summary>
    public int CheckTimeoutSeconds { get; set; } = 900;

    /// <summary><c>Adapter:AssetsRefreshSeconds</c> (D6: 600).</summary>
    public int AssetsRefreshSeconds { get; set; } = 600;

    /// <summary><c>Adapter:GpuSampleSeconds</c> (D6: 15); the periodic MPT/proxy/disk/GPU interval.</summary>
    public int GpuSampleSeconds { get; set; } = 15;

    /// <summary><c>Adapter:FfmpegPath</c> (D6).</summary>
    public string FfmpegPath { get; set; } = "/usr/bin/ffmpeg";

    /// <summary><c>Adapter:VideoCodec</c> (D6): the <c>NTS_MPT_VIDEO_CODEC</c> value.</summary>
    public string VideoCodec { get; set; } = Environment.GetEnvironmentVariable("NTS_MPT_VIDEO_CODEC") ?? "libx264";

    /// <summary><c>Adapter:ProxyHealthUrl</c> (D6).</summary>
    public string ProxyHealthUrl { get; set; } = "http://127.0.0.1:8001/health";

    /// <summary>The <c>nvidia-smi</c> binary: a rooted path, never resolved through PATH (SEC-5).</summary>
    public string NvidiaSmiPath { get; set; } = "/usr/bin/nvidia-smi";

    /// <summary>The rendered MPT config (D8 state dir).</summary>
    public string MptConfigPath { get; set; } = "/var/lib/nts/mpt/config.toml";

    /// <summary>The filesystem the disk source measures: the task dirs' (D20 H16).</summary>
    public string DiskPath => MptTasksDir;

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
