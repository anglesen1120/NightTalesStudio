using Microsoft.EntityFrameworkCore;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;

namespace NightTales.TestKit.Builders;

/// <summary>
/// Inserts real <c>autopilot_runs</c> rows (02.8.3 plan §3.3) through <see cref="AutopilotRun.Plan"/> and the tracked
/// write model, so a seeded run carries exactly what the planner writes: a UUIDv7, the slot and start instants, the
/// snapshot authorization and <c>state = planned</c>.
/// </summary>
/// <remarks>
/// <para>
/// The scenario columns — state, step, outcome, pause reason, the two counters, the owner ask and the stored
/// <c>gates.v1</c> document — are written through the change tracker (<see cref="ShellInstance.Set{T}"/>), the same
/// route <see cref="SeriesBuilder"/> uses for a row no factory of its owner sets. The aggregate's own methods are the
/// production path for those moves and stay the only way to make a transition inside a test that exercises one; a
/// seed needs an arbitrary position of the machine, which the transition table would refuse to reach in one step.
/// </para>
/// <para>
/// A parameter that belongs to a state (a step, a pause reason, an outcome) without <paramref name="state"/> being
/// given is a builder mistake and throws, so a seeded row cannot end up with a counter or an ask that contradicts
/// its position.
/// </para>
/// </remarks>
public sealed class AutopilotRunBuilder(TestDatabase database, TestCurrentUser currentUser, IClock? clock = null)
{
    /// <summary>
    /// Inserts one run of <paramref name="seriesId"/> for <paramref name="userId"/> and returns it.
    /// </summary>
    /// <param name="userId">The owner; it must own the series.</param>
    /// <param name="seriesId">The series whose cadence produced the slot.</param>
    /// <param name="slotAt">The slot's intended publish instant; defaults to <paramref name="now"/>.</param>
    /// <param name="startAt">The computed earliest safe start; defaults to <paramref name="slotAt"/> minus two days.</param>
    /// <param name="now">The planned instant (also <c>last_transition_at</c>); defaults to the clock or the wall clock.</param>
    /// <param name="authorizationId">The series' current authorization, or <see langword="null"/>.</param>
    /// <param name="episodeId">The episode the run produced, attached through the aggregate.</param>
    /// <param name="ideaId">The idea the run picked, attached through the aggregate.</param>
    /// <param name="publicationId">The publication the run created, attached through the aggregate.</param>
    /// <param name="state">The position to seed; <see cref="AutopilotRunState.Planned"/> when omitted.</param>
    /// <param name="outcome">The stored outcome; derived from <paramref name="state"/> when omitted.</param>
    /// <param name="step">The last completed step's name, one of <see cref="AutopilotRunStep"/>'s constants.</param>
    /// <param name="pauseReason">The stored pause trigger.</param>
    /// <param name="waitingFor">The stored owner ask.</param>
    /// <param name="attempt">Story generations consumed.</param>
    /// <param name="renderAttempts">Renders consumed.</param>
    /// <param name="gateResults">The stored <c>gates.v1</c> document.</param>
    /// <param name="finishedAt">The terminal instant; defaults to <paramref name="now"/> in a terminal state and <see langword="null"/> otherwise.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The tracked row, detached after its own save.</returns>
    /// <exception cref="ArgumentException">
    /// An owner or series is <see cref="Guid.Empty"/>; a scenario column is given without a <paramref name="state"/>.
    /// </exception>
    public async Task<AutopilotRun> CreateAsync(
        Guid userId,
        Guid seriesId,
        DateTimeOffset? slotAt = null,
        DateTimeOffset? startAt = null,
        DateTimeOffset? now = null,
        Guid? authorizationId = null,
        Guid? episodeId = null,
        Guid? ideaId = null,
        Guid? publicationId = null,
        AutopilotRunState? state = null,
        AutopilotRunOutcome? outcome = null,
        string? step = null,
        AutopilotPauseReason? pauseReason = null,
        WaitingFor? waitingFor = null,
        int attempt = 0,
        int renderAttempts = 0,
        string? gateResults = null,
        DateTimeOffset? finishedAt = null,
        CancellationToken ct = default)
    {
        if (state is null && (outcome is not null || step is not null || pauseReason is not null
            || waitingFor is not null || attempt != 0 || renderAttempts != 0 || gateResults is not null
            || finishedAt is not null))
        {
            throw new ArgumentException(
                "A scenario column (outcome, step, pause reason, owner ask, counters, gate results, finish instant) "
                + "needs the state it belongs to: pass state as well (AutopilotRunBuilder).",
                nameof(state));
        }

        var plannedAt = now ?? clock?.UtcNow ?? DateTimeOffset.UtcNow;
        var slot = slotAt ?? plannedAt;
        // Two days is the plan §3.4 worked example's buffer; a caller that cares about the exact lead passes startAt.
        var start = startAt ?? slot.AddDays(-2);

        var run = AutopilotRun.Plan(userId, seriesId, slot, start, authorizationId, plannedAt);

        if (ideaId is { } idea)
        {
            run.AttachIdea(idea);
        }

        if (episodeId is { } episode)
        {
            run.AttachEpisode(episode);
        }

        if (publicationId is { } publication)
        {
            run.AttachPublication(publication);
        }

        await using var context = database.CreateAppDbContext(currentUser, clock);
        context.AutopilotRuns.Add(run);

        if (state is { } seededState)
        {
            ShellInstance.Set(context, run, nameof(AutopilotRun.State), seededState);
            ShellInstance.Set(
                context,
                run,
                nameof(AutopilotRun.Outcome),
                outcome ?? AutopilotRunStateMachine.OutcomeFor(seededState));

            if (step is not null)
            {
                ShellInstance.Set(context, run, nameof(AutopilotRun.Step), step);
            }

            if (pauseReason is not null)
            {
                ShellInstance.Set(context, run, nameof(AutopilotRun.PauseReason), pauseReason);
            }

            if (waitingFor is not null)
            {
                ShellInstance.Set(context, run, nameof(AutopilotRun.WaitingFor), waitingFor);
            }

            ShellInstance.Set(context, run, nameof(AutopilotRun.LastTransitionAt), plannedAt);

            if (AutopilotRunStateMachine.IsTerminal(seededState))
            {
                ShellInstance.Set(context, run, nameof(AutopilotRun.FinishedAt), finishedAt ?? plannedAt);
            }
        }

        if (attempt != 0)
        {
            ShellInstance.Set(context, run, nameof(AutopilotRun.Attempt), attempt);
        }

        if (renderAttempts != 0)
        {
            ShellInstance.Set(context, run, nameof(AutopilotRun.RenderAttempts), renderAttempts);
        }

        if (gateResults is not null)
        {
            ShellInstance.Set(context, run, nameof(AutopilotRun.GateResults), gateResults);
        }

        await context.SaveChangesAsync(ct);

        return run;
    }
}
