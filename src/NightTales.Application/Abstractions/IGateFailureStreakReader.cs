using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Abstractions;

/// <summary>
/// The gate-failure streak of one series (02.8.2 plan §3.5, consumed by 02.8.3-T4): how many of the two most recent
/// terminal <c>autopilot_runs</c> failed the gate, which is 0 unless every one of them did, plus the series' latest run.
/// </summary>
/// <remarks>
/// <para>
/// The streak is derived, never stored: 02.8.3-T4 pauses a series when it reaches its bound, so a column would be
/// a second source of truth for the same fact. A run is terminal when its <c>outcome</c> is not null (02.8.3-T1
/// writes it when the state machine stops); a run still in flight is <em>skipped</em> rather than counted, and a
/// terminal run that did not fail ends the streak: a <c>published</c> run in between two failures is not a run of
/// two failures, so the answer is 0. The count also stops at the reader's own window — the plan's <c>LIMIT 2</c> —
/// so it answers 0, 1 or 2.
/// </para>
/// <para>
/// The read is scoped to one owner by the caller's system context (the D10 tenant filter), so a series of another
/// account is never part of an answer. A caller outside a user scope is refused rather than answered: see the
/// Infrastructure implementation, where a filtered-empty read would look exactly like a healthy series.
/// </para>
/// </remarks>
public interface IGateFailureStreakReader
{
    /// <summary>
    /// How many of the two most recent terminal runs of <paramref name="seriesId"/> failed the gate, up to the
    /// reader's window; 0 when any of those runs did not fail the gate (or the series has no terminal run).
    /// </summary>
    /// <param name="seriesId">The series; the caller's scope is what limits the rows to its owner.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    ValueTask<int> GetStreakAsync(Guid seriesId, CancellationToken ct);

    /// <summary>
    /// The series' most recent run whatever its outcome, or <see langword="null"/> when the series has none.
    /// </summary>
    /// <param name="seriesId">The series; the caller's scope is what limits the rows to its owner.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    ValueTask<AutopilotRunGateSnapshot?> GetLatestAsync(Guid seriesId, CancellationToken ct);
}

/// <summary>
/// What 02.8.3-T4 reads off the series' latest autopilot run to write its pause alert and its thresholds patch
/// (02.8.2 plan §3.5). Ids, the stored outcome, the pause code and the <c>gates.v1</c> document as stored — the
/// document's own shape belongs to 02.8.2-T1 and is read by the caller that owns the thresholds.
/// </summary>
/// <param name="RunId">The run row.</param>
/// <param name="EpisodeId">The episode it is producing, or <see langword="null"/> before one exists.</param>
/// <param name="StartedAt">The instant the row was planned (<c>autopilot_runs.started_at</c>).</param>
/// <param name="Outcome">The stored terminal outcome, or <see langword="null"/> while the run is in flight.</param>
/// <param name="PauseReason">
/// The wire value (<c>gate_failed_streak</c>, <c>autopilot_channel_unsupported</c>, …) of the trigger the run state
/// machine wrote when it paused the run, or <see langword="null"/>.
/// </param>
/// <param name="GateResults">The <c>gates.v1</c> document of the run's verdicts, or <see langword="null"/> before one.</param>
public sealed record AutopilotRunGateSnapshot(
    Guid RunId,
    Guid? EpisodeId,
    DateTimeOffset StartedAt,
    AutopilotRunOutcome? Outcome,
    string? PauseReason,
    string? GateResults);
