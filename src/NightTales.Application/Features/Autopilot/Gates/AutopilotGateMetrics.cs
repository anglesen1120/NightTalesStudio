using System.Diagnostics.Metrics;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Domain.Stories.Gates;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// The gate engine's instruments (02.8.2 plan §3 Steps 8, §8): one counter per evaluation, one per failing
/// check and one histogram of how long an evaluation took, which is what T3's calibration and the run-log
/// dashboards read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded tags only.</b> All three tag vocabularies are closed sets — the gate, the result and the check id —
/// and a value outside one is counted under <see cref="OtherTagValue"/> instead of being published: a metric tag
/// is where a stray id, title or code would otherwise become an unbounded series, and this is the one place
/// Application code emits a tag (D19).
/// </para>
/// <para>
/// The meter is the autopilot feature's (<c>NightTales.Autopilot</c>, the one <c>IdeaMetrics</c> creates), so one
/// dashboard selects the meter and finds the idea, variety and gate instruments together.
/// </para>
/// <para>
/// Registered as a singleton: one instrument per process, so a scrape never sees two series for one counter
/// (the <c>StorageMetrics</c> rule).
/// </para>
/// </remarks>
public sealed class AutopilotGateMetrics : IDisposable
{
    /// <summary>The meter the gate instruments publish on, shared with the rest of the autopilot feature.</summary>
    public const string MeterName = IdeaMetrics.MeterName;

    /// <summary>One gate evaluation, by gate and result.</summary>
    public const string GateTotalName = "autopilot_gate_total";

    /// <summary>One failing check of one evaluation, by check id.</summary>
    public const string CheckFailedTotalName = "autopilot_gate_check_failed_total";

    /// <summary>How long one evaluation took, by gate and result.</summary>
    public const string DurationSecondsName = "autopilot_gate_duration_seconds";

    /// <summary>The tag naming which half ran: <c>story</c> or <c>video</c>.</summary>
    public const string GateTagName = "gate";

    /// <summary>The tag naming how the evaluation ended.</summary>
    public const string ResultTagName = "result";

    /// <summary>The tag naming the check that failed.</summary>
    public const string CheckTagName = "check";

    /// <summary>The tag value every word outside a closed vocabulary is counted under.</summary>
    public const string OtherTagValue = "other";

    /// <summary>The <see cref="GateTagName"/> value of the story half.</summary>
    public const string StoryGate = "story";

    /// <summary>The <see cref="GateTagName"/> value of the video half.</summary>
    public const string VideoGate = "video";

    /// <summary>Every check passed and, live, the episode moved.</summary>
    public const string PassResult = "pass";

    /// <summary>A check failed, or the evaluation could not read what it needed.</summary>
    public const string FailResult = "fail";

    /// <summary>The story was held for the owner by design (the true-crime hold).</summary>
    public const string HoldResult = "hold";

    /// <summary>The evaluation ran in dry-run mode and approved nothing.</summary>
    public const string DryRunResult = "dry_run";

    /// <summary>The evaluation itself failed: no verdict was produced.</summary>
    public const string ErrorResult = "error";

    /// <summary>The closed set of gates; anything else is counted as <see cref="OtherTagValue"/>.</summary>
    public static readonly IReadOnlySet<string> GateNames = new HashSet<string>(StringComparer.Ordinal)
    {
        StoryGate,
        VideoGate,
    };

    /// <summary>The closed set of results; anything else is counted as <see cref="OtherTagValue"/>.</summary>
    public static readonly IReadOnlySet<string> ResultNames = new HashSet<string>(StringComparer.Ordinal)
    {
        PassResult,
        FailResult,
        HoldResult,
        DryRunResult,
        ErrorResult,
    };

    /// <summary>
    /// The closed set of check ids of both halves, from the Domain's own lists: the ids are the strings
    /// <c>gate_results</c>, the run log and the copy keys share, so a new check becomes a new series here without
    /// a second place to update.
    /// </summary>
    public static readonly IReadOnlySet<string> CheckNames = new HashSet<string>(
        [.. StoryGateCheckIds.All, .. VideoGateCheckIds.All],
        StringComparer.Ordinal);

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _gates;
    private readonly Counter<long> _failedChecks;
    private readonly Histogram<double> _duration;

    /// <summary>Creates the three instruments.</summary>
    public AutopilotGateMetrics()
    {
        _gates = _meter.CreateCounter<long>(
            GateTotalName,
            "1",
            "One autopilot gate evaluation, tagged with the gate and how it ended.");
        _failedChecks = _meter.CreateCounter<long>(
            CheckFailedTotalName,
            "1",
            "One failing check of one gate evaluation, tagged with the check id: the per-check failure rate.");
        _duration = _meter.CreateHistogram<double>(
            DurationSecondsName,
            "s",
            "How long one gate evaluation took, tagged with the gate and how it ended.");
    }

    /// <summary>
    /// Records one finished evaluation: its outcome, each failing check and its duration, in one call, so a
    /// caller cannot record a verdict without its duration or omit a failure.
    /// </summary>
    /// <param name="gate">The half that ran: <c>story</c> or <c>video</c>.</param>
    /// <param name="result">One of <see cref="PassResult"/>, <see cref="FailResult"/>, <see cref="HoldResult"/>, <see cref="DryRunResult"/> or <see cref="ErrorResult"/>.</param>
    /// <param name="elapsed">How long the evaluation took; negative values are clamped to zero.</param>
    /// <param name="failedCheckIds">
    /// The ids the verdict failed on, in verdict order; an empty list for a pass. An id outside
    /// <see cref="CheckNames"/> is counted as <see cref="OtherTagValue"/>, and duplicates are counted once so one
    /// verdict cannot inflate a check's failure rate.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="failedCheckIds"/> is <see langword="null"/>.</exception>
    public void RecordGate(string gate, string result, TimeSpan elapsed, IReadOnlyList<string> failedCheckIds)
    {
        ArgumentNullException.ThrowIfNull(failedCheckIds);

        var boundedGate = Known(gate, GateNames);
        var boundedResult = Known(result, ResultNames);
        var seconds = elapsed > TimeSpan.Zero ? elapsed.TotalSeconds : 0;

        _gates.Add(
            1,
            new KeyValuePair<string, object?>(GateTagName, boundedGate),
            new KeyValuePair<string, object?>(ResultTagName, boundedResult));
        _duration.Record(
            seconds,
            new KeyValuePair<string, object?>(GateTagName, boundedGate),
            new KeyValuePair<string, object?>(ResultTagName, boundedResult));

        foreach (var check in failedCheckIds.Distinct(StringComparer.Ordinal))
        {
            _failedChecks.Add(1, new KeyValuePair<string, object?>(CheckTagName, Known(check, CheckNames)));
        }
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    private static string Known(string value, IReadOnlySet<string> vocabulary) =>
        vocabulary.Contains(value) ? value : OtherTagValue;
}
