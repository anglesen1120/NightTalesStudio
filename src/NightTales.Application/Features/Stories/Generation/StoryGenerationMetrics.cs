using System.Diagnostics.Metrics;

namespace NightTales.Application.Features.Stories.Generation;

/// <summary>
/// The instruments of the story generation job and sweeper (02.2.2-T3 step 7). Tags carry bounded values only (tier,
/// stage, kind, status, trigger, corpus, reason), never an id or any text (D19).
/// </summary>
public sealed class StoryGenerationMetrics : IDisposable
{
    /// <summary>The meter name.</summary>
    public const string MeterName = "NightTales.StoryGeneration";

    /// <summary>Seconds spent in one stage, by tier and stage.</summary>
    public const string DurationHistogramName = "story_generation_duration_seconds";

    /// <summary>Writer, review or revision outputs that were invalid.</summary>
    public const string InvalidOutputCounterName = "story_invalid_output_total";

    /// <summary>Runs that left a running or queued state, by kind, status and trigger.</summary>
    public const string RunsCounterName = "story_generation_runs_total";

    /// <summary>Runs that passed 80 % of their deadline.</summary>
    public const string SoftTimeoutCounterName = "story_generation_soft_timeout_total";

    /// <summary>Generation similarity scores, by corpus.</summary>
    public const string SimilarityScoreHistogramName = "similarity_score";

    /// <summary>Seconds spent loading and scoring a similarity corpus.</summary>
    public const string SimilarityDurationHistogramName = "similarity_duration_seconds";

    /// <summary>Corpus failures, by bounded reason.</summary>
    public const string SimilarityUnavailableCounterName = "similarity_unavailable_total";

    private readonly Meter _meter = new(MeterName);
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _invalidOutput;
    private readonly Counter<long> _runs;
    private readonly Counter<long> _softTimeout;
    private readonly Histogram<double> _similarityScore;
    private readonly Histogram<double> _similarityDuration;
    private readonly Counter<long> _similarityUnavailable;

    /// <summary>Creates the instruments.</summary>
    public StoryGenerationMetrics()
    {
        _duration = _meter.CreateHistogram<double>(DurationHistogramName, "s", "Time spent in one generation stage.");
        _invalidOutput = _meter.CreateCounter<long>(InvalidOutputCounterName, "1", "Invalid writer, review or revision outputs.");
        _runs = _meter.CreateCounter<long>(RunsCounterName, "1", "Generation runs that reached a status.");
        _softTimeout = _meter.CreateCounter<long>(SoftTimeoutCounterName, "1", "Runs that passed 80 % of their deadline.");
        _similarityScore = _meter.CreateHistogram<double>(SimilarityScoreHistogramName, "1", "Generation similarity score.");
        _similarityDuration = _meter.CreateHistogram<double>(SimilarityDurationHistogramName, "s", "Time spent loading and scoring similarity.");
        _similarityUnavailable = _meter.CreateCounter<long>(SimilarityUnavailableCounterName, "1", "Similarity corpus failures.");
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

    /// <summary>Records a completed score and its duration.</summary>
    public void SimilarityScored(string corpus, double score, double seconds)
    {
        _similarityScore.Record(score, new KeyValuePair<string, object?>("corpus", corpus));
        _similarityDuration.Record(seconds);
    }

    /// <summary>Counts a corpus failure using the closed unavailable-reason set.</summary>
    public void SimilarityUnavailable(string reason) =>
        _similarityUnavailable.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
