using System.Diagnostics;
using System.Diagnostics.Metrics;
using NightTales.Application.Telemetry;

namespace NightTales.Application.Rendering;

/// <summary>
/// The render-pipeline instruments of 09.6.3-T1 (09 README BD12, 09.9.3 conventions): the retry counter this
/// task adds, and the render-duration histogram of the poll path.
/// </summary>
/// <remarks>
/// <para>
/// <b>One meter.</b> <c>render_duration_seconds</c> is declared by <see cref="NightTalesMetrics"/>, so this class
/// publishes it through it instead of creating a second instrument of the same name on a second meter — that
/// would export every point twice. Only <c>render_retries_total</c> is new, and it is created on that same
/// <see cref="NightTalesMetrics.Meter"/>.
/// </para>
/// <para>
/// <b>The <c>render_jobs_active</c> gauge is not here.</b> One writer owns it: the Worker's
/// <c>RenderMetricsRefresher</c> publishes the platform-wide active count on its own timer through
/// <see cref="NightTalesMetrics.ResetRenderJobsActive"/>. The poller used to publish a second, per-tick count of
/// the same gauge with a different meaning; that second writer is gone (09.6.3-T1 P7).
/// </para>
/// </remarks>
/// <param name="metrics">The host's single metrics instance.</param>
public sealed class RenderMetrics(NightTalesMetrics metrics)
{
    /// <summary>The counter of automatic render retries, by outcome code.</summary>
    public const string RetriesInstrumentName = "render_retries_total";

    /// <summary>The tag key of a retry's outcome code (a 05 D5 code, never adapter text).</summary>
    public const string CodeTag = "code";

    /// <summary>The <c>outcome</c> label of a poll that recorded progress while the task was still in flight.</summary>
    public const string OutcomeRunning = "running";

    /// <summary>The <c>outcome</c> label of a render the adapter reported <c>succeeded</c>.</summary>
    public const string OutcomeSucceeded = "succeeded";

    /// <summary>The <c>outcome</c> label of a render that ended in an outcome code of 05 D5.</summary>
    public const string OutcomeFailed = "failed";

    /// <summary>The <c>outcome</c> label of a render the Worker watchdog ended as <c>render_timeout</c>.</summary>
    public const string OutcomeTimeout = "timeout";

    /// <summary>The <c>outcome</c> label of a render the adapter reported <c>cancelled</c>.</summary>
    public const string OutcomeCancelled = "cancelled";

    private readonly Counter<long> _retries = (metrics ?? throw new ArgumentNullException(nameof(metrics)))
        .Meter.CreateCounter<long>(
            RetriesInstrumentName,
            unit: "{retry}",
            description: "Automatic render retries a failed attempt queued, by the outcome code it failed with.");

    /// <summary>Records how long an attempt had been running when it reached <paramref name="stage"/>.</summary>
    /// <param name="stage">The closed stage label (<c>queued</c>, <c>voice</c>, … or <c>unknown</c>).</param>
    /// <param name="outcome">The closed outcome label (<c>running</c>, <c>succeeded</c>, <c>failed</c>, <c>timeout</c>, <c>cancelled</c>).</param>
    /// <param name="duration">The attempt's elapsed time; a negative reading is recorded as zero.</param>
    public void ObserveDuration(string stage, string outcome, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        metrics.RecordRenderDuration(stage, outcome, Math.Max(0d, duration.TotalSeconds));
    }

    /// <summary>Counts one automatic retry that was queued for a failed attempt.</summary>
    /// <param name="code">The 05 D5 outcome code the attempt failed with.</param>
    public void CountRetry(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        _retries.Add(1, new TagList { { CodeTag, code } });
    }
}
