using NightTales.Domain.Series.Autopilot;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.Autopilot.Orchestration;

/// <summary>
/// One step of the autopilot run state machine (02.8.3 plan §3.5, task file §Design): the handler registered for
/// <see cref="State"/> is called by <see cref="AutopilotRunOrchestrator"/> when a run sitting in that state is advanced.
/// </summary>
/// <remarks>
/// <para>
/// <b>One handler per source state.</b> A handler does at most one unit of work per call (one command, one gate
/// evaluation, one state change) and mutates only the run through its aggregate methods; the orchestrator saves the
/// row once, guarded by the run's <c>xmin</c> token (a lost race is a no-op, not an error). Handlers are scoped
/// services resolved inside the per-run DI scope, after <c>ISystemContext.For(run.UserId)</c>.
/// </para>
/// <para>
/// A step that is already behind the run (<see cref="AutopilotRunStep.IsBehind"/> says the run has done it) is
/// skipped by returning <see cref="StepResult.Idle"/> without effects; a paid step re-entered after a crash re-sends
/// its command with the same idempotency key (<see cref="AutopilotIdempotencyKeys"/>) and adopts the result.
/// </para>
/// <para>
/// Handlers never log or tag stories, briefs, fact sheets, URLs or titles (D19): ids, step names, codes and
/// durations only.
/// </para>
/// </remarks>
public interface IAutopilotStepHandler
{
    /// <summary>The run state this handler advances out of.</summary>
    AutopilotRunState State { get; }

    /// <summary>Does the work of this state once. Never throws for an expected failure: it returns a <see cref="StepResult"/>.</summary>
    ValueTask<StepResult> HandleAsync(AutopilotStepContext context, CancellationToken ct);
}

/// <summary>What a handler sees: the tracked run and the rows the orchestrator already loaded under the owner's scope.</summary>
/// <param name="Run">The tracked run row; the handler mutates it through its aggregate methods only.</param>
/// <param name="Series">The series of the run.</param>
/// <param name="Settings">The parsed <c>series.autopilot_settings</c> (validated; a run whose settings fail validation never reaches a handler).</param>
/// <param name="Timing">The derived instants of this run (plan §3.4), computed from the slot and the settings.</param>
/// <param name="TimingOptions">The platform timing options the instants were computed with.</param>
/// <param name="Now">The tick's clock reading (one per call, <c>IClock.UtcNow</c>).</param>
public sealed record AutopilotStepContext(
    AutopilotRun Run,
    SeriesAggregate Series,
    AutopilotSettings Settings,
    AutopilotRunTiming Timing,
    AutopilotTimingOptions TimingOptions,
    DateTimeOffset Now);

/// <summary>How one handler call ended; the orchestrator turns it into metrics, logs and the next tick's behaviour.</summary>
/// <param name="Kind">The outcome class.</param>
/// <param name="Code">A closed code (an error code, a <c>pause_reason</c>, a step name) for logs and metric tags; never free text.</param>
public sealed record StepResult(StepResultKind Kind, string? Code = null)
{
    /// <summary>Nothing to do now (waiting for an outbox event, for the clock, or the step is already behind the run).</summary>
    public static StepResult Idle { get; } = new(StepResultKind.Idle);

    /// <summary>The handler changed the run (state, step, ids); the orchestrator saves it.</summary>
    public static StepResult Advanced(string? step = null) => new(StepResultKind.Advanced, step);

    /// <summary>The handler moved the run to a stopping state it must own (waiting_owner, paused, failed, gate_failed, …); the orchestrator saves it and counts it.</summary>
    public static StepResult Stopped(string? code = null) => new(StepResultKind.Stopped, code);

    /// <summary>A transient failure (a dependency was unavailable); the run is untouched and the next tick retries.</summary>
    public static StepResult Retry(string? code = null) => new(StepResultKind.Retry, code);
}

/// <summary>The outcome classes of <see cref="StepResult"/>.</summary>
public enum StepResultKind
{
    /// <summary>No change.</summary>
    Idle,

    /// <summary>The run was advanced and must be saved.</summary>
    Advanced,

    /// <summary>The run was stopped (waiting for the owner, paused, failed, gate failed) and must be saved.</summary>
    Stopped,

    /// <summary>Nothing was saved; try again next tick.</summary>
    Retry,
}

/// <summary>The idempotency keys of one autopilot run (task file §Idempotency): <c>autopilot:{seriesId}:{slotAt:O}:{step}</c>, slot in UTC.</summary>
public static class AutopilotIdempotencyKeys
{
    /// <summary>Builds the key for <paramref name="step"/> of the run of <paramref name="seriesId"/> at <paramref name="slotAt"/>.</summary>
    public static string For(Guid seriesId, DateTimeOffset slotAt, string step) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"autopilot:{seriesId}:{slotAt.ToUniversalTime():O}:{step}");

    /// <summary>The key of <paramref name="step"/> of <paramref name="run"/>.</summary>
    public static string For(AutopilotRun run, string step)
    {
        ArgumentNullException.ThrowIfNull(run);
        return For(run.SeriesId, run.SlotAt, step);
    }
}
