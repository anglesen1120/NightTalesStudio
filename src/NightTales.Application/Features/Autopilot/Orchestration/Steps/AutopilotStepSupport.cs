using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories.Generation;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// The guards and the reads every fiction step repeats (02.8.3 plan §3.4, §3.5): the missed-slot cutoff, the
/// authorization of the series and the idempotency-keyed rows of a step.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here mutates a run: a step calls these, then moves the aggregate itself, so the only writer of the
/// state machine stays one call per <see cref="IAutopilotStepHandler.HandleAsync"/>. The helpers are internal
/// because they are the mechanics of this namespace, not a contract another feature may lean on.
/// </para>
/// <para>
/// The codes they hand back are closed values (<see cref="StepResult.Code"/>, log fields, owner-action reasons),
/// never free text: D19 forbids logging episode content, so a step logs ids, codes and durations only.
/// </para>
/// </remarks>
internal static class AutopilotStepSupport
{
    /// <summary>The code of a run parked because its series has no current authorization (plan §3.5).</summary>
    internal const string AwaitingAuthorizationCode = "awaiting_authorization";

    /// <summary>The code of a run stopped for the owner; the wire value of <see cref="AutopilotRunState.WaitingOwner"/>.</summary>
    internal const string WaitingOwnerCode = "waiting_owner";

    /// <summary>The code of a run that used both story attempts; the wire value of <see cref="AutopilotRunState.GateFailed"/>.</summary>
    internal const string GateFailedCode = "gate_failed";

    /// <summary>
    /// The code of a run whose <c>idea_id</c> names an idea row that is gone: a data defect, never user input, so
    /// the run fails instead of retrying forever.
    /// </summary>
    internal const string IdeaMissingCode = "idea_missing";

    /// <summary>The code of a run in <see cref="AutopilotRunState.StoryGenerating"/> without an episode.</summary>
    internal const string EpisodeMissingCode = "episode_missing";

    /// <summary>The code of an idea step whose run row is queued or running: the holder is the current tick.</summary>
    internal const string IdeaInFlightCode = "idea_in_flight";

    /// <summary>The code of a story step still waiting for its generation (queued, running or regenerating).</summary>
    internal const string StoryInFlightCode = "story_in_flight";

    /// <summary>The code of a story gate that found the evaluated version already approved (S67-9 containment).</summary>
    internal const string StoryApprovedCode = "story_approved";

    /// <summary>The code of a story gate that stopped the run for the owner.</summary>
    internal const string StoryHeldCode = "story_held";

    /// <summary>The code of a story gate that spent the second attempt on one regeneration.</summary>
    internal const string StoryRegeneratedCode = "story_regenerated";

    /// <summary>The code of a story step that found the episode past the story state: the gate step decides on it.</summary>
    internal const string StoryReadyCode = "story_ready";

    /// <summary>
    /// The code of a story step that found the episode still a draft after a finite number of attempts with neither
    /// a failed run row nor a live generation: the outcome is unknown, so the run waits instead of paying twice.
    /// </summary>
    internal const string StoryOutcomeUnknownCode = "story_outcome_unknown";

    /// <summary>The wire value of <see cref="WaitingFor.Story"/> (the <c>autopilot_runs.waiting_for</c> column).</summary>
    internal const string WaitingForStory = "story";

    /// <summary>Is the run still before its video, i.e. is a missed slot a failed slot for it (plan §3.4)?</summary>
    /// <param name="run">The tracked run.</param>
    /// <returns>
    /// True while <c>video_gate</c> is still ahead of the run's <c>step</c>. From the video gate onwards the render
    /// exists and the late-publish decision belongs to the publication steps, not to this cutoff.
    /// </returns>
    internal static bool IsBeforeVideo(AutopilotRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return AutopilotRunStep.IsBehind(run.Step, AutopilotRunStep.VideoGate);
    }

    /// <summary>Has the run passed the instant after which its video can no longer be published in its slot (plan §3.4)?</summary>
    /// <param name="context">The run, its timing and the tick's clock reading.</param>
    /// <returns>True once <see cref="AutopilotTiming.SlotMissedCutoff"/> is reached and the run is still before its video.</returns>
    internal static bool SlotIsMissed(AutopilotStepContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return IsBeforeVideo(context.Run)
            && context.Now >= AutopilotTiming.SlotMissedCutoff(context.Timing, context.TimingOptions);
    }

    /// <summary>Fails the run for a missed slot: the aggregate records <see cref="AutopilotRun.SlotMissedReason"/>.</summary>
    /// <param name="context">The run and the tick's clock reading.</param>
    /// <returns>A stopped result carrying the pause reason as its code.</returns>
    internal static StepResult FailSlotMissed(AutopilotStepContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Run.Fail(AutopilotRun.SlotMissedReason, context.Now);

        return StepResult.Stopped(AutopilotRun.SlotMissedReason);
    }

    /// <summary>Is the run still behind the step of that name (the single-writer rule of a paid step)?</summary>
    /// <param name="run">The tracked run.</param>
    /// <param name="step">One of the <see cref="AutopilotRunStep"/> names.</param>
    /// <returns>True when the step has not been completed yet; a replay of a completed step is a no-op.</returns>
    internal static bool IsBehind(AutopilotRun run, string step)
    {
        ArgumentNullException.ThrowIfNull(run);

        return AutopilotRunStep.IsBehind(run.Step, step);
    }

    /// <summary>
    /// The series' current authorization, if it is still valid (plan §3.7): the revision
    /// <c>series.current_autopilot_authorization_id</c> names, provided that row exists and was never revoked.
    /// </summary>
    /// <param name="db">The scope's context (the tenant filter is the owner's).</param>
    /// <param name="series">The run's series.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The authorization id, or <see langword="null"/> when the series must wait for the owner to accept one.</returns>
    internal static async Task<Guid?> CurrentAuthorizationAsync(
        IAppDbContext db,
        SeriesAggregate series,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(series);

        if (series.CurrentAutopilotAuthorizationId is not { } authorizationId)
        {
            return null;
        }

        // The revocation is a column of the same row, so one read answers both questions: the revision must exist
        // and must not have been revoked. A series naming a revision that is gone is not authorized either.
        var row = await db.SeriesAutopilotAuthorizations
            .Where(candidate => candidate.Id == authorizationId)
            .Select(candidate => new { candidate.Id, candidate.RevokedAt })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return row is not null && row.RevokedAt is null ? row.Id : null;
    }

    /// <summary>One <c>generation_runs</c> row of this run by its idempotency key.</summary>
    /// <param name="db">The scope's context.</param>
    /// <param name="run">The run whose key is built.</param>
    /// <param name="kind">The kind the row must have (<c>ideas</c>, <c>story</c>); the key already names the step.</param>
    /// <param name="step">One of the <see cref="AutopilotRunStep"/> names the key is built for.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The row the previous tick committed, or <see langword="null"/> when the step never dispatched.</returns>
    internal static Task<GenerationRun?> FindGenerationRunAsync(
        IAppDbContext db,
        AutopilotRun run,
        GenerationKind kind,
        string step,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(run);

        var key = AutopilotIdempotencyKeys.For(run, step);

        return db.GenerationRuns.FirstOrDefaultAsync(
            candidate => candidate.SeriesId == run.SeriesId
                && candidate.Kind == kind
                && candidate.IdempotencyKey == key,
            ct);
    }
}
