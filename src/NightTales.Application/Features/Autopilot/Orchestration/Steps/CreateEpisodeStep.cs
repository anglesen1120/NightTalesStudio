using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Application.Features.Episodes.Commands.CreateEpisode;
using NightTales.Application.Features.Stories.Commands.GenerateStory;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Application.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// <see cref="AutopilotRunState.IdeaPicked"/>: turn the picked idea into the episode, then either dispatch the story
/// (fiction) or hand the run to research (true crime). Plan §3.5 rows <c>idea_picked → story_generating</c> (key
/// <c>…:story</c>) and <c>idea_picked → researching</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this step owns.</b> <see cref="CreateEpisodeCommand"/> decides the episode row (its owner, its series and
/// its audit row) and <see cref="IIdeaPlanner.MarkUsedAsync"/> is the one transition of an idea an autopilot run may
/// drive, so both happen here and nowhere else. The fiction branch then sends the same
/// <see cref="GenerateStoryCommand"/> an owner sends — with <see cref="GenerationTrigger.Autopilot"/> and the run's
/// <c>…:story</c> key — and spends story attempt 1 through
/// <see cref="AutopilotRun.ConsumeStoryAttempt"/>; a true-crime run (F33) advances to
/// <see cref="AutopilotRunState.Researching"/> with the <c>step</c> column still at <c>idea</c>, because the research
/// steps (<c>find_sources</c>, <c>fact_sheet</c>, <c>sources_required</c>) are the next handler's to complete.
/// </para>
/// <para>
/// <b>Why nothing after the episode returns Retry.</b> A command the step already sent cannot be taken back by
/// answering <see cref="StepResultKind.Retry"/> — the orchestrator saves nothing for Idle/Retry, so the scope dies
/// with the episode row committed and the next tick would create a second one. Every error after the episode exists
/// is therefore a terminal answer for the slot: the run fails with the command's code, the episode stays a draft the
/// owner can finish by hand, and the log line names both ids.
/// </para>
/// <para>
/// <b>Replay.</b> The state leaves <see cref="AutopilotRunState.IdeaPicked"/> in the same save that carries the
/// episode id, so a second entry finds <see cref="AutopilotRun.EpisodeId"/> set and does not create anything: it
/// re-sends the idempotent story command (same key, same answer) or returns the true-crime route unchanged.
/// </para>
/// </remarks>
public sealed partial class CreateEpisodeStep(
    IAppDbContext db,
    ISender sender,
    IIdeaPlanner planner,
    ILogger<CreateEpisodeStep> logger) : IAutopilotStepHandler
{
    /// <inheritdoc />
    public AutopilotRunState State => AutopilotRunState.IdeaPicked;

    /// <inheritdoc />
    public async ValueTask<StepResult> HandleAsync(AutopilotStepContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var run = context.Run;

        // The series, its autopilot and its authorization are re-read before this state spends anything (task rows
        // SEC-1, PF-5): the episode's story is paid, and an archived, switched-off, paused or unauthorized series must
        // not buy one.
        var spend = await AutopilotStepSupport.SeriesMaySpendAsync(db, context, ct).ConfigureAwait(false);

        if (spend.Stop is { } refusal)
        {
            LogSeriesHeld(run.Id, spend.Code);

            return refusal;
        }

        // The slot's story must be requested while the slot is still publishable (plan §3.4).
        if (AutopilotStepSupport.SlotIsMissed(context))
        {
            LogSlotMissed(run.Id, run.SeriesId);

            return AutopilotStepSupport.FailSlotMissed(context);
        }

        if (run.EpisodeId is not { } episodeId)
        {
            var created = await CreateEpisodeAsync(context, ct).ConfigureAwait(false);

            if (created.IsError)
            {
                // FailStep already failed the run; the step only has to report the same code as its stop.
                return StepResult.Stopped(created.FirstError.Code);
            }

            episodeId = created.Value;
        }

        return context.Series.Topic.IsFiction()
            ? await RequestStoryAsync(context, episodeId, ct).ConfigureAwait(false)
            : RouteToResearch(context, episodeId);
    }

    /// <summary>Creates the episode from the run's idea and marks the idea used.</summary>
    private async ValueTask<ErrorOr<Guid>> CreateEpisodeAsync(
        AutopilotStepContext context,
        CancellationToken ct)
    {
        var run = context.Run;

        if (run.IdeaId is not { } ideaId)
        {
            return FailStep(context, AutopilotStepSupport.IdeaMissingCode);
        }

        var idea = await db.EpisodeIdeas
            .FirstOrDefaultAsync(candidate => candidate.Id == ideaId && candidate.SeriesId == run.SeriesId, ct)
            .ConfigureAwait(false);

        if (idea is null)
        {
            return FailStep(context, AutopilotStepSupport.IdeaMissingCode);
        }

        var created = await sender
            .Send(
                new CreateEpisodeCommand(
                    run.SeriesId,
                    idea.Title,
                    ComposeBrief(idea.Logline, idea.Hook, idea.Setting),
                    LengthTier.Standard),
                ct)
            .ConfigureAwait(false);

        if (created.IsError)
        {
            return FailStep(context, created.FirstError.Code);
        }

        var episodeId = created.Value;

        // The idea becomes used only once its episode exists; a conflict here means another run already took it,
        // which is a data defect rather than a user answer, so the slot fails with the code the command returned.
        var used = await planner.MarkUsedAsync(idea.Id, episodeId, ct).ConfigureAwait(false);

        if (used.IsError)
        {
            LogMarkUsedFailed(run.Id, idea.Id, episodeId, used.FirstError.Code);

            return FailStep(context, used.FirstError.Code);
        }

        run.AttachIdea(idea.Id);
        run.AttachEpisode(episodeId);

        LogEpisodeCreated(run.Id, episodeId);

        return episodeId;
    }

    /// <summary>The fiction branch: spend story attempt 1 and dispatch the first generation.</summary>
    private async ValueTask<StepResult> RequestStoryAsync(
        AutopilotStepContext context,
        Guid episodeId,
        CancellationToken ct)
    {
        var run = context.Run;

        // The run's attempt is the number this dispatch spends; a replay of the state never reaches the command
        // because the state itself left in the same save, and the command's key makes a redelivery a no-op anyway.
        if (run.Attempt == 0)
        {
            run.ConsumeStoryAttempt();
        }

        var requested = await sender
            .Send(
                new GenerateStoryCommand(
                    episodeId,
                    GenerationTrigger.Autopilot,
                    AutopilotIdempotencyKeys.For(run, AutopilotRunStep.Story)),
                ct)
            .ConfigureAwait(false);

        if (requested.IsError)
        {
            // A generation already in flight for this episode (a redelivered command, or the owner's own request)
            // is the same fact as a fresh dispatch: the run waits for that story like any other.
            if (requested.FirstError.Code != ErrorCodes.StoryGenerationInProgress)
            {
                FailStep(context, requested.FirstError.Code);

                return StepResult.Stopped(requested.FirstError.Code);
            }

            LogStoryInProgress(run.Id, episodeId);
        }

        run.AdvanceTo(AutopilotRunState.StoryGenerating, AutopilotRunStep.Story, context.Now);

        LogStoryRequested(run.Id, episodeId, run.Attempt);

        return StepResult.Advanced(AutopilotRunStep.Story);
    }

    /// <summary>The true-crime branch (F33): the research steps take the run from here.</summary>
    private StepResult RouteToResearch(AutopilotStepContext context, Guid episodeId)
    {
        var run = context.Run;

        // `idea` stays the last completed step: F33's source search and fact sheet are the research handler's
        // steps, and writing `find_sources` here would make them look done.
        run.AdvanceTo(AutopilotRunState.Researching, AutopilotRunStep.Idea, context.Now);

        LogResearching(run.Id, episodeId);

        return StepResult.Advanced(AutopilotRunStep.Idea);
    }

    /// <summary>
    /// The brief of the episode an idea becomes: premise, hook and setting, in that order, one paragraph each.
    /// </summary>
    internal static string ComposeBrief(string logline, string hook, string setting) =>
        string.Join("\n\n", logline, hook, setting);

    /// <summary>Fails the run with <paramref name="code"/> and answers the step with the same code.</summary>
    private Error FailStep(AutopilotStepContext context, string code)
    {
        context.Run.Fail(code, context.Now);

        LogFailed(context.Run.Id, code);

        return Error.Failure(code, code);
    }

    [LoggerMessage(
        EventId = 6648,
        Level = LogLevel.Warning,
        Message = "autopilot_episode_series_held runId={RunId} code={Code}")]
    private partial void LogSeriesHeld(Guid runId, string code);

    [LoggerMessage(
        EventId = 6551,
        Level = LogLevel.Warning,
        Message = "autopilot_episode_slot_missed runId={RunId} seriesId={SeriesId}")]
    private partial void LogSlotMissed(Guid runId, Guid seriesId);

    [LoggerMessage(
        EventId = 6552,
        Level = LogLevel.Information,
        Message = "autopilot_episode_created runId={RunId} episodeId={EpisodeId}")]
    private partial void LogEpisodeCreated(Guid runId, Guid episodeId);

    [LoggerMessage(
        EventId = 6553,
        Level = LogLevel.Information,
        Message = "autopilot_story_requested runId={RunId} episodeId={EpisodeId} attempt={Attempt}")]
    private partial void LogStoryRequested(Guid runId, Guid episodeId, int attempt);

    [LoggerMessage(
        EventId = 6554,
        Level = LogLevel.Information,
        Message = "autopilot_story_already_generating runId={RunId} episodeId={EpisodeId}")]
    private partial void LogStoryInProgress(Guid runId, Guid episodeId);

    [LoggerMessage(
        EventId = 6555,
        Level = LogLevel.Information,
        Message = "autopilot_researching runId={RunId} episodeId={EpisodeId}")]
    private partial void LogResearching(Guid runId, Guid episodeId);

    [LoggerMessage(
        EventId = 6556,
        Level = LogLevel.Warning,
        Message = "autopilot_episode_step_failed runId={RunId} code={Code}")]
    private partial void LogFailed(Guid runId, string code);

    [LoggerMessage(
        EventId = 6557,
        Level = LogLevel.Warning,
        Message = "autopilot_idea_mark_used_failed runId={RunId} ideaId={IdeaId} episodeId={EpisodeId} code={Code}")]
    private partial void LogMarkUsedFailed(Guid runId, Guid ideaId, Guid episodeId, string code);
}
