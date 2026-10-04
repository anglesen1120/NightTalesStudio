using System.Diagnostics.Metrics;

namespace NightTales.Application.Features.Stories.Generation;

/// <summary>
/// The instruments of the story generation job and sweeper (02.2.2-T3 step 7). Tags carry bounded values only (tier,
/// stage, kind, status, trigger), never an id or any text (D19).
/// </summary>
public sealed class StoryGenerationMetrics : IDisposable
{
    /// <summary>The meter name.</summary>
    public const string MeterName = "NightTales.StoryGeneration";

    /// <summary>Seconds spent in one stage, by tier and stage.</summary>
    public const string DurationHistogramName = "story_generation_duration_seconds";

    /// <summary>Writer or review outputs that were invalid.</summary>
    public const string InvalidOutputCounterName = "story_invalid_output_total";

    /// <summary>Runs that left a running or queued state, by kind, status and trigger.</summary>
    public const string RunsCounterName = "story_generation_runs_total";

    /// <summary>Runs that passed 80 % of their deadline.</summary>
    public const string SoftTimeoutCounterName = "story_generation_soft_timeout_total";

    private readonly Meter _meter = new(MeterName);
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _invalidOutput;
    private readonly Counter<long> _runs;
    private readonly Counter<long> _softTimeout;

    /// <summary>Creates the instruments.</summary>
    public StoryGenerationMetrics()
    {
        _duration = _meter.CreateHistogram<double>(DurationHistogramName, "s", "Time spent in one generation stage.");
        _invalidOutput = _meter.CreateCounter<long>(InvalidOutputCounterName, "1", "Invalid writer or review outputs.");
        _runs = _meter.CreateCounter<long>(RunsCounterName, "1", "Generation runs that reached a status.");
        _softTimeout = _meter.CreateCounter<long>(SoftTimeoutCounterName, "1", "Runs that passed 80 % of their deadline.");
    }

    /// <summary>Records the time one stage took.</summary>
    /// <param name="tier">The wire name of the length tier.</param>
    /// <param name="stage">The wire name of the stage.</param>
    /// <param name="seconds">The elapsed seconds.</param>
    public void StageDuration(string tier, string stage, double seconds) =>
        _duration.Record(
            seconds,
            new KeyValuePair<string, object?>("tier", tier),
            new KeyValuePair<string, object?>("stage", stage));

    /// <summary>Counts one invalid output.</summary>
    public void InvalidOutput() => _invalidOutput.Add(1);

    /// <summary>Counts one run that reached <paramref name="status"/>.</summary>
    /// <param name="kind">The wire name of the run kind.</param>
    /// <param name="status">The wire name of the status.</param>
    /// <param name="trigger">The wire name of the trigger.</param>
    public void RunFinished(string kind, string status, string trigger) =>
        _runs.Add(
            1,
            new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("status", status),
            new KeyValuePair<string, object?>("trigger", trigger));

    /// <summary>Counts one run that passed 80 % of its deadline.</summary>
    public void SoftTimeout() => _softTimeout.Add(1);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
