using System.Diagnostics.Metrics;
using System.Text.Json;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Orchestration;

/// <summary>
/// The run orchestrator's instruments (02.8.3 plan §3 Steps 7, §9; s71-plan decision 9): what a run stopped
/// as, how long a step took, which slots the planner skipped, how often DST moved a slot, and how often two
/// producing runs of one series were seen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded tags only.</b> Every tag vocabulary is a closed set — the run states and steps of the ladder, the
/// stop outcomes of plan §3.5, the four skip reasons, the two DST kinds — and a value outside one is counted
/// under <see cref="OtherTagValue"/> instead of being published. The one open tag is the time-zone id, and it
/// is open only over the validated IANA set of the settings: it is never a title, a URL, an id or a code
/// (D19).
/// </para>
/// <para>
/// The meter is the autopilot feature's (<c>NightTales.Autopilot</c>, the one <c>IdeaMetrics</c> creates), so
/// one dashboard selects the meter and finds the idea, gate and run instruments together. Registered as a
/// singleton: one instrument per process, so a scrape never sees two series for one counter.
/// </para>
/// </remarks>
public sealed class AutopilotRunMetrics : IDisposable
{
    /// <summary>The meter the run instruments publish on, shared with the rest of the autopilot feature.</summary>
    public const string MeterName = IdeaMetrics.MeterName;

    /// <summary>One run that stopped, by state and outcome.</summary>
    public const string RunsTotalName = "autopilot_runs_total";

    /// <summary>How long one handler call took, by step.</summary>
    public const string StepDurationSecondsName = "autopilot_step_duration_seconds";

    /// <summary>One slot the planner did not create, by reason.</summary>
    public const string SlotSkippedTotalName = "autopilot_slot_skipped_total";

    /// <summary>One slot whose local time DST moved, by zone and kind.</summary>
    public const string SlotDstAdjustedTotalName = "autopilot_slot_dst_adjusted_total";

    /// <summary>One tick that saw another producing run of the same series.</summary>
    public const string ConcurrencyViolationTotalName = "autopilot_concurrency_violation_total";

    /// <summary>The tag naming the run state.</summary>
    public const string StateTagName = "state";

    /// <summary>The tag naming the stop outcome.</summary>
    public const string OutcomeTagName = "outcome";

    /// <summary>The tag naming the step (or state) a handler call started from.</summary>
    public const string StepTagName = "step";

    /// <summary>The tag naming why a slot was skipped.</summary>
    public const string ReasonTagName = "reason";

    /// <summary>The tag naming the time zone a slot was computed in.</summary>
    public const string ZoneTagName = "zone";

    /// <summary>The tag naming the DST adjustment.</summary>
    public const string DstKindTagName = "kind";

    /// <summary>The tag value every word outside a closed vocabulary is counted under.</summary>
    public const string OtherTagValue = "other";

    /// <summary>A series' settings failed validation, so the planner skipped the whole series.</summary>
    public const string SettingsInvalidReason = "settings_invalid";

    /// <summary>The slot instant was already behind the clock when the planner saw it.</summary>
    public const string SlotInPastReason = "slot_in_past";

    /// <summary>The slot's local day on that channel is already at the settings' daily cap.</summary>
    public const string DailyCapReason = "daily_cap";

    /// <summary>The spring-forward gap moved the local time forward.</summary>
    public const string DstGapKind = "gap";

    /// <summary>The fall-back overlap resolved to the earlier instant.</summary>
    public const string DstOverlapKind = "overlap";

    /// <summary>The closed set of run-state tags, in the wire spelling of the column.</summary>
    public static readonly IReadOnlySet<string> StateNames = new HashSet<string>(
        Enum.GetValues<AutopilotRunState>().Select(Wire),
        StringComparer.Ordinal);

    /// <summary>The closed set of stop-outcome tags: the eight stopping outcomes plus <c>other</c>.</summary>
    public static readonly IReadOnlySet<string> OutcomeNames = new HashSet<string>(
        Enum.GetValues<AutopilotRunOutcome>().Select(outcome => Wire(outcome.ToString())),
        StringComparer.Ordinal);

    /// <summary>
    /// The closed set of step tags: the run states (one handler per source state) plus the ladder's step names,
    /// so a caller may tag a call with either half without opening the vocabulary.
    /// </summary>
    public static readonly IReadOnlySet<string> StepNames = new HashSet<string>(
        [
            .. Enum.GetValues<AutopilotRunState>().Select(Wire),
            AutopilotRunStep.Authorization,
            AutopilotRunStep.Idea,
            AutopilotRunStep.FindSources,
            AutopilotRunStep.FactSheet,
            AutopilotRunStep.SourcesRequired,
            AutopilotRunStep.Story,
            AutopilotRunStep.StoryGate,
            AutopilotRunStep.Story2,
            AutopilotRunStep.Render,
            AutopilotRunStep.VideoGate,
            AutopilotRunStep.Publication,
            AutopilotRunStep.Consent,
            AutopilotRunStep.GoLive,
        ],
        StringComparer.Ordinal);

    /// <summary>The closed set of skip reasons.</summary>
    public static readonly IReadOnlySet<string> SkipReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        SettingsInvalidReason,
        SlotInPastReason,
        DailyCapReason,
    };

    /// <summary>The closed set of DST kinds.</summary>
    public static readonly IReadOnlySet<string> DstKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        DstGapKind,
        DstOverlapKind,
    };

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _runs;
    private readonly Histogram<double> _stepDuration;
    private readonly Counter<long> _slotsSkipped;
    private readonly Counter<long> _slotsDstAdjusted;
    private readonly Counter<long> _concurrencyViolations;

    /// <summary>Creates the five instruments.</summary>
    public AutopilotRunMetrics()
    {
        _runs = _meter.CreateCounter<long>(
            RunsTotalName,
            "1",
            "One autopilot run that stopped, tagged with the state and the outcome it wrote.");
        _stepDuration = _meter.CreateHistogram<double>(
            StepDurationSecondsName,
            "s",
            "How long one autopilot step handler call took, tagged with the state it started from.");
        _slotsSkipped = _meter.CreateCounter<long>(
            SlotSkippedTotalName,
            "1",
            "One slot the planner did not create, tagged with the reason.");
        _slotsDstAdjusted = _meter.CreateCounter<long>(
            SlotDstAdjustedTotalName,
            "1",
            "One slot whose local publish time DST moved, tagged with the zone and the kind of move.");
        _concurrencyViolations = _meter.CreateCounter<long>(
            ConcurrencyViolationTotalName,
            "1",
            "One tick that saw more producing runs of a series than Autopilot:Run:MaxProducingRunsPerSeries.");
    }

    /// <summary>
    /// Records that a run stopped (a terminal outcome, or a hold on the owner, a pause, a wait for the
    /// authorization): one count of <see cref="RunsTotalName"/>.
    /// </summary>
    /// <param name="state">The state the run stopped in.</param>
    /// <param name="outcome">The written outcome, or the reason code when no outcome was written.</param>
    public void RunStopped(AutopilotRunState state, string outcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        _runs.Add(
            1,
            new KeyValuePair<string, object?>(StateTagName, Bounded(Wire(state), StateNames)),
            new KeyValuePair<string, object?>(OutcomeTagName, Bounded(outcome, OutcomeNames)));
    }

    /// <summary>Records how long one handler call took; a negative duration is recorded as zero.</summary>
    /// <param name="step">The state (or step) the call started from.</param>
    /// <param name="duration">The elapsed time of the call.</param>
    public void StepDuration(string step, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step);

        _stepDuration.Record(
            duration > TimeSpan.Zero ? duration.TotalSeconds : 0,
            new KeyValuePair<string, object?>(StepTagName, Bounded(step, StepNames)));
    }

    /// <summary>Records one slot the planner did not create.</summary>
    /// <param name="reason">One of <see cref="SettingsInvalidReason"/>, <see cref="SlotInPastReason"/> or <see cref="DailyCapReason"/>.</param>
    public void SlotSkipped(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        _slotsSkipped.Add(1, new KeyValuePair<string, object?>(ReasonTagName, Bounded(reason, SkipReasons)));
    }

    /// <summary>Records one slot whose local publish time DST moved.</summary>
    /// <param name="zone">The IANA id of the series' cadence (validated by the settings validator).</param>
    /// <param name="kind"><see cref="DstGapKind"/> or <see cref="DstOverlapKind"/>.</param>
    public void SlotDstAdjusted(string zone, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zone);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        _slotsDstAdjusted.Add(
            1,
            new KeyValuePair<string, object?>(ZoneTagName, zone),
            new KeyValuePair<string, object?>(DstKindTagName, Bounded(kind, DstKinds)));
    }

    /// <summary>Records one tick that found more producing runs of a series than the configured maximum.</summary>
    public void ConcurrencyViolation() => _concurrencyViolations.Add(1);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    /// <summary>
    /// The wire spelling of a run state (<c>planned</c>, <c>story_gate</c>, …): the column's
    /// <c>EnumToSnakeCaseStringConverter</c> form, computed here because Application may not reference the
    /// Infrastructure converter.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>The snake_case name.</returns>
    public static string Wire(AutopilotRunState state) => Wire(state.ToString());

    /// <summary>The wire spelling of any enum member name (see <see cref="Wire(AutopilotRunState)"/>).</summary>
    /// <param name="memberName">The member name, e.g. <c>WaitingOwner</c>; a snake_case name passes through.</param>
    /// <returns>The snake_case name.</returns>
    public static string Wire(string memberName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);

        return memberName.Contains('_')
            ? memberName
            : JsonNamingPolicy.SnakeCaseLower.ConvertName(memberName);
    }

    private static string Bounded(string value, IReadOnlySet<string> vocabulary) =>
        vocabulary.Contains(value) ? value : OtherTagValue;
}
