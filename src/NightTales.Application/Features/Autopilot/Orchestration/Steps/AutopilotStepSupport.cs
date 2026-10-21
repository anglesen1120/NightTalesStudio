using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
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

    /// <summary>
    /// Whether the run may make its next paid dispatch (task file rows SEC-1, PF-5; plan §3.6): the series still
    /// exists, is not archived, its autopilot is on and not paused, and its current authorization is a live revision
    /// of <b>this</b> series.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every step that buys something (an idea, a story, its one regeneration, a fact sheet, a source search, a
    /// render, or a command an owner answer triggers) asks this first, because the guards of the earlier steps run
    /// once: without it a run that already passed <c>idea</c> keeps spending after the owner revokes the
    /// authorization, disables autopilot or the series is archived (02.8.3-T4 owns the series-level trigger and the
    /// alert; this helper owns the run).
    /// </para>
    /// <para>
    /// The decision moves the run itself, so a step cannot forget the move: an archived series fails the run with
    /// <c>series_archived</c> (nothing may be produced for it again), a switched-off or paused autopilot pauses it
    /// with the series' own reason — the owner paused it, so no alert copy is owed — and a missing, revoked or
    /// foreign current authorization pauses it, because the §3.5 table has no <c>→ awaiting_authorization</c> row
    /// outside <c>planned</c> and the step must not guess one. The pause is the one the owner clears with
    /// <c>POST /series/{id}/autopilot/resume</c> after re-accepting.
    /// </para>
    /// </remarks>
    /// <param name="db">The scope's context (the tenant filter is the owner's).</param>
    /// <param name="context">The run, its series and the tick's clock reading.</param>
    /// <param name="ct">Cancels the authorization read.</param>
    /// <returns>The spend decision; a stop is already recorded on the run, so the step only returns its result.</returns>
    internal static async Task<SeriesSpendDecision> SeriesMaySpendAsync(
        IAppDbContext db,
        AutopilotStepContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(context);

        var series = context.Series;

        if (series.ArchivedAt is not null)
        {
            context.Run.Fail(ErrorCodes.SeriesArchived, context.Now);

            return new SeriesSpendDecision(StepResult.Stopped(ErrorCodes.SeriesArchived), ErrorCodes.SeriesArchived);
        }

        if (!series.AutopilotEnabled || series.AutopilotPausedAt is not null)
        {
            var reason = SeriesPauseReason(series);

            context.Run.Pause(reason, context.Now);

            var code = PauseReasonWire(reason);

            return new SeriesSpendDecision(StepResult.Stopped(code), code);
        }

        if (await CurrentAuthorizationAsync(db, series, ct).ConfigureAwait(false) is null)
        {
            context.Run.Pause(SeriesPauseReason(series), context.Now);

            return new SeriesSpendDecision(
                StepResult.Stopped(ErrorCodes.AutopilotAuthorizationRequired),
                ErrorCodes.AutopilotAuthorizationRequired);
        }

        return new SeriesSpendDecision(Stop: null, Code: string.Empty);
    }

    /// <summary>
    /// The §3.6 reason a series carries, mapped back from its stored wire value; <see cref="AutopilotPauseReason.OwnerPaused"/>
    /// when the series records none (an autopilot that is merely switched off, or a text this build does not know).
    /// </summary>
    /// <param name="series">The run's series.</param>
    /// <returns>The pause reason to record on the run.</returns>
    private static AutopilotPauseReason SeriesPauseReason(SeriesAggregate series)
    {
        if (string.IsNullOrEmpty(series.AutopilotPauseReason))
        {
            return AutopilotPauseReason.OwnerPaused;
        }

        foreach (var candidate in Enum.GetValues<AutopilotPauseReason>())
        {
            if (string.Equals(PauseReasonWire(candidate), series.AutopilotPauseReason, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return AutopilotPauseReason.OwnerPaused;
    }

    /// <summary>
    /// The stored form of a pause reason: the snake_case member name, except <see cref="AutopilotPauseReason.ChannelUnsupported"/>
    /// whose §3.6 row 6b wire value is <c>autopilot_channel_unsupported</c>.
    /// </summary>
    /// <param name="reason">The reason.</param>
    /// <returns>The wire value the column, the <c>CHECK</c> and the alert copy key use.</returns>
    internal static string PauseReasonWire(AutopilotPauseReason reason) =>
        reason == AutopilotPauseReason.ChannelUnsupported
            ? AutopilotStepCodes.ChannelUnsupported
            : AutopilotRunMetrics.Wire(reason.ToString());

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
    /// <c>series.current_autopilot_authorization_id</c> names, provided that row exists, was never revoked and belongs
    /// to <paramref name="series"/>.
    /// </summary>
    /// <remarks>
    /// The <c>SeriesId</c> column is filtered rather than trusted (S71 SEC-5): the pointer is a column the owner's
    /// writes set, so a stale or cross-series id must not authorize a paid dispatch. The tenant filter alone would let
    /// another series' live revision of the same owner pass.
    /// </remarks>
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

        // The revocation is a column of the same row, so one read answers both questions: the revision must exist,
        // belong to this series and must not have been revoked. A series naming a revision that is gone is not
        // authorized either.
        var row = await db.SeriesAutopilotAuthorizations
            .Where(candidate => candidate.Id == authorizationId && candidate.SeriesId == series.Id)
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

/// <summary>
/// The answer of <see cref="AutopilotStepSupport.SeriesMaySpendAsync"/>: <see cref="Stop"/> is the result the step
/// returns when the run may not spend (the move is already recorded on the run), and <see langword="null"/> when the
/// paid dispatch may happen. <see cref="Code"/> is the closed code the step logs, empty when the dispatch is allowed.
/// </summary>
/// <param name="Stop">The stopped result to return, or <see langword="null"/> when the dispatch is allowed.</param>
/// <param name="Code">The stop's code (<c>series_archived</c>, a §3.6 pause reason, <c>autopilot_authorization_required</c>).</param>
internal readonly record struct SeriesSpendDecision(StepResult? Stop, string Code)
{
    /// <summary>Whether the step may make its paid dispatch.</summary>
    public bool MaySpend => Stop is null;
}
