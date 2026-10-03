using NightTales.MockMpt.Failures;
using NightTales.MockMpt.Images;

namespace NightTales.MockMpt;

/// <summary>
/// The mock's own configuration, read once at start (11 README D14). The <c>MOCK_*</c> names exist only in
/// <c>tools/mock-mpt</c>, the dev <c>.env</c> and the optional compose file — the real adapter has no such
/// keys (11.1.2-T2 Failure handling).
/// </summary>
/// <remarks>
/// <see cref="ApiKey"/> and <see cref="MaxConcurrentTasks"/> are the two values the core owns
/// (<c>RenderNodeServer:ApiKey</c> / <c>RenderNodeServer:MaxConcurrentTasks</c>, 11 README D5); every other
/// member is the mock's own behaviour.
/// </remarks>
public sealed record MockMptOptions
{
    /// <summary>The dev-only key <c>.env.example</c> and <c>tools/dev/up.sh</c> carry; never a real key.</summary>
    public const string DefaultApiKey = "dev-mock-api-key-local-0123456789";

    /// <summary>D14 default render length; §10 T32 makes ≥ 1 s the floor.</summary>
    public const int DefaultRenderSeconds = 20;

    /// <summary>D14 default reported duration. The served file stays the 10 s canned one (README).</summary>
    public const int DefaultVideoSeconds = 10;

    /// <summary>D14 default capacity: the fake node accepts two concurrent tasks.</summary>
    public const int DefaultMaxConcurrentTasks = 2;

    /// <summary>
    /// The contract's cleaned-tombstone window (11.3.5-T2: <c>PurgeCleanedAsync(now − 7 d)</c>): a DELETE marks
    /// <c>cleaned_at</c> and keeps the row this long so idempotent re-reads still work (11.1.2-T1 review fix
    /// MOCK-PERF-01).
    /// </summary>
    public static readonly TimeSpan DefaultCleanedRetention = TimeSpan.FromDays(7);

    /// <summary>How often the mock's retention sweep calls the core's purge API.</summary>
    public static readonly TimeSpan DefaultRetentionSweepInterval = TimeSpan.FromMinutes(10);

    /// <summary>The <c>MOCK_API_KEY</c> the core compares <c>x-api-key</c> against (≥ 32 bytes, 05 D15 S3).</summary>
    public required string ApiKey { get; init; }

    /// <summary>Wall-clock seconds a task takes to walk <c>queued → running → checking → succeeded</c>.</summary>
    public int RenderSeconds { get; init; } = DefaultRenderSeconds;

    /// <summary>The <c>narration_seconds</c> the status reports; it never changes the canned file.</summary>
    public int VideoSeconds { get; init; } = DefaultVideoSeconds;

    /// <summary>The node's <c>capacity.max_concurrent_tasks</c>.</summary>
    public int MaxConcurrentTasks { get; init; } = DefaultMaxConcurrentTasks;

    /// <summary>The D14 fixtures directory (working tree natively, <c>/fixtures</c> in the image).</summary>
    public required string FixturesDirectory { get; init; }

    /// <summary>Where the canned media lives, and where a task's SRT is written.</summary>
    public required string MediaDirectory { get; init; }

    /// <summary>
    /// <c>MOCK_FIXTURE</c>: a D14 fixture id every task (or the health body) replays; null means the mock's
    /// normal behaviour.
    /// </summary>
    public string? Fixture { get; init; }

    /// <summary>
    /// <c>MOCK_FAIL_MODE</c> (11.1.2-T2): the failure every submit applies unless a keyword overrides it;
    /// <see cref="FailMode.None"/> is the normal path.
    /// </summary>
    public FailMode FailMode { get; init; } = FailMode.None;

    /// <summary>
    /// <c>MOCK_FAIL_RATE</c> in <c>[0,1]</c> (11.1.2-T2): the share of submits that fail, decided by a
    /// deterministic draw over the <c>render_job_id</c>; inert while <see cref="FailMode"/> is set.
    /// </summary>
    public double FailRate { get; init; }

    /// <summary>
    /// <c>MOCK_IMAGE_REPORT</c> (11.1.2-T2 / 03.4.3-T2): the shape of the <c>images</c> block every submit
    /// reports unless a keyword overrides it.
    /// </summary>
    public ImageReportMode ImageReport { get; init; } = ImageReportMode.Ok;

    /// <summary>How long a cleaned tombstone is kept before the retention sweep purges it.</summary>
    public TimeSpan CleanedRetention { get; init; } = DefaultCleanedRetention;

    /// <summary>How often the retention sweep runs; tests shorten it to exercise the purge quickly.</summary>
    public TimeSpan RetentionSweepInterval { get; init; } = DefaultRetentionSweepInterval;
}
