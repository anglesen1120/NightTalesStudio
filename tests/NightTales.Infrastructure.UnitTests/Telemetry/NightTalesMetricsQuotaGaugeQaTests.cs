using System.Diagnostics.Metrics;
using NightTales.Application.Telemetry;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Telemetry;

public sealed class NightTalesMetricsQuotaGaugeQaTests
{
    [Fact]
    public void NightTalesMetrics_YoutubeQuotaUsed_ReflectsRatio()
    {
        using var metrics = new NightTalesMetrics();
        double? observed = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter == metrics.Meter && instrument.Name == "youtube_quota_used")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => observed = value);
        listener.Start();

        metrics.SetYoutubeQuotaUsed(0.5);
        listener.RecordObservableInstruments();
        observed.ShouldBe(0.5);

        metrics.SetYoutubeQuotaUsed(0.25);
        listener.RecordObservableInstruments();
        observed.ShouldBe(0.25);
    }
}
