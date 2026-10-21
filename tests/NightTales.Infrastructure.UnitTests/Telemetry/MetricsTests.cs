using System.Diagnostics.Metrics;
using NightTales.Application.Telemetry;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Telemetry;

public sealed class MetricsTests
{
    private static readonly (string Name, string Unit)[] OwnedByNightTalesMetrics =
    [
        ("render_jobs_active", "{job}"),
        ("render_duration_seconds", "s"),
        ("openai_cost_usd_total", "USD"),
        ("youtube_quota_used", "1"),
    ];

    [Fact]
    public void Frozen_metric_names_are_exported_with_units()
    {
        using var metrics = new NightTalesMetrics();
        var seen = new Dictionary<string, string?>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter == metrics.Meter)
                {
                    seen[instrument.Name] = instrument.Unit;
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.Start();

        metrics.Meter.Name.ShouldBe(NightTalesMetrics.MeterName);
        foreach (var (name, unit) in OwnedByNightTalesMetrics)
        {
            seen.ShouldContainKey(name);
            seen[name].ShouldBe(unit);
        }
    }

    [Fact]
    public void Render_jobs_active_reflects_seeded_jobs()
    {
        using var metrics = new NightTalesMetrics();
        long observed = -1;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter == metrics.Meter && instrument.Name == "render_jobs_active")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => observed = value);
        listener.Start();

        metrics.ResetRenderJobsActive(7);
        listener.RecordObservableInstruments();
        observed.ShouldBe(7);

        metrics.ResetRenderJobsActive(0);
        listener.RecordObservableInstruments();
        observed.ShouldBe(0);
    }

    [Fact]
    public void No_metric_carries_an_id_tag()
    {
        using var metrics = new NightTalesMetrics();
        var tagKeys = new HashSet<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter == metrics.Meter)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                tagKeys.Add(tag.Key);
            }
        });
        listener.Start();

        metrics.RecordRenderDuration("tts", "ok", 1.5);
        metrics.AddOpenAiCost("story", 0.01);

        tagKeys.ShouldBe(["stage", "outcome", "operation"], ignoreOrder: true);
        tagKeys.ShouldAllBe(key => !key.Contains("id", StringComparison.OrdinalIgnoreCase));
    }
}
