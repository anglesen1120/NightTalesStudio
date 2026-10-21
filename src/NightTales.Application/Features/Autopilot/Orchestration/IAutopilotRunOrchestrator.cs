using ErrorOr;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Orchestration;

/// <summary>
/// The one way 02.8.3's steps are driven (plan §3.5, §7.1/§7.2): one handler call per
/// <see cref="AdvanceAsync"/>, a bounded chain per <see cref="AdvanceWhileProgressingAsync"/>, and the tick's
/// due-run scan per <see cref="AdvanceDueAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One run, one scope, one save.</b> Every call opens its own DI scope, pins it to the run's owner with
/// <c>ISystemContext.For(run.UserId)</c> <em>before</em> any <c>DbContext</c> is resolved, calls the handler
/// registered for the run's state once, and persists the tracked row once. The row's concurrency token is the
/// arbiter: a lost race (<see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>) is a no-op
/// that answers <see cref="StepResultKind.Idle"/>, so two writers cannot both advance the same run.
/// </para>
/// <para>
/// <b>Never unfiltered.</b> There is no mode that reads across tenants: the tick finds due
/// <c>(id, user_id)</c> pairs through <see cref="Abstractions.ISystemWorkQuery"/> and each row is then read
/// under its owner's filter. An unknown or empty user id fails the step and is logged (ids only).
/// </para>
/// </remarks>
public interface IAutopilotRunOrchestrator
{
    /// <summary>
    /// Advances one run by exactly one handler call, in its own scope pinned to <paramref name="userId"/>.
    /// </summary>
    /// <param name="runId">The run to advance.</param>
    /// <param name="userId">The run's owner; required for <c>ISystemContext.For</c> and never <see cref="Guid.Empty"/>.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>
    /// What the call did, or an error: <c>autopilot_user_required</c> when the owner is missing,
    /// <c>autopilot_run_not_found</c> when the row is not visible to that owner, <c>autopilot_step_failed</c>
    /// when the handler or the row it needs blew up.
    /// </returns>
    Task<ErrorOr<AdvanceResult>> AdvanceAsync(Guid runId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Repeats <see cref="AdvanceAsync"/> while it answers <see cref="StepResultKind.Advanced"/>, at most
    /// <paramref name="maxSteps"/> times, so a catch-up run walks its ladder in one tick without starving the
    /// others.
    /// </summary>
    /// <param name="runId">The run to advance.</param>
    /// <param name="userId">The run's owner.</param>
    /// <param name="maxSteps">The largest number of handler calls; values below one are treated as one.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The last call's result, or its error.</returns>
    Task<ErrorOr<AdvanceResult>> AdvanceWhileProgressingAsync(Guid runId, Guid userId, int maxSteps, CancellationToken ct);

    /// <summary>
    /// Advances up to <paramref name="max"/> runs that are due at <paramref name="now"/>, each through
    /// <see cref="AdvanceWhileProgressingAsync"/> with the configured per-run step budget.
    /// </summary>
    /// <param name="now">The tick's clock reading, read once.</param>
    /// <param name="max">The largest number of runs this tick touches (the actionable pass takes it all first).</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>
    /// How many due runs the tick handled without an error (a run that only answered idle is counted); a failing run
    /// never stops the others.
    /// </returns>
    Task<int> AdvanceDueAsync(DateTimeOffset now, int max, CancellationToken ct);
}

/// <summary>What one orchestrator call did to one run.</summary>
/// <param name="State">The run's state after the call (the loaded state when nothing was saved).</param>
/// <param name="Kind">How the handler call ended — what the caller keys its next decision on.</param>
/// <param name="Code">A closed code: the handler's code, or the skip's reason (<c>concurrency_lost</c>, …).</param>
public sealed record AdvanceResult(AutopilotRunState State, StepResultKind Kind, string? Code);
