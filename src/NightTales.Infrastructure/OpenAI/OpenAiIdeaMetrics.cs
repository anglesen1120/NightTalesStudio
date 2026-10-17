using System.Diagnostics.Metrics;

namespace NightTales.Infrastructure.OpenAI;

/// <summary>
/// The one instrument the idea-generation step itself raises (02.8.1-T1 plan §7.1):
/// <c>idea_generation_duration_seconds</c>, the wall-clock time of one dispatched idea call.
/// </summary>
/// <remarks>
/// <para>
/// The <c>idea_candidates_total{outcome}</c> counter belongs to the planner (<c>IdeaMetrics</c>, Application),
/// which owns the validate/persist outcome; this class never sees a candidate. Both publish on the same
/// <c>NightTales.Autopilot</c> meter, the split <c>SeriesBibleMetrics</c>/<c>OpenAiBibleProposalMetrics</c> uses.
/// </para>
/// <para>
/// The instrument name is frozen for the 11.6.2 dashboards and carries no tag, no id and no content (D19).
/// Exported by 09.9.3-T1 like every other meter of the platform (<c>AddMeter("NightTales*")</c>). Registered as
/// a singleton: one instrument per process, so a scrape never sees two series for one histogram (the
/// <c>StorageMetrics</c> rule).
/// </para>
/// </remarks>
public sealed class OpenAiIdeaMetrics : IDisposable
{
    /// <summary>The meter the autopilot feature publishes on.</summary>
    public const string MeterName = "NightTales.Autopilot";

    /// <summary>The wall-clock duration of one idea-generation call.</summary>
    public const string GenerationDurationSecondsName = "idea_generation_duration_seconds";

    private readonly Meter _meter = new(MeterName);
    private readonly Histogram<double> _generationDuration;

    /// <summary>Creates the instruments.</summary>
    public OpenAiIdeaMetrics() =>
        _generationDuration = _meter.CreateHistogram<double>(
            GenerationDurationSecondsName,
            "s",
            "The wall-clock duration of one dispatched idea-generation call (plan §7.1).");

    /// <summary>Records the duration of one dispatched idea call, whatever its outcome.</summary>
    /// <param name="seconds">The elapsed seconds.</param>
    public void GenerationDuration(double seconds) => _generationDuration.Record(seconds);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
