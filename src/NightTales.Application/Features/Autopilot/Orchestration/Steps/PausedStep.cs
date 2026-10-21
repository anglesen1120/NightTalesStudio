using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// The handler of <see cref="AutopilotRunState.Paused"/>: a held run has nothing to do, so the step answers
/// <see cref="StepResult.Idle"/> and touches nothing.
/// </summary>
/// <remarks>
/// An owner's <c>resume</c> is not a tick: it goes through <see cref="AutopilotRun.Resume"/>, which moves the row
/// back to the state its step implies (02.8.3-T2 owns the route). This handler exists because the orchestrator
/// resolves exactly one handler per state — an explicit <c>AdvanceAsync</c> of a paused run is a no-op, not a
/// missing-handler error — and the due-run query never selects a paused row.
/// </remarks>
public sealed class PausedStep : IAutopilotStepHandler
{
    /// <inheritdoc />
    public AutopilotRunState State => AutopilotRunState.Paused;

    /// <inheritdoc />
    public ValueTask<StepResult> HandleAsync(AutopilotStepContext context, CancellationToken ct) =>
        ValueTask.FromResult(StepResult.Idle);
}
