using NightTales.Application.Telemetry;

namespace NightTales.Application.Features.Research.Fetcher;

/// <summary>
/// The <c>fetch_leases_active</c> gauge (02.5.2-T2, plan §7.4): how many tasks are <c>leased</c>. The Api refreshes it
/// from an exact <c>count(*)</c> on every lease call and every accepted result, so it is exact on the Api process that
/// served the last call; a sweeper requeue in the Worker is reflected at the next lease call.
/// </summary>
public sealed class FetchLeaseGauge
{
    /// <summary>The frozen instrument name.</summary>
    public const string Name = "fetch_leases_active";

    private int _value;

    /// <summary>Creates the instrument on the host's meter.</summary>
    /// <param name="metrics">The platform's custom instruments; the host owns the single instance.</param>
    public FetchLeaseGauge(NightTalesMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        metrics.Meter.CreateObservableGauge(
            Name,
            () => Volatile.Read(ref _value),
            unit: "{task}",
            description: "Fetch tasks that are currently leased to a fetcher.");
    }

    /// <summary>The value the gauge reports now.</summary>
    public int Value => Volatile.Read(ref _value);

    /// <summary>Replaces the gauge value.</summary>
    /// <param name="leased">The number of leased tasks, never negative.</param>
    public void Set(int leased) => Volatile.Write(ref _value, Math.Max(0, leased));
}
