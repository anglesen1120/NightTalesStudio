using System.Diagnostics.Metrics;

namespace NightTales.Fetcher;

/// <summary>
/// The fetcher's instruments (02.5.2-T3 §Observability, 02 D19). Labels are status, error code, operation and kind only:
/// never a URL, title or text. <c>fetch_leases_active</c> is deliberately absent: the Worker's sweeper owns that name
/// (set from <c>count(*)</c> in 02.5.2-T2), and a second same-named instrument from this host would be a duplicate series.
/// </summary>
public sealed class FetcherMetrics : IDisposable
{
    /// <summary>The meter name the OpenTelemetry pipeline subscribes to.</summary>
    public const string MeterName = "NightTales.Fetcher";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _tasks;
    private readonly Counter<long> _apiErrors;
    private readonly Histogram<double> _duration;

    /// <summary>Creates the instruments.</summary>
    /// <param name="robotsCacheEntries">Reads the robots cache size; <see langword="null"/> omits the gauge.</param>
    public FetcherMetrics(Func<int>? robotsCacheEntries = null)
    {
        _tasks = _meter.CreateCounter<long>("fetch_tasks_total", description: "Tasks finished by this fetcher.");
        _apiErrors = _meter.CreateCounter<long>("fetch_api_errors_total", description: "Internal API failures.");
        _duration = _meter.CreateHistogram<double>("fetch_duration_seconds", unit: "s", description: "Time from lease to result.");
        _meter.CreateObservableGauge("fetch_process_working_set_bytes", () => Environment.WorkingSet, unit: "By");
        if (robotsCacheEntries is not null)
        {
            _meter.CreateObservableGauge("fetch_robots_cache_entries", robotsCacheEntries);
        }
    }

    /// <summary>Counts one finished task; <paramref name="errorCode"/> is empty for a success.</summary>
    /// <param name="status"><c>done</c>, <c>failed</c>, <c>abandoned</c> or <c>cancelled</c>.</param>
    /// <param name="errorCode">The D8 code of a failure, or an empty string.</param>
    public void RecordTask(string status, string errorCode) =>
        _tasks.Add(1, new KeyValuePair<string, object?>("status", status), new KeyValuePair<string, object?>("error_code", errorCode));

    /// <summary>Records the time one task took.</summary>
    /// <param name="seconds">Seconds from lease to result.</param>
    public void RecordDuration(double seconds) => _duration.Record(seconds);

    /// <summary>Counts one internal API failure.</summary>
    /// <param name="operation"><c>lease</c>, <c>heartbeat</c> or <c>result</c>.</param>
    /// <param name="kind"><c>4xx</c>, <c>5xx</c> or <c>network</c>.</param>
    public void RecordApiError(string operation, string kind) =>
        _apiErrors.Add(1, new KeyValuePair<string, object?>("operation", operation), new KeyValuePair<string, object?>("kind", kind));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
