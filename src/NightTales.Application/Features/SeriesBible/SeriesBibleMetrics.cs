using System.Diagnostics.Metrics;

namespace NightTales.Application.Features.SeriesBible;

/// <summary>
/// The series bible's instruments (02.6.3-T1, plan §4 U1 failure table): today the one counter a missing
/// row raises. The instrument names are frozen for the 11.6.2 dashboards, and no tag carries an id or any
/// content (D19).
/// </summary>
public sealed class SeriesBibleMetrics : IDisposable
{
    /// <summary>The meter name.</summary>
    public const string MeterName = "NightTales.SeriesBible";

    /// <summary>A read or a write that found no <c>series_bibles</c> row for an existing series.</summary>
    public const string MissingTotalName = "bible_missing_total";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _missing;

    /// <summary>Creates the instruments.</summary>
    public SeriesBibleMetrics()
    {
        _missing = _meter.CreateCounter<long>(
            MissingTotalName,
            "1",
            "An existing series whose series_bibles row is missing (data drift: the create hook did not land or a row was deleted).");
    }

    /// <summary>Counts one missing-row alert: a series exists but has no bible row.</summary>
    public void Missing() => _missing.Add(1);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
