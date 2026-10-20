using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Rendering.Commands.RenderEpisode;
using NightTales.Application.Features.Research.Commands.GenerateFactSheet;
using NightTales.Application.Features.Stories.Commands.GenerateStory;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Application.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// The owner wait of an autopilot run (02.8.3 plan §3.5, column <c>waiting_for</c>): it expires the wait, detects
/// the owner's answer by reading the episode, and continues the run along the one edge the answer opens.
/// </summary>
/// <remarks>
/// <para>
/// <b>The routing table</b> — <see cref="WaitingFor"/> → what "answered" means → where the run goes:
/// </para>
/// <list type="table">
/// <item>
/// <term><see cref="WaitingFor.Sources"/></term>
/// <description>
/// a fetched source exists. With an approved sheet the run continues into <c>story_generating</c>; otherwise it
/// drafts the sheet itself (<c>GenerateFactSheetCommand</c>, key <c>…:fact_sheet</c>) and re-labels the wait
/// <see cref="WaitingFor.FactSheet"/>. The state stays <c>waiting_owner</c>: the plan §3.5 table has no
/// <c>waiting_owner → researching</c> edge, and the transition engine refuses one, so the research direction is
/// expressed by the <c>waiting_for</c> column instead (deviation recorded for 02.8.3-T2).
/// </description>
/// </item>
/// <item>
/// <term><see cref="WaitingFor.FactSheet"/></term>
/// <description>
/// an approved sheet exists → <c>story_generating</c> with a <c>GenerateStoryCommand</c>
/// (<c>Trigger=Autopilot</c>, key <c>…:story</c>) and one story attempt consumed (plan §3.5 row 206).
/// </description>
/// </item>
/// <item>
/// <term><see cref="WaitingFor.Story"/></term>
/// <description>
/// the episode is <see cref="EpisodeStatus.StoryApproved"/> → <c>rendering</c> with a
/// <c>RenderEpisodeCommand</c> (key <c>…:render</c>) and the run's single render attempt consumed (row 215).
/// </description>
/// </item>
/// <item>
/// <term><see cref="WaitingFor.Video"/></term>
/// <description>
/// the episode is <see cref="EpisodeStatus.VideoReview"/> or <see cref="EpisodeStatus.Ready"/> →
/// <see cref="AutopilotPublicationCreator.TryCreateAsync"/>, which owns the timing guard, the publication command and
/// the move to <c>publication_created</c> (row 222). An episode back in <see cref="EpisodeStatus.StoryApproved"/>
/// (the owner regenerated the story while the wait was on the video, so no video exists to publish) → back to
/// <c>rendering</c> with the wait cleared, so the next tick renders the new story instead of publishing nothing.
/// </description>
/// </item>
/// </list>
/// <para>
/// <b>Expiry first</b> (plan §3.6/task row): once the wait is older than
/// <see cref="AutopilotRunOptions.WaitingOwnerExpiryHours"/> the run ends <c>failed</c> with
/// <c>owner_approval_expired</c>; the episode stays in its review state for the owner to finish by hand, and the
/// series is not paused — a true-crime series legitimately waits. The reason travels in the result code only: the
/// <c>autopilot_runs</c> row has no column for a failed run's reason (plan §3.3, decision 4).
/// </para>
/// <para>
/// <b>Nothing else is attempted.</b> A wait whose answer has not arrived answers <see cref="StepResult.Idle"/>, so
/// the tick leaves the row alone; an orchestrator save only happens for the two paths that change a column.
/// </para>
/// </remarks>
/// <param name="sender">The in-process mediator, already inside the run owner's scope.</param>
/// <param name="db">The tenant-filtered write model of the run's owner.</param>
/// <param name="options">The <c>Autopilot:Run</c> settings (the wait's expiry).</param>
/// <param name="publicationCreator">The publication half of the video answer (owned by the render/publish slice).</param>
public sealed class WaitingOwnerStep(
    ISender sender,
    IAppDbContext db,
    AutopilotRunOptions options,
    AutopilotPublicationCreator publicationCreator) : IAutopilotStepHandler
{
    /// <summary>The code a wait that outlived its window ends with (plan §3.5, §3.6).</summary>
    public const string OwnerApprovalExpiredCode = "owner_approval_expired";

    /// <summary>The code of a run whose episode is gone; the run cannot continue any more.</summary>
    public const string EpisodeMissingCode = "episode_missing";

    /// <inheritdoc />
    public AutopilotRunState State => AutopilotRunState.WaitingOwner;

    /// <inheritdoc />
    public async ValueTask<StepResult> HandleAsync(AutopilotStepContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var run = context.Run;

        // 1. Expiry first: an owner who never answers must not hold the row (or the series) for ever.
        if (context.Now - run.LastTransitionAt >= TimeSpan.FromHours(options.WaitingOwnerExpiryHours))
        {
            run.Fail(OwnerApprovalExpiredCode, context.Now);

            return StepResult.Stopped(OwnerApprovalExpiredCode);
        }

        if (run.EpisodeId is not { } episodeId)
        {
            run.Fail(EpisodeMissingCode, context.Now);

            return StepResult.Stopped(EpisodeMissingCode);
        }

        var episode = await db.Episodes
            .SingleOrDefaultAsync(candidate => candidate.Id == episodeId, ct)
            .ConfigureAwait(false);
        if (episode is null)
        {
            run.Fail(EpisodeMissingCode, context.Now);

            return StepResult.Stopped(EpisodeMissingCode);
        }

        return run.WaitingFor switch
        {
            WaitingFor.Sources => await AnswerSourcesAsync(run, episodeId, context.Now, ct).ConfigureAwait(false),
            WaitingFor.FactSheet => await AnswerFactSheetAsync(run, episodeId, context.Now, ct).ConfigureAwait(false),
            WaitingFor.Story => await AnswerStoryAsync(run, episode, context.Now, ct).ConfigureAwait(false),
            WaitingFor.Video => episode.Status switch
            {
                // A story state while waiting for a video means the video's input changed underneath the wait (the
                // owner regenerated or re-approved the story instead of approving the video). Routing that to the
                // publication creator would publish a video nobody approved, so the run renders the new story first;
                // `WaitingFor` alone cannot tell the two apart, the episode is the signal.
                EpisodeStatus.StoryApproved => AutopilotRunStep.IsBehind(run.Step, AutopilotRunStep.Render)
                    ? ResumeRender(run, context.Now)
                    : StepResult.Idle,
                EpisodeStatus.VideoReview or EpisodeStatus.Ready =>
                    await publicationCreator.TryCreateAsync(context, ct).ConfigureAwait(false),
                _ => StepResult.Idle,
            },
            _ => StepResult.Idle,
        };
    }

    /// <summary>
    /// The F33 owner ask: the owner pasted sources (or generated and approved a sheet by hand), so the run drafts
    /// the sheet or, when one is already approved, continues into the story.
    /// </summary>
    private async ValueTask<StepResult> AnswerSourcesAsync(
        AutopilotRun run,
        Guid episodeId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var hasSheet = await HasApprovedSheetAsync(episodeId, ct).ConfigureAwait(false);
        if (hasSheet)
        {
            return await StartStoryAsync(run, episodeId, now, ct).ConfigureAwait(false);
        }

        if (!await HasFetchedSourceAsync(episodeId, ct).ConfigureAwait(false))
        {
            return StepResult.Idle;
        }

        var drafted = await sender
            .Send(
                new GenerateFactSheetCommand(
                    episodeId,
                    AutopilotIdempotencyKeys.For(run, AutopilotRunStep.FactSheet)),
                ct)
            .ConfigureAwait(false);
        if (drafted.IsError)
        {
            return StepResult.Retry(drafted.FirstError.Code);
        }

        // The run stays waiting_owner; the ask it waits on is now the sheet's approval, not the sources. The 48 h
        // window keeps counting from the entry into waiting_owner (there is no column for the ask's own start).
        run.SetWaitingFor(WaitingFor.FactSheet);

        return StepResult.Advanced(AutopilotRunStep.FactSheet);
    }

    /// <summary>
    /// The story changed while the run waited for a video: the run goes back to <c>rendering</c>, so the next tick
    /// re-dispatches the render against the new story. The wait is cleared first — <c>waiting_owner</c> is the only
    /// state that owns that column. The caller only resumes a run whose step is still behind the render: the aggregate
    /// has no way to rewind <c>step</c> (<c>AdvanceTo(step: null)</c> keeps the stored value), and a run already at
    /// or past <c>render</c> would come back here for ever.
    /// </summary>
    /// <param name="run">The run whose wait is over.</param>
    /// <param name="now">The tick's clock reading.</param>
    /// <returns>The advanced result; the orchestrator saves the two columns.</returns>
    private static StepResult ResumeRender(AutopilotRun run, DateTimeOffset now)
    {
        run.SetWaitingFor(null);
        run.AdvanceTo(AutopilotRunState.Rendering, step: null, now);

        return StepResult.Advanced(AutopilotRunStep.Render);
    }

    /// <summary>The owner approved the fact sheet: the story generation is the run's next paid step.</summary>
    private async ValueTask<StepResult> AnswerFactSheetAsync(
        AutopilotRun run,
        Guid episodeId,
        DateTimeOffset now,
        CancellationToken ct) =>
        await HasApprovedSheetAsync(episodeId, ct).ConfigureAwait(false)
            ? await StartStoryAsync(run, episodeId, now, ct).ConfigureAwait(false)
            : StepResult.Idle;

    /// <summary>Sends the story generation and moves the run to <c>story_generating</c> (plan §3.5 row 206).</summary>
    private async ValueTask<StepResult> StartStoryAsync(
        AutopilotRun run,
        Guid episodeId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (!AutopilotRunStep.IsBehind(run.Step, AutopilotRunStep.Story))
        {
            return StepResult.Idle;
        }

        var story = await sender
            .Send(
                new GenerateStoryCommand(
                    episodeId,
                    Trigger: GenerationTrigger.Autopilot,
                    IdempotencyKey: AutopilotIdempotencyKeys.For(run, AutopilotRunStep.Story)),
                ct)
            .ConfigureAwait(false);
        if (story.IsError)
        {
            // Nothing is written: the command's own guard (an in-flight run, an unapproved sheet, the spend cap)
            // is a wait or a refusal the next tick re-evaluates, never a silent approval.
            return StepResult.Retry(story.FirstError.Code);
        }

        run.ConsumeStoryAttempt();
        run.AdvanceTo(AutopilotRunState.StoryGenerating, AutopilotRunStep.Story, now);

        return StepResult.Advanced(AutopilotRunStep.Story);
    }

    /// <summary>Sends the run's single render and moves it to <c>rendering</c> (plan §3.5 row 215).</summary>
    private async ValueTask<StepResult> AnswerStoryAsync(
        AutopilotRun run,
        Episode episode,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (episode.Status != EpisodeStatus.StoryApproved
            || !AutopilotRunStep.IsBehind(run.Step, AutopilotRunStep.Render))
        {
            return StepResult.Idle;
        }

        // The approve bumped the episode's xmin, so the If-Match is read now, from the row this call loaded: a
        // write landing in between answers 412 and the next tick retries, never a render of a story nobody approved.
        var render = await sender
            .Send(
                new RenderEpisodeCommand(
                    episode.Id,
                    IfMatch: ETags.Episode(episode.Version),
                    IdempotencyKey: AutopilotIdempotencyKeys.For(run, AutopilotRunStep.Render)),
                ct)
            .ConfigureAwait(false);
        if (render.IsError)
        {
            // The keyword lint (03.2.2-T2) refuses the same story on every tick: the state does not change, so a
            // Retry would loop for ever with nothing recorded. The run ends with the same diagnostic code the render
            // step uses (plan §3.5: `rendering → failed`); every other refusal is a wait the next tick re-evaluates.
            if (render.FirstError.Code == ErrorCodes.KeywordsFlagged)
            {
                run.Fail(AutopilotStepCodes.RenderKeywordsFlagged, now);

                return StepResult.Stopped(AutopilotStepCodes.RenderKeywordsFlagged);
            }

            return StepResult.Retry(render.FirstError.Code);
        }

        run.ConsumeRenderAttempt();

        // `step` is the last *completed* step (plan §3.3, AutopilotRun.AdvanceTo): the render step belongs to the
        // success edge `rendering -> video_gate` (RenderStep writes it there), so dispatch keeps the step the run
        // already has. Claiming `render` here would make a §3.6 pause resume into `video_gate` with no video.
        run.AdvanceTo(AutopilotRunState.Rendering, step: null, now);

        return StepResult.Advanced(AutopilotRunStep.Render);
    }

    private Task<bool> HasApprovedSheetAsync(Guid episodeId, CancellationToken ct) =>
        db.FactSheets.AnyAsync(
            sheet => sheet.EpisodeId == episodeId && sheet.Status == FactSheetStatus.Approved,
            ct);

    private Task<bool> HasFetchedSourceAsync(Guid episodeId, CancellationToken ct) =>
        db.StorySources.AnyAsync(
            source => source.EpisodeId == episodeId && source.Status == StorySourceStatus.Fetched,
            ct);
}
