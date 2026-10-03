using System.Diagnostics.Metrics;

namespace NightTales.Application.Features.Assets.Jobs;

/// <summary>
/// The instruments of <see cref="RetentionSweepJob"/> (09.7.3-T1, exported by 09.9.3-T1):
/// <c>retention_sweep_assets_deleted_total</c>, <c>retention_sweep_asset_failures_total</c>,
/// <c>retention_sweep_audit_rows_deleted_total</c>, <c>retention_sweep_batches_total</c>,
/// <c>retention_sweep_batches_capped_total{phase}</c> and <c>retention_sweep_duration_seconds</c>.
/// </summary>
/// <remarks>
/// One instance per process (a singleton), like <c>EmailMetrics</c>. No tag carries an id, a key or a user.
/// </remarks>
public sealed class RetentionSweepMetrics : IDisposable
{
    /// <summary>The meter every instrument of the sweep publishes on.</summary>
    public const string MeterName = "NightTales.RetentionSweep";

    /// <summary>Objects deleted and rows marked.</summary>
    public const string AssetsDeletedCounterName = "retention_sweep_assets_deleted_total";

    /// <summary>Assets whose object delete or mark failed.</summary>
    public const string AssetFailuresCounterName = "retention_sweep_asset_failures_total";

    /// <summary>Audit rows deleted.</summary>
    public const string AuditRowsDeletedCounterName = "retention_sweep_audit_rows_deleted_total";

    /// <summary>Batches run, across both phases.</summary>
    public const string BatchesCounterName = "retention_sweep_batches_total";

    /// <summary>Phases that stopped at <c>MaxBatches</c> with backlog left.</summary>
    public const string BatchesCappedCounterName = "retention_sweep_batches_capped_total";

    /// <summary>One run's duration in seconds.</summary>
    public const string DurationHistogramName = "retention_sweep_duration_seconds";

    /// <summary>The tag naming the phase (<c>assets</c> or <c>audit</c>).</summary>
    public const string PhaseTagName = "phase";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _assetsDeleted;
    private readonly Counter<long> _assetFailures;
    private readonly Counter<long> _auditRowsDeleted;
    private readonly Counter<long> _batches;
    private readonly Counter<long> _batchesCapped;
    private readonly Histogram<double> _duration;

    /// <summary>Creates the meter and its instruments.</summary>
    public RetentionSweepMetrics()
    {
        _assetsDeleted = _meter.CreateCounter<long>(AssetsDeletedCounterName, "1", "Asset objects deleted and rows marked deleted.");
        _assetFailures = _meter.CreateCounter<long>(AssetFailuresCounterName, "1", "Assets the sweep could not delete or mark.");
        _auditRowsDeleted = _meter.CreateCounter<long>(AuditRowsDeletedCounterName, "1", "Audit rows deleted by the retention rules.");
        _batches = _meter.CreateCounter<long>(BatchesCounterName, "1", "Batches the sweep ran.");
        _batchesCapped = _meter.CreateCounter<long>(BatchesCappedCounterName, "1", "Phases that stopped at MaxBatches with backlog left.");
        _duration = _meter.CreateHistogram<double>(DurationHistogramName, "s", "Duration of one sweep run.");
    }

    /// <summary>Counts one asset deleted.</summary>
    public void AssetDeleted() => _assetsDeleted.Add(1);

    /// <summary>Counts one asset that failed.</summary>
    public void AssetFailed() => _assetFailures.Add(1);

    /// <summary>Counts audit rows deleted.</summary>
    /// <param name="count">The rows one batch removed.</param>
    public void AuditRowsDeleted(int count) => _auditRowsDeleted.Add(count);

    /// <summary>Counts one batch of <paramref name="phase"/>.</summary>
    /// <param name="phase">The phase name.</param>
    public void Batch(string phase) => _batches.Add(1, new KeyValuePair<string, object?>(PhaseTagName, phase));

    /// <summary>Counts a phase that reached <c>MaxBatches</c>.</summary>
    /// <param name="phase">The phase name.</param>
    public void Capped(string phase) => _batchesCapped.Add(1, new KeyValuePair<string, object?>(PhaseTagName, phase));

    /// <summary>Records the duration of one run.</summary>
    /// <param name="duration">The run's wall time.</param>
    public void Duration(TimeSpan duration) => _duration.Record(duration.TotalSeconds);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
