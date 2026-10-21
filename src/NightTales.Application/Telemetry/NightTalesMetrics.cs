using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NightTales.Application.Telemetry;

/// <summary>
/// The platform's custom instruments (09 README BD12, 09.9.3-T1): the single surface 02, 03, 06 and 09.6.3
/// record into. It depends on <c>System.Diagnostics.Metrics</c> only — never on the OpenTelemetry SDK —
/// so a handler stays free of the exporter, and the host decides what listens to <see cref="MeterName"/>.
/// </summary>
/// <remarks>
/// <para>
/// The metric names, units and tag keys are frozen: 11.6.2-T1's dashboards and alerts read them. Tag sets
/// are closed enums or strings (<c>stage</c>, <c>outcome</c>, <c>operation</c>), never an id, so the series
/// count stays bounded.
/// </para>
/// <para>
/// <c>outbox_lag_seconds</c> and <c>outbox_dead_letters_total</c> are the other two frozen names; they are
/// published by <c>OutboxMetrics</c> on <c>NightTales.Outbox</c> (09.2.4), which already owns them with the
/// same units — a second instrument of the same name here would export every point twice.
/// </para>
/// </remarks>
public sealed class NightTalesMetrics : IDisposable
{
    /// <summary>The meter every custom instrument of this class lives on.</summary>
    public const string MeterName = "NightTales";

    /// <summary>The gauge of render jobs in <c>queued|submitted|running|ingesting</c>.</summary>
    public const string RenderJobsActiveName = "render_jobs_active";

    /// <summary>The histogram of one render stage's duration.</summary>
    public const string RenderDurationName = "render_duration_seconds";

    /// <summary>The counter of OpenAI spend.</summary>
    public const string OpenAiCostName = "openai_cost_usd_total";

    /// <summary>The gauge of the YouTube daily quota used, as a ratio.</summary>
    public const string YoutubeQuotaUsedName = "youtube_quota_used";

    /// <summary>The tag key of a render stage.</summary>
    public const string StageTag = "stage";

    /// <summary>The tag key of a stage outcome.</summary>
    public const string OutcomeTag = "outcome";

    /// <summary>The tag key of an OpenAI operation (<c>story</c>, <c>image</c>).</summary>
    public const string OperationTag = "operation";

    /// <summary>The counter of fetch-task transitions (02.5.2-T2, D19).</summary>
    public const string FetchTasksTotalName = "fetch_tasks_total";

    /// <summary>The histogram of a fetch's lease-to-result duration (02.5.2-T2, D19).</summary>
    public const string FetchDurationSecondsName = "fetch_duration_seconds";

    /// <summary>The tag key of a fetch task status.</summary>
    public const string FetchStatusTag = "status";

    /// <summary>The tag key of a fetch task error code.</summary>
    public const string FetchErrorCodeTag = "error_code";

    /// <summary>
    /// The counter of channel revokes Google refused or never answered (06.4.1-T1 §Design step 4, plan §Revocation):
    /// an exhausted or non-retryable revoke. A disconnected channel whose grant outlived the wipe is the alert this
    /// instrument raises.
    /// </summary>
    public const string YoutubeChannelRevokeFailedTotalName = "youtube_channel_revoke_failed_total";

    private readonly Meter _meter = new(MeterName);
    private readonly Histogram<double> _renderDuration;
    private readonly Counter<double> _openAiCost;
    private long _renderJobsActive;
    private double _youtubeQuotaUsed;
    private readonly Counter<long> _fetchTasks;
    private readonly Histogram<double> _fetchDuration;
    private readonly Counter<long> _youtubeChannelRevokeFailed;

    /// <summary>Creates the instruments once; the host owns the single instance.</summary>
    public NightTalesMetrics()
    {
        _meter.CreateObservableGauge(
            RenderJobsActiveName,
            () => Volatile.Read(ref _renderJobsActive),
            unit: "{job}",
            description: "Render jobs that are queued, submitted, running or ingesting.");

        _renderDuration = _meter.CreateHistogram<double>(
            RenderDurationName,
            unit: "s",
            description: "Duration of one render stage, by stage and outcome.");

        _openAiCost = _meter.CreateCounter<double>(
            OpenAiCostName,
            unit: "USD",
            description: "OpenAI spend, by operation.");

        _meter.CreateObservableGauge(
            YoutubeQuotaUsedName,
            () => Volatile.Read(ref _youtubeQuotaUsed),
            unit: "1",
            description: "Share of the YouTube daily quota used (0..1).");

        _fetchTasks = _meter.CreateCounter<long>(
            FetchTasksTotalName,
            unit: "{task}",
            description: "Fetch tasks that finished, failed or were requeued, by status and error code.");

        _fetchDuration = _meter.CreateHistogram<double>(
            FetchDurationSecondsName,
            unit: "s",
            description: "Lease-to-result duration of one fetch task.");

        _youtubeChannelRevokeFailed = _meter.CreateCounter<long>(
            YoutubeChannelRevokeFailedTotalName,
            unit: "{revoke}",
            description: "Channel disconnects whose Google revoke was refused or never answered (no tags: ids never reach a metric).");
    }

    /// <summary>The host's meter; a test attaches a listener to this exact instance.</summary>
    public Meter Meter => _meter;

    /// <summary>Replaces the active-render-jobs gauge value (the Worker refresh loop is the only caller).</summary>
    /// <param name="count">The platform-wide count of active render jobs.</param>
    public void ResetRenderJobsActive(long count) => Volatile.Write(ref _renderJobsActive, count);

    /// <summary>Records the duration of one render stage.</summary>
    /// <param name="stage">The closed stage name.</param>
    /// <param name="outcome">The closed outcome name.</param>
    /// <param name="seconds">The measured duration, in seconds.</param>
    public void RecordRenderDuration(string stage, string outcome, double seconds) =>
        _renderDuration.Record(seconds, new TagList { { StageTag, stage }, { OutcomeTag, outcome } });

    /// <summary>Adds OpenAI spend.</summary>
    /// <param name="operation">The closed operation name.</param>
    /// <param name="usd">The cost in US dollars; non-positive values are ignored.</param>
    public void AddOpenAiCost(string operation, double usd)
    {
        if (usd > 0d)
        {
            _openAiCost.Add(usd, new TagList { { OperationTag, operation } });
        }
    }

    /// <summary>Sets the YouTube quota gauge.</summary>
    /// <param name="ratio">The share used, clamped into 0..1.</param>
    public void SetYoutubeQuotaUsed(double ratio) =>
        Volatile.Write(ref _youtubeQuotaUsed, Math.Clamp(ratio, 0d, 1d));

    /// <summary>Counts one fetch task that left the queue or the lease (02.5.2-T2, D19).</summary>
    /// <param name="status">The closed status: <c>done</c>, <c>failed</c> or <c>requeued</c>.</param>
    /// <param name="errorCode">The closed D8 code of a failure; <see langword="null"/> otherwise.</param>
    public void RecordFetchTask(string status, string? errorCode)
    {
        var tags = new TagList { { FetchStatusTag, status } };
        if (errorCode is not null)
        {
            tags.Add(FetchErrorCodeTag, errorCode);
        }

        _fetchTasks.Add(1, tags);
    }

    /// <summary>Records one fetch's duration, measured from the lease (<c>leased_at</c>), never from <c>created_at</c>.</summary>
    /// <param name="seconds">The lease-to-result time, in seconds.</param>
    public void RecordFetchDuration(double seconds) => _fetchDuration.Record(seconds);

    /// <summary>
    /// Counts one disconnect whose Google revoke failed (06.4.1-T1 §Design step 4): a transport failure that outlived
    /// the retry ladder, or a refusal that retrying cannot fix. The disconnect itself still succeeds.
    /// </summary>
    public void RecordYoutubeChannelRevokeFailed() => _youtubeChannelRevokeFailed.Add(1);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
