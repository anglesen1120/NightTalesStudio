using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Commands.GenerateStory;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Application.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// <see cref="AutopilotRunState.StoryGenerating"/>: wait for the story the episode requested, move to
/// <see cref="AutopilotRunState.StoryGate"/> when it lands, and spend the run's one regeneration when the first
/// generation failed (plan §3.5 rows <c>idea_picked → story_generating</c>, <c>story_generating → story_gate</c>,
/// <c>story_gate → story_generating</c>; plan §3.6 platform failures versus moderation).
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the verdict comes from.</b> Nothing in this step waits on an event: the
/// <c>story_generated</c>/<c>story_failed</c> handlers and this tick read the same rows, and the episode's
/// <see cref="Episode.Status"/> is the outcome the writer already committed — a story that landed moves the run on,
/// a draft after a failed run row is the failure. That makes the step idempotent without a subscription and lets a
/// missed outbox message cost one tick instead of the slot.
/// </para>
/// <para>
/// <b>Attempts.</b> Attempt 1 is spent by the state that requested the story (<see cref="CreateEpisodeStep"/>), so
/// this step only ever dispatches <c>…:story2</c>: once, after the gate failed attempt 1 and
/// <see cref="AutopilotRun.ConsumeStoryAttempt"/> reached <see cref="AutopilotRun.MaxStoryAttempts"/>. The row it
/// reads is the one of the attempt the run is on — <c>…:story</c> while <see cref="AutopilotRun.Attempt"/> is 1,
/// <c>…:story2</c> after that — because the code of the attempt's failure is what decides between a moderation end
/// and a platform retry. The dispatch is detected by its own idempotency-keyed row, not by the <c>step</c> column,
/// because the column never names a step before it ran: a replay therefore finds the row and waits instead of
/// paying twice.
/// </para>
/// <para>
/// <b>Failure mapping</b> (task file §Failure handling): <c>story_moderated</c> never retries — the slot ends
/// <c>failed</c> with that code (plan §3.6). <c>story_invalid_output</c>, <c>story_generation_failed</c> and a run
/// that ended <c>cancelled</c> (the stale-generation sweeper) are platform failures: while an attempt is left they
/// consume it and go back to this same state, and on the last attempt the run fails with the row's code.
/// <c>spend_cap_reached</c> at dispatch time is also mapped here, because the command says it refused to queue and
/// nothing was spent.
/// </para>
/// <para>
/// <b>Overdue is a log, not a verdict.</b> A story still generating past twice
/// <see cref="AutopilotTimingOptions.StoryEstimateMinutes"/> of the run's last transition is reported for the
/// operations dashboard; failing a stale generation is the sweeper's job (02.2.2), so the step only waits.
/// </para>
/// </remarks>
public sealed partial class GenerateStoryStep(
    IAppDbContext db,
    ISender sender,
    ILogger<GenerateStoryStep> logger) : IAutopilotStepHandler
{
    /// <inheritdoc />
    public AutopilotRunState State => AutopilotRunState.StoryGenerating;

    /// <inheritdoc />
    public async ValueTask<StepResult> HandleAsync(AutopilotStepContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var run = context.Run;

        // The series may have been paused, disabled or archived, or its authorization revoked, since the idea step
        // (task rows SEC-1, PF-5): the regeneration is paid, so nothing is dispatched until the owner resumes.
        var spend = await AutopilotStepSupport.SeriesMaySpendAsync(db, context, ct).ConfigureAwait(false);

        if (spend.Stop is { } refusal)
        {
            LogSeriesHeld(run.Id, spend.Code);

            return refusal;
        }

        if (AutopilotStepSupport.SlotIsMissed(context))
        {
            LogSlotMissed(run.Id, run.SeriesId);

            return AutopilotStepSupport.FailSlotMissed(context);
        }

        if (run.EpisodeId is not { } episodeId)
        {
            return Fail(context, AutopilotStepSupport.EpisodeMissingCode);
        }

        var episode = await db.Episodes
            .FirstOrDefaultAsync(candidate => candidate.Id == episodeId, ct)
            .ConfigureAwait(false);

        if (episode is null)
        {
            return Fail(context, AutopilotStepSupport.EpisodeMissingCode);
        }

        var dispatched = await AutopilotStepSupport
            .FindGenerationRunAsync(db, run, GenerationKind.Story, AttemptStep(run), ct)
            .ConfigureAwait(false);

        if (run.Attempt >= AutopilotRun.MaxStoryAttempts && dispatched is null)
        {
            return await RegenerateAsync(context, episodeId, ct).ConfigureAwait(false);
        }

        return episode.Status switch
        {
            EpisodeStatus.StoryGenerating => Waiting(context, dispatched),

            // The writer finished: the gate decides on the produced version (attempt 2 included).
            EpisodeStatus.StoryReview => Advance(context, AutopilotStepSupport.StoryReadyCode),

            // The story already passed (a replay, or an owner approval that beat the gate): the gate step contains
            // the already-approved branch, so the run still goes through it rather than skipping a decision.
            EpisodeStatus.StoryApproved
                or EpisodeStatus.Rendering
                or EpisodeStatus.VideoReview
                or EpisodeStatus.Ready
                or EpisodeStatus.Scheduled
                or EpisodeStatus.Published => Advance(context, AutopilotStepSupport.StoryReadyCode),

            // Back to draft: the generation of the attempt the run is waiting for failed.
            EpisodeStatus.Draft => Failure(context, dispatched),

            // A cancelled episode has no story this run can gate, and re-requesting one would ignore the owner.
            _ => Fail(context, ErrorCodes.EpisodeInvalidState),
        };
    }

    /// <summary>
    /// The idempotency step of the attempt the run is on (plan §3.5 rows 208–210): attempt 1's story is dispatched by
    /// <see cref="CreateEpisodeStep"/> under <c>…:story</c>, and only the single regeneration uses <c>…:story2</c>.
    /// </summary>
    /// <param name="run">The tracked run.</param>
    /// <returns>The step whose row carries this attempt's outcome.</returns>
    private static string AttemptStep(AutopilotRun run) =>
        run.Attempt >= AutopilotRun.MaxStoryAttempts ? AutopilotRunStep.Story2 : AutopilotRunStep.Story;

    /// <summary>Dispatches the run's single regeneration, the key <c>…:story2</c>, once.</summary>
    private async ValueTask<StepResult> RegenerateAsync(
        AutopilotStepContext context,
        Guid episodeId,
        CancellationToken ct)
    {
        var run = context.Run;

        var requested = await sender
            .Send(
                new GenerateStoryCommand(
                    episodeId,
                    GenerationTrigger.Autopilot,
                    AutopilotIdempotencyKeys.For(run, AutopilotRunStep.Story2)),
                ct)
            .ConfigureAwait(false);

        if (requested.IsError && requested.FirstError.Code != ErrorCodes.StoryGenerationInProgress)
        {
            return Fail(context, requested.FirstError.Code);
        }

        run.AdvanceTo(AutopilotRunState.StoryGenerating, AutopilotRunStep.Story2, context.Now);

        LogRegenerated(run.Id, episodeId, run.Attempt);

        return StepResult.Advanced(AutopilotRunStep.Story2);
    }

    /// <summary>Waits for a generation that is still running, reporting one that outlived its estimate.</summary>
    private StepResult Waiting(AutopilotStepContext context, GenerationRun? dispatched)
    {
        var run = context.Run;

        var overdueAfter = TimeSpan.FromMinutes(2 * (double)context.TimingOptions.StoryEstimateMinutes);

        if (context.Now - run.LastTransitionAt > overdueAfter)
        {
            LogOverdue(run.Id, run.EpisodeId ?? Guid.Empty, dispatched?.Id ?? Guid.Empty);
        }
        else
        {
            LogInFlight(run.Id, run.EpisodeId ?? Guid.Empty);
        }

        return new StepResult(StepResultKind.Idle, AutopilotStepSupport.StoryInFlightCode);
    }

    /// <summary>The generation of this attempt failed: regenerate once, then fail the run with the row's code.</summary>
    private StepResult Failure(AutopilotStepContext context, GenerationRun? dispatched)
    {
        var run = context.Run;
        var code = dispatched?.ErrorCode;

        if (dispatched is { Status: GenerationRunStatus.Queued, PauseReason: { } pause })
        {
            // The row names the hold, so the run records that hold and not a fixed one (S71 CR-7): a
            // `spend_cap_reached` row is not a provider-funds outage, and only a hold this ladder knows becomes a
            // pause — an unmapped code fails closed with the row's own code rather than pausing for an unknown reason.
            if (HeldReason(pause) is not { } reason)
            {
                return Fail(context, code ?? ErrorCodes.StoryGenerationFailed);
            }

            run.Pause(reason, context.Now);

            var wire = AutopilotStepSupport.PauseReasonWire(reason);

            LogHeld(run.Id, wire);

            return StepResult.Stopped(wire);
        }

        // Moderation is never retried in the same slot (plan §3.6): the second attempt would buy another flag.
        if (code == ErrorCodes.StoryModerated)
        {
            return Fail(context, code);
        }

        if (run.Attempt < AutopilotRun.MaxStoryAttempts)
        {
            // The next tick sees attempt 2 and no `…:story2` row and dispatches the regeneration.
            run.ConsumeStoryAttempt();
            run.AdvanceTo(AutopilotRunState.StoryGenerating, step: null, context.Now);

            LogAttemptSpent(run.Id, run.Attempt, code ?? AutopilotStepSupport.StoryOutcomeUnknownCode);

            return StepResult.Advanced(AutopilotRunStep.Story);
        }

        return Fail(context, code ?? ErrorCodes.StoryGenerationFailed);
    }

    /// <summary>
    /// The plan §3.6 reason a held generation row stands for: our own spend cap is row 2, the provider account's funds
    /// row 5. Anything else is not a §3.6 trigger this build knows, so the caller fails closed instead of inventing a
    /// pause (S71 CR-7).
    /// </summary>
    /// <param name="pause">The hold the queued <c>generation_runs</c> row carries.</param>
    /// <returns>The pause reason to record on the run, or <see langword="null"/> for a hold nothing maps.</returns>
    private static AutopilotPauseReason? HeldReason(GenerationPauseReason pause) => pause switch
    {
        GenerationPauseReason.SpendCapReached => AutopilotPauseReason.SpendCapReached,
        GenerationPauseReason.ProviderFundsExhausted => AutopilotPauseReason.ProviderFundsExhausted,
        _ => null,
    };

    /// <summary>The story landed: the gate decides on it.</summary>
    private StepResult Advance(AutopilotStepContext context, string code)
    {
        context.Run.AdvanceTo(AutopilotRunState.StoryGate, AutopilotRunStep.StoryGate, context.Now);

        LogStoryReady(context.Run.Id, context.Run.EpisodeId ?? Guid.Empty);

        return new StepResult(StepResultKind.Advanced, code);
    }

    /// <summary>Fails the run with <paramref name="code"/>; the slot has nothing left to produce.</summary>
    private StepResult Fail(AutopilotStepContext context, string code)
    {
        context.Run.Fail(code, context.Now);

        LogFailed(context.Run.Id, code);

        return StepResult.Stopped(code);
    }

    [LoggerMessage(
        EventId = 6647,
        Level = LogLevel.Warning,
        Message = "autopilot_story_series_held runId={RunId} code={Code}")]
    private partial void LogSeriesHeld(Guid runId, string code);

    [LoggerMessage(
        EventId = 6582,
        Level = LogLevel.Warning,
        Message = "autopilot_story_slot_missed runId={RunId} seriesId={SeriesId}")]
    private partial void LogSlotMissed(Guid runId, Guid seriesId);

    [LoggerMessage(
        EventId = 6583,
        Level = LogLevel.Information,
        Message = "autopilot_story_regenerated runId={RunId} episodeId={EpisodeId} attempt={Attempt}")]
    private partial void LogRegenerated(Guid runId, Guid episodeId, int attempt);

    [LoggerMessage(
        EventId = 6584,
        Level = LogLevel.Debug,
        Message = "autopilot_story_in_flight runId={RunId} episodeId={EpisodeId}")]
    private partial void LogInFlight(Guid runId, Guid episodeId);

    [LoggerMessage(
        EventId = 6585,
        Level = LogLevel.Warning,
        Message = "autopilot_story_overdue runId={RunId} episodeId={EpisodeId} generationRunId={GenerationRunId}")]
    private partial void LogOverdue(Guid runId, Guid episodeId, Guid generationRunId);

    [LoggerMessage(
        EventId = 6586,
        Level = LogLevel.Information,
        Message = "autopilot_story_attempt_spent runId={RunId} attempt={Attempt} code={Code}")]
    private partial void LogAttemptSpent(Guid runId, int attempt, string code);

    [LoggerMessage(
        EventId = 6587,
        Level = LogLevel.Information,
        Message = "autopilot_story_ready runId={RunId} episodeId={EpisodeId}")]
    private partial void LogStoryReady(Guid runId, Guid episodeId);

    [LoggerMessage(
        EventId = 6588,
        Level = LogLevel.Warning,
        Message = "autopilot_story_failed runId={RunId} code={Code}")]
    private partial void LogFailed(Guid runId, string code);

    [LoggerMessage(
        EventId = 6589,
        Level = LogLevel.Warning,
        Message = "autopilot_story_held runId={RunId} reason={Reason}")]
    private partial void LogHeld(Guid runId, string reason);
}
