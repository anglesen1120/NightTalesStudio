namespace NightTales.Application.Rendering;

/// <summary>
/// The <c>Render</c> configuration section of 09.6.3-T1 (BD7's cadence and bounds): the poll cadence the
/// poller ticks at and the two job-level bounds, plus the batch and parallelism caps of one tick.
/// </summary>
/// <remarks>
/// <para>
/// The section is bound and validated in the <b>Worker only</b> (<c>ValidateOnStart</c>), exactly as the
/// <c>Images</c> section is: the Application layer holds the values and the checks
/// (<see cref="RenderOptionsValidator"/>), the composition root owns the binding, because Application may not
/// reference the Options package. The bound value is registered as itself, so a job or a handler takes
/// <see cref="RenderOptions"/> directly instead of <c>IOptions&lt;RenderOptions&gt;</c>.
/// </para>
/// <para>
/// The keys <c>Render:Download:*</c> of 05/09.6.2-T1 are untouched: <c>Render:MaxVideoBytes</c> and
/// <c>Render:MaxSubtitleBytes</c> are declared here as well as in the Infrastructure <c>RenderClientOptions</c>
/// of 09.6.2-T1, because the ingest job that enforces them reads them from this side, and a key it does not
/// declare is ignored by the binder either way.
/// </para>
/// </remarks>
public sealed class RenderOptions
{
    /// <summary>The configuration section this type binds (BD7).</summary>
    public const string SectionName = "Render";

    /// <summary>
    /// How long one poll tick waits before the next one, in seconds (BD7, WF07/W12: 10 — never Hangfire's
    /// 15 s minimum). Also the next-poll distance of a job that only reported progress, and the fallback
    /// reschedule of a deferred adapter answer.
    /// </summary>
    public int PollSeconds { get; set; } = 10;

    /// <summary>
    /// How long a submitted render may stay non-terminal before the poller fails it with
    /// <c>render_timeout</c>, in minutes (BD7: 60; 05 D15 P7 — a timeout is never a re-submit).
    /// </summary>
    public int MaxMinutes { get; set; } = 60;

    /// <summary>
    /// How long a job may stay <c>queued</c> with no dispatchable node before the Worker raises the queue
    /// alarm, in minutes (BD7: 360; 11 D20 H14). The alarm never spends a retry attempt.
    /// </summary>
    public int QueueAlertMinutes { get; set; } = 360;

    /// <summary>How many due rows one poll tick claims at most (BD7: 50).</summary>
    public int PollBatchSize { get; set; } = 50;

    /// <summary>How many claimed rows one poll tick processes at once (BD7: 4).</summary>
    public int PollParallelism { get; set; } = 4;

    /// <summary>
    /// The byte cap of a downloaded video (<c>Render:MaxVideoBytes</c>, BD6/BD7: 2 GiB), read by the ingest of
    /// 09.6.3-T2 as the bound it hands <c>IRenderDownloadClient.DownloadAsync</c>.
    /// </summary>
    /// <remarks>
    /// The key is the one 09.6.2-T1 already binds into its own <c>RenderClientOptions</c> (the download client
    /// takes the stricter of the two, <c>RenderDownloadClient.CapFor</c>), and the default is that class's own:
    /// the value has one home per layer, never a second configuration key.
    /// </remarks>
    public long MaxVideoBytes { get; set; } = 2_147_483_648L;

    /// <summary>The byte cap of a downloaded subtitle track (<c>Render:MaxSubtitleBytes</c>, BD6/BD7: 2 MiB).</summary>
    public long MaxSubtitleBytes { get; set; } = 2_097_152L;
}

/// <summary>
/// Checks <see cref="RenderOptions"/> so an impossible <c>Render</c> section stops the Worker's start-up
/// instead of its first tick (the <c>ImagesOptionsValidator</c> precedent).
/// </summary>
/// <remarks>
/// Every value is an interval, a count or a bound whose zero or negative reading would silently disable a
/// guard of the poller (a zero batch claims nothing, a zero poll interval spins), so the section is refused
/// rather than clamped. The composition root adapts this list to <c>IValidateOptions&lt;RenderOptions&gt;</c>,
/// the same way it adapts <c>ImagesOptionsValidator</c>.
/// </remarks>
public static class RenderOptionsValidator
{
    /// <summary>The longest accepted <see cref="RenderOptions.PollSeconds"/>: one hour.</summary>
    public const int MaxPollSeconds = 3_600;

    /// <summary>The longest accepted <see cref="RenderOptions.MaxMinutes"/>: one day.</summary>
    public const int MaxRenderMinutes = 1_440;

    /// <summary>The longest accepted <see cref="RenderOptions.QueueAlertMinutes"/>: one week.</summary>
    public const int MaxQueueAlertMinutes = 10_080;

    /// <summary>The largest accepted <see cref="RenderOptions.PollBatchSize"/>.</summary>
    public const int MaxPollBatchSize = 1_000;

    /// <summary>The largest accepted <see cref="RenderOptions.PollParallelism"/>.</summary>
    public const int MaxPollParallelism = 64;

    /// <summary>The largest accepted download cap: 1 TiB, far above any render this platform stores.</summary>
    public const long MaxDownloadBytes = 1_099_511_627_776L;

    /// <summary>
    /// Validates the bound section: every value strictly positive and inside its documented ceiling.
    /// </summary>
    /// <param name="options">The bound section.</param>
    /// <returns>One message per problem; empty when valid.</returns>
    public static IReadOnlyList<string> Validate(RenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();

        if (options.PollSeconds is < 1 or > MaxPollSeconds)
        {
            problems.Add($"{nameof(options.PollSeconds)} must be between 1 and {MaxPollSeconds}.");
        }

        if (options.MaxMinutes is < 1 or > MaxRenderMinutes)
        {
            problems.Add($"{nameof(options.MaxMinutes)} must be between 1 and {MaxRenderMinutes}.");
        }

        if (options.QueueAlertMinutes is < 1 or > MaxQueueAlertMinutes)
        {
            problems.Add($"{nameof(options.QueueAlertMinutes)} must be between 1 and {MaxQueueAlertMinutes}.");
        }

        if (options.PollBatchSize is < 1 or > MaxPollBatchSize)
        {
            problems.Add($"{nameof(options.PollBatchSize)} must be between 1 and {MaxPollBatchSize}.");
        }

        if (options.PollParallelism is < 1 or > MaxPollParallelism)
        {
            problems.Add($"{nameof(options.PollParallelism)} must be between 1 and {MaxPollParallelism}.");
        }

        // A cap of zero or less would refuse every download and a subtitle cap above the video cap would let a
        // subtitle track outgrow the file it belongs to: both are configuration mistakes, not clampable bounds.
        if (options.MaxVideoBytes is < 1 or > MaxDownloadBytes)
        {
            problems.Add($"{nameof(options.MaxVideoBytes)} must be between 1 and {MaxDownloadBytes}.");
        }

        if (options.MaxSubtitleBytes is < 1 or > MaxDownloadBytes)
        {
            problems.Add($"{nameof(options.MaxSubtitleBytes)} must be between 1 and {MaxDownloadBytes}.");
        }
        else if (options.MaxSubtitleBytes > options.MaxVideoBytes)
        {
            problems.Add($"{nameof(options.MaxSubtitleBytes)} must not exceed {nameof(options.MaxVideoBytes)}.");
        }

        return problems;
    }
}
