using Mediator;
using Microsoft.Extensions.Logging;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Rendering.Commands.RenderEpisode;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// The <c>rendering</c> step (02.8.3 plan §3.5 rows <c>rendering → video_gate</c> and <c>rendering → failed</c>,
/// §7.2): dispatches the run's <b>one</b> render as the owner would, and reads the episode's own state as the
/// render's signal so a lost outbox message still advances the run on the next tick.
/// </summary>
/// <remarks>
/// <para>
/// <b>One render, never two.</b> The render is sent only while <see cref="AutopilotRun.RenderAttempts"/> is 0, with
/// the run's idempotency key <c>…:render</c> (<see cref="AutopilotIdempotencyKeys"/>), so a duplicated tick or a
/// Worker restarted mid-step replays the command's own answer instead of queueing a second job. The attempt is
/// consumed <em>after</em> the command is accepted and never before: a transient refusal
/// (<c>service_unavailable</c>, a price catalogue that is momentarily empty) must leave the run free to retry the
/// same render. The cap counts accepted renders — consuming before a refusal would strand the run in
/// <c>rendering</c> with no job to wait for.
/// </para>
/// <para>
/// <b>Never a second paid call after a crash.</b> If the command was accepted but the row's save was lost, the next
/// tick re-sends the same key: the idempotency store replays the first acceptance, or — when the episode has
/// already moved to <c>rendering</c>/<c>video_review</c> — the command answers <c>episode_invalid_state</c> and
/// <see cref="AdoptOrFail"/> adopts the in-flight render instead. Either way the attempt is consumed and the run
/// continues, without a second render job.
/// </para>
/// <para>
/// <b>Errors.</b> A lint refusal (<c>keywords_flagged</c>, 03.2.2-T2) happens before a job exists, but plan §3.5
/// gives <c>rendering</c> only <c>video_gate</c> and <c>failed</c>, so the run ends <c>failed</c> with the
/// diagnostic code <c>render_keywords_flagged</c> (open item for the TL: a <c>rendering → waiting_owner</c> row
/// would keep the slot recoverable inside the run). A spend-cap refusal ends the run <c>failed</c>
/// (<c>spend_cap_reached</c>, plan §3.6 row 2 — 02.8.3-T4 pauses the series). Every other refusal ends it with the
/// command's own code (plan §7.3). A missed slot outranks all of this: once
/// <c>now</c> passed <see cref="AutopilotTiming.SlotMissedCutoff"/> the run ends with
/// <c>pause_reason = slot_missed</c> whatever the render does.
/// </para>
/// <para>
/// <b>Does not move the run to <c>rendering</c>.</b> The transition in is the story gate's step
/// (<c>story_gate → rendering</c>); this step moves the run out, and writing the step name while staying in
/// <c>rendering</c> is impossible by design — the state machine has no self-transition. The persisted
/// <see cref="AutopilotRun.RenderAttempts"/> is the evidence a render was dispatched (<see cref="AutopilotRun.ConsumeRenderAttempt"/>).
/// </para>
/// </remarks>
public sealed partial class RenderStep : IAutopilotStepHandler
{
    private readonly ISender _sender;
    private readonly IAutopilotStepReader _reader;
    private readonly ILogger<RenderStep> _logger;

    /// <summary>Builds the step over the reader port; the only ctor, so dependency injection has no tie.</summary>
    /// <param name="sender">The mediator the render command is sent through.</param>
    /// <param name="reader">The episode read the step decides on.</param>
    /// <param name="logger">The ids-only log of this step.</param>
    public RenderStep(ISender sender, IAutopilotStepReader reader, ILogger<RenderStep> logger)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(logger);

        _sender = sender;
        _reader = reader;
        _logger = logger;
    }

    /// <inheritdoc />
    public AutopilotRunState State => AutopilotRunState.Rendering;

    /// <inheritdoc />
    public async ValueTask<StepResult> HandleAsync(AutopilotStepContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var run = context.Run;
        var now = context.Now;

        if (run.EpisodeId is not { } episodeId)
        {
            // A run in `rendering` carries its episode (the story gate attached it); without one there is nothing
            // to render and no row to wait for, so the tick retries and the orchestrator's stuck-run guard reports it.
            LogEpisodeMissing(run.Id);

            return StepResult.Retry(AutopilotStepCodes.EpisodeMissing);
        }

        if (now >= AutopilotTiming.SlotMissedCutoff(context.Timing, context.TimingOptions))
        {
            run.Fail(AutopilotRun.SlotMissedReason, now);
            LogSlotMissed(run.Id, episodeId);

            return StepResult.Stopped(AutopilotStepCodes.SlotMissed);
        }

        var episode = await _reader.FindEpisodeAsync(episodeId, ct).ConfigureAwait(false);
        if (episode is null)
        {
            LogEpisodeMissing(run.Id);

            return StepResult.Retry(AutopilotStepCodes.EpisodeMissing);
        }

        return run.RenderAttempts >= AutopilotRun.MaxRenderAttempts
            ? Observe(run, episode, now)
            : await DispatchAsync(run, episode, now, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the run's render, or adopts one that a previous tick already dispatched (plan §7.2's time-based
    /// fallback: the episode's state is the signal, so the step never depends on <c>render_succeeded.v1</c>).
    /// </summary>
    private async ValueTask<StepResult> DispatchAsync(
        AutopilotRun run,
        AutopilotRenderEpisode episode,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (episode.Status != EpisodeStatus.StoryApproved)
        {
            return AdoptOrFail(run, episode, now);
        }

        // The If-Match the command requires (02 D9) is the tag of the episode as read here: the render reserves
        // credit against the story the caller saw, and a stale tag must be refused rather than rendered.
        var accepted = await _sender
            .Send(
                new RenderEpisodeCommand(
                    episode.Id,
                    ETags.Episode(episode.Version),
                    AutopilotIdempotencyKeys.For(run, AutopilotRunStep.Render)),
                ct)
            .ConfigureAwait(false);

        if (!accepted.IsError)
        {
            run.ConsumeRenderAttempt();
            LogDispatched(run.Id, episode.Id);

            return StepResult.Advanced(AutopilotRunStep.Render);
        }

        var error = accepted.FirstError;
        LogDispatchRefused(run.Id, error.Code);

        return error.Code switch
        {
            // 03.2.2-T2: the keyword lint refused the render before a job existed. §3.5 gives `rendering` only
            // `video_gate` and `failed`, so the run ends failed with a diagnostic code; the owner can still fix
            // the story and render the episode by hand (TL open item: a `rendering → waiting_owner` row would keep
            // the slot recoverable inside the run).
            ErrorCodes.KeywordsFlagged => Fail(run, AutopilotStepCodes.RenderKeywordsFlagged, now),

            // Transient by contract; the attempt is not consumed, so the same render is retried next tick.
            ErrorCodes.ServiceUnavailable => StepResult.Retry(error.Code),

            // The episode moved under this tick (a concurrent render, an approval, a reopen): adopt what it says.
            ErrorCodes.EpisodeInvalidState or ErrorCodes.EpisodeConflict =>
                await ReReadAndAdoptAsync(run, episode.Id, now, error.Code, ct).ConfigureAwait(false),

            // Plan §3.6 row 2: the run ends and 02.8.3-T4 pauses the series.
            _ => Fail(run, error.Code, now),
        };
    }

    /// <summary>The state the episode reports after the run's render was dispatched.</summary>
    private StepResult Observe(AutopilotRun run, AutopilotRenderEpisode episode, DateTimeOffset now)
    {
        switch (episode.Status)
        {
            case EpisodeStatus.VideoReview:
            case EpisodeStatus.Ready:
                run.AdvanceTo(AutopilotRunState.VideoGate, AutopilotRunStep.Render, now);

                return StepResult.Advanced(AutopilotRunStep.Render);

            case EpisodeStatus.Rendering:
                // In flight: `RenderSucceeded`/`RenderFailed` or the next tick's read decides.
                return StepResult.Idle;

            case EpisodeStatus.StoryApproved:
            case EpisodeStatus.Failed:
                // A failed render returns the episode to `story_approved` (Episode.RenderFailed); the run never
                // renders a second cut (plan §3.5).
                run.Fail(AutopilotStepCodes.RenderFailed, now);
                LogRenderFailed(run.Id, episode.Id);

                return StepResult.Stopped(AutopilotStepCodes.RenderFailed);

            default:
                // Scheduled/Published/Cancelled/Draft/StoryGenerating/StoryReview: the run and the episode
                // disagree and no render can happen from there.
                run.Fail(AutopilotStepCodes.EpisodeInvalidState, now);

                return StepResult.Stopped(AutopilotStepCodes.EpisodeInvalidState);
        }
    }

    /// <summary>
    /// Re-reads the episode after a refusal and adopts an in-flight or finished render; anything else ends the run
    /// with the refusal's code.
    /// </summary>
    private async ValueTask<StepResult> ReReadAndAdoptAsync(
        AutopilotRun run,
        Guid episodeId,
        DateTimeOffset now,
        string code,
        CancellationToken ct)
    {
        var current = await _reader.FindEpisodeAsync(episodeId, ct).ConfigureAwait(false);

        return current is null
            ? StepResult.Retry(AutopilotStepCodes.EpisodeMissing)
            : AdoptOrFail(run, current, now, code);
    }

    /// <summary>
    /// Adopts the render a previous call already started (<c>rendering</c>, <c>video_review</c> or <c>ready</c>),
    /// consuming the attempt it stands for; every other state ends the run with <paramref name="code"/>.
    /// </summary>
    private static StepResult AdoptOrFail(
        AutopilotRun run,
        AutopilotRenderEpisode episode,
        DateTimeOffset now,
        string? code = null)
    {
        switch (episode.Status)
        {
            case EpisodeStatus.Rendering:
                if (run.RenderAttempts < AutopilotRun.MaxRenderAttempts)
                {
                    run.ConsumeRenderAttempt();

                    return StepResult.Advanced(AutopilotRunStep.Render);
                }

                return StepResult.Idle;

            case EpisodeStatus.VideoReview:
            case EpisodeStatus.Ready:
                if (run.RenderAttempts < AutopilotRun.MaxRenderAttempts)
                {
                    run.ConsumeRenderAttempt();
                }

                run.AdvanceTo(AutopilotRunState.VideoGate, AutopilotRunStep.Render, now);

                return StepResult.Advanced(AutopilotRunStep.Render);

            default:
                return Fail(run, code ?? AutopilotStepCodes.EpisodeInvalidState, now);
        }
    }

    /// <summary>Ends the run with <paramref name="code"/> (a no-op when it already ended).</summary>
    private static StepResult Fail(AutopilotRun run, string code, DateTimeOffset now)
    {
        run.Fail(code, now);

        return StepResult.Stopped(code);
    }

    [LoggerMessage(
        EventId = 6560,
        Level = LogLevel.Information,
        Message = "Autopilot run {RunId} dispatched its render for episode {EpisodeId}.")]
    private partial void LogDispatched(Guid runId, Guid episodeId);

    [LoggerMessage(
        EventId = 6561,
        Level = LogLevel.Warning,
        Message = "The render of autopilot run {RunId} was refused with {ErrorCode}.")]
    private partial void LogDispatchRefused(Guid runId, string errorCode);

    [LoggerMessage(
        EventId = 6562,
        Level = LogLevel.Information,
        Message = "Autopilot run {RunId} found no episode; the render step retries.")]
    private partial void LogEpisodeMissing(Guid runId);

    [LoggerMessage(
        EventId = 6563,
        Level = LogLevel.Warning,
        Message = "Autopilot run {RunId} ended failed (render_failed): episode {EpisodeId} is back from a failed render.")]
    private partial void LogRenderFailed(Guid runId, Guid episodeId);

    [LoggerMessage(
        EventId = 6564,
        Level = LogLevel.Warning,
        Message = "Autopilot run {RunId} missed its slot while rendering episode {EpisodeId}; the run ends slot_missed.")]
    private partial void LogSlotMissed(Guid runId, Guid episodeId);
}
