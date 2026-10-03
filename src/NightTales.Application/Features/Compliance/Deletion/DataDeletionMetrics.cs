using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NightTales.Application.Features.Compliance.Deletion;

/// <summary>
/// The deletion engine's instruments (13.3.1 plan §7.2). The names feed 11.6.2-T1's rule (<c>deletion_requests_due_soon &gt; 0</c>)
/// and are frozen; tags are scope and error code only, never an id.
/// </summary>
public sealed class DataDeletionMetrics : IDisposable
{
    /// <summary>The meter name (covered by the host's <c>NightTales*</c> subscription).</summary>
    public const string MeterName = "NightTales.Deletion";

    public const string OpenName = "deletion_requests_open";
    public const string OverdueName = "deletion_request_overdue_total";
    public const string DueSoonName = "deletion_requests_due_soon";
    public const string UnhandledName = "deletion_requests_unhandled_total";
    public const string FailedName = "deletion_request_failed_total";
    public const string RunSecondsName = "deletion_run_seconds";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _unhandled;
    private readonly Counter<long> _failed;
    private readonly Histogram<double> _runSeconds;
    private long _open;
    private long _overdue;
    private long _dueSoon;

    public DataDeletionMetrics()
    {
        _meter.CreateObservableGauge(OpenName, () => Volatile.Read(ref _open), "{request}", "Deletion requests not yet completed.");
        _meter.CreateObservableGauge(OverdueName, () => Volatile.Read(ref _overdue), "{request}", "Open deletion requests past their deadline.");
        _meter.CreateObservableGauge(DueSoonName, () => Volatile.Read(ref _dueSoon), "{request}", "Open deletion requests due within the alert lead time.");
        _unhandled = _meter.CreateCounter<long>(UnhandledName, "{request}", "Due rows left pending because their scope has no handler.");
        _failed = _meter.CreateCounter<long>(FailedName, "{attempt}", "Failed deletion attempts, by scope and error code.");
        _runSeconds = _meter.CreateHistogram<double>(RunSecondsName, "s", "Duration of one hourly deletion run.");
    }

    /// <summary>The host's meter; a test attaches a listener to this exact instance.</summary>
    public Meter Meter => _meter;

    /// <summary>Publishes the three gauge values of one run's metric phase.</summary>
    public void SetCounts(long open, long overdue, long dueSoon)
    {
        Volatile.Write(ref _open, open);
        Volatile.Write(ref _overdue, overdue);
        Volatile.Write(ref _dueSoon, dueSoon);
    }

    public void Unhandled(string scope, long count) => _unhandled.Add(count, new TagList { { "scope", scope } });

    public void Failed(string scope, string errorCode) =>
        _failed.Add(1, new TagList { { "scope", scope }, { "error_code", errorCode } });

    public void RunFinished(double seconds) => _runSeconds.Record(seconds);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
