using System.Globalization;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Publishing;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Application.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// <see cref="AutopilotRunState.Planned"/>: authorize, pay for one ideas run, pick the winner and hand the run to
/// <see cref="AutopilotRunState.IdeaPicked"/> (02.8.3 plan §3.5 rows <c>planned → awaiting_authorization</c> and
/// <c>planned → idea_picked</c>, plan §7.2 step 1, task file IDEA-S52-3/IDEA-S52-7/S56-4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> This step is the caller the <see cref="IIdeaPlanner"/> contract asks for: it inserts and
/// commits the <c>generation_runs</c> row (<c>kind = ideas</c>, <c>episode_id = NULL</c>,
/// <c>counts_toward_limit = false</c>, <c>trigger = autopilot</c>, key <c>autopilot:{seriesId}:{slotAt:O}:idea</c>),
/// reserves the credit and prices the bound — and then pays, holding no transaction across the call. The one
/// <see cref="IAppDbContext.SaveChangesAsync"/> in this file is that insert: every other write of the step (the
/// generation run's outcome and the run's own advance) rides the orchestrator's single save, which is what keeps
/// the run's <c>xmin</c> compare-and-set meaningful.
/// </para>
/// <para>
/// <b>Single flight.</b> Before paying, the step reads the row its idempotency key already committed. A row that
/// exists is adopted instead of a second paid call: the run is advanced from a pick the planner persisted before
/// it returned, and a row that is queued or running leaves the step idle — the tick that committed it owns the
/// work. That is the whole of "a replay never pays twice". A crash between the planner's pick and the orchestrator's
/// save is the one case where a queued or running row already owns a pick: adoption then completes the row
/// (<see cref="GenerationRun.Start"/>/<see cref="GenerationRun.Succeed"/>, both no-ops once reached) and settles its
/// reservation, because no job follows an ideas row to do either (CR-8).
/// </para>
/// <para>
/// <b>Settlement is keyed on the reservation.</b> <see cref="ICreditLedger.SettleAsync"/> takes no idempotency key
/// of its own: the cost it settles is the amount <c>usage_records</c> already holds for that reservation, so
/// repeating it writes the same figure and the reservation id is the key. A crash between the settle and the
/// orchestrator's save therefore repeats the same settlement, never a second charge. 12.1.2's ledger must answer
/// success for a repeated settle the way it does for a repeated refund (recorded as an open item on the task).
/// </para>
/// </para>
/// <para>
/// <b>Error mapping</b> (task file §Failure handling, plan §7.3): <c>spend_cap_reached</c> fails the run (the
/// platform refuses the charge; the aggregate records the reason, the owner's notice is 02.8.3-T4's); a
/// <c>provider_funds_exhausted</c> ends the generation row with that code — plan §3.6 row 5's detection source —
/// refunds the reservation and pauses the run with the same reason, which is the hold plan §3.5 gives the story
/// step (<c>story_generating → paused</c>); a Retry here would save nothing and leave a queued row every later tick
/// adopts as in flight, so the slot could never pay again and would idle until <c>slot_missed</c>. The series pause
/// that closes the slot stays 02.8.3-T4's trigger 5. Every ideas error
/// (<c>ideas_no_idea</c> with its <c>variety_*</c> detail, <c>ideas_moderated</c>, <c>ideas_invalid_output</c>,
/// <c>ideas_generation_failed</c>) fails the run with that code and is never retried in the same slot
/// (02.8.4-T1's variety steering reads the recorded outcome). A pricing or budget-readiness error
/// (<c>price_catalog_empty</c>, <c>prompt_not_shipped</c>) is a deployment fault rather than this slot's fault, so
/// it answers Retry and spends nothing.
/// </para>
/// <para>
/// <b>The channel is checked before the first credit.</b> Plan §3.6 rows 6 and 6b refuse a slot whose authorization
/// names no channel, a platform that cannot schedule a publication, or a connection that is not <c>active</c> — and
/// they refuse it <em>here</em>, before the reservation and the paid call, so a connection the owner has to fix
/// costs the slot nothing. The planner still creates the run (the owner may fix the channel before the slot); only
/// the run pauses, because the series pause is 02.8.3-T4's trigger 6.
/// </para>
/// <para>
/// The text the planner returns is never logged (D19): the log lines carry run, series and idea ids and codes only.
/// </para>
/// </remarks>
public sealed partial class PlanIdeaStep(
    IAppDbContext db,
    IAutopilotStepReader reader,
    IIdeaPlanner planner,
    IPriceCatalog prices,
    IIdeaCostEstimator ideaCosts,
    IBudgetGuard budget,
    ICreditLedger ledger,
    ILogger<PlanIdeaStep> logger) : IAutopilotStepHandler
{
    /// <summary>
    /// The refund reason of a reservation whose <c>generation_runs</c> row never committed: the insert failed, so no
    /// call could be bought with it (short and content-free, which is all a refund reason may be).
    /// </summary>
    private const string ReservationNotCommittedReason = "generation_run_not_committed";

    /// <inheritdoc />
    public AutopilotRunState State => AutopilotRunState.Planned;

    /// <inheritdoc />
    public async ValueTask<StepResult> HandleAsync(AutopilotStepContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var run = context.Run;

        // A row that already completed this step is a replay: the plan allows one execution per step and per key.
        if (!AutopilotStepSupport.IsBehind(run, AutopilotRunStep.Idea))
        {
            return StepResult.Idle;
        }

        // The slot's own clock: a run whose start instant is still ahead publishes later, not now (plan §3.4).
        if (context.Now < run.StartAt)
        {
            return StepResult.Idle;
        }

        if (!context.Series.AutopilotEnabled || context.Series.AutopilotPausedAt is not null)
        {
            LogSeriesHeld(run.Id, context.Series.Id);

            return StepResult.Idle;
        }

        if (AutopilotStepSupport.SlotIsMissed(context))
        {
            LogSlotMissed(run.Id, run.SeriesId);

            return AutopilotStepSupport.FailSlotMissed(context);
        }

        var authorizationId = await AutopilotStepSupport
            .CurrentAuthorizationAsync(db, context.Series, ct)
            .ConfigureAwait(false);

        if (authorizationId is null)
        {
            run.AdvanceTo(AutopilotRunState.AwaitingAuthorization, AutopilotRunStep.Authorization, context.Now);
            LogAuthorizationRequired(run.Id, run.SeriesId);

            return StepResult.Stopped(AutopilotStepSupport.AwaitingAuthorizationCode);
        }

        var committed = await AutopilotStepSupport
            .FindGenerationRunAsync(db, run, GenerationKind.Ideas, AutopilotRunStep.Idea, ct)
            .ConfigureAwait(false);

        if (committed is not null)
        {
            return await AdoptAsync(context, committed, authorizationId.Value, ct).ConfigureAwait(false);
        }

        if (await CheckChannelAsync(context, authorizationId.Value, ct).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        return await PayAsync(context, authorizationId.Value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies plan §3.6 rows 6 and 6b to the authorization's channel before anything is reserved: an unreadable or
    /// missing channel, a platform that cannot schedule, and a connection that is not <c>active</c> each pause the
    /// run with the matching reason (the task file's failure table: the run pauses, the series does not — T4 owns
    /// trigger 6).
    /// </summary>
    /// <param name="context">The run, its series and the tick's clock reading.</param>
    /// <param name="authorizationId">The series' current authorization (already resolved, never revoked).</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns><see langword="null"/> when the channel may be published through, the stopped result otherwise.</returns>
    private async ValueTask<StepResult?> CheckChannelAsync(
        AutopilotStepContext context,
        Guid authorizationId,
        CancellationToken ct)
    {
        var run = context.Run;
        var channel = await reader.FindChannelAsync(authorizationId, ct).ConfigureAwait(false);

        (AutopilotPauseReason Reason, string Code)? refusal = channel switch
        {
            // A pre-channel authorization (S66-7 leaves the column nullable) or a connection outside this scope:
            // nothing names a channel the slot could publish through, and 02.8.3-T3 is what writes it.
            null => (AutopilotPauseReason.ChannelNotActive, AutopilotStepCodes.ChannelNotActive),

            // TikTok Direct Post has no scheduling parameter (07 CR-5), so no cadence slot can be honoured on it.
            { Platform: not NightTales.Domain.Publishing.Platform.YouTube } => (
                AutopilotPauseReason.ChannelUnsupported,
                AutopilotStepCodes.ChannelUnsupported),

            { Status: not ChannelConnectionStatus.Active } => (
                AutopilotPauseReason.ChannelNotActive,
                AutopilotStepCodes.ChannelNotActive),

            _ => null,
        };

        if (refusal is not { } stop)
        {
            return null;
        }

        run.Pause(stop.Reason, context.Now);

        LogChannelUnusable(run.Id, stop.Code);

        return StepResult.Stopped(stop.Code);
    }

    /// <summary>
    /// The step's first execution: price the bound, reserve it, commit the run row, then pay once and advance from
    /// the pick.
    /// </summary>
    private async ValueTask<StepResult> PayAsync(
        AutopilotStepContext context,
        Guid authorizationId,
        CancellationToken ct)
    {
        var run = context.Run;

        var price = await prices.GetActiveAsync(context.Now, ct).ConfigureAwait(false);

        if (price.IsError)
        {
            // No active price version: nothing can be quoted, so nothing may be reserved. A deployment fault, not
            // this slot's: retry, spend nothing, and leave the attempt for the next tick.
            return Retry(context, price.FirstError.Code);
        }

        var priceText = price.Value.Version.ToString(CultureInfo.InvariantCulture);
        var bound = await ideaCosts.WorstCaseAsync(priceText, ct).ConfigureAwait(false);

        if (bound.IsError)
        {
            return Retry(context, bound.FirstError.Code);
        }

        var budgetCheck = await budget
            .CheckAsync(new CostBound(bound.Value.WorstCaseUsd), ct)
            .ConfigureAwait(false);

        if (budgetCheck.IsError)
        {
            return Fail(context, budgetCheck.FirstError.Code);
        }

        var reservation = await ledger
            .ReserveAsync(run.UserId, CreditOperation.Idea, run.SeriesId, bound.Value, ct)
            .ConfigureAwait(false);

        if (reservation.IsError)
        {
            return Fail(context, reservation.FirstError.Code);
        }

        var queued = GenerationRun.Queue(
            run.UserId,
            run.SeriesId,
            episodeId: null,
            GenerationKind.Ideas,
            GenerationTrigger.Autopilot,
            AutopilotIdempotencyKeys.For(run, AutopilotRunStep.Idea),
            previousEpisodeStatus: null,
            inputFactSheetId: null,
            reservation.Value.Value,
            bound.Value.WorstCaseUsd,
            context.Now,
            countsTowardLimit: false);

        if (queued.IsError)
        {
            // The row that would carry the reservation was refused, so nothing can spend it: release it before the
            // run fails (PF-13). This exit and the save below are the only two between the reserve and the call.
            await RefundAsync(context, reservation.Value, queued.FirstError.Code, ct).ConfigureAwait(false);

            return Fail(context, queued.FirstError.Code);
        }

        db.GenerationRuns.Add(queued.Value);

        // The planner's contract: the row is committed before the paid call and no transaction spans it.
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The insert is the whole save, so an exception — a cancellation or the `ux_generation_runs_active_series`
            // violation of an overlapping tick — leaves no row behind, and no call may be paid for. The row is
            // detached so no later save of this scope can insert it over a refunded reservation, and the refund gets
            // its own token because the tick's cancellation is exactly one of the ways to land here (PF-13).
            db.GenerationRuns.Remove(queued.Value);

            await RefundAsync(context, reservation.Value, ReservationNotCommittedReason, CancellationToken.None)
                .ConfigureAwait(false);

            LogRowNotCommitted(run.Id, exception.GetType().Name);

            throw;
        }

        var facts = new RunContext(
            queued.Value.Id,
            run.UserId,
            run.SeriesId,
            EpisodeId: null,
            reservation.Value,
            GenerationTrigger.Autopilot,
            Attempt: 1,
            priceText);

        var pick = await planner
            .ProposeAndPickAsync(run.SeriesId, run.SlotAt, facts, ct)
            .ConfigureAwait(false);

        if (pick.IsError)
        {
            return await RecordFailure(context, queued.Value, pick.FirstError.Code, reservation.Value)
                .ConfigureAwait(false);
        }

        var cost = await db.UsageRecords
            .Where(record => record.ReservationId == reservation.Value.Value)
            .SumAsync(record => record.CostUsd, ct)
            .ConfigureAwait(false);

        queued.Value.Start(context.Now);
        queued.Value.Succeed(cost, resultStoryVersionId: null, resultFactSheetId: null, context.Now);

        // This step is the ideas run's terminal writer (no job follows it), so it settles the reservation itself:
        // the pattern is settle on success, refund on failure, and no reconciliation job repairs a skipped call.
        var settled = await ledger.SettleAsync(reservation.Value, ct).ConfigureAwait(false);

        if (settled.IsError)
        {
            LogLedgerFailed(run.Id, "settle", settled.FirstError.Code);
        }

        run.AttachIdea(pick.Value.IdeaId);
        run.AdvanceTo(AutopilotRunState.IdeaPicked, AutopilotRunStep.Idea, context.Now, authorizationId);

        LogIdeaPicked(run.Id, pick.Value.IdeaId);

        return StepResult.Advanced(AutopilotRunStep.Idea);
    }

    /// <summary>
    /// A row for this run's key already exists: adopt the pick it produced, or answer what its own status says.
    /// </summary>
    private async ValueTask<StepResult> AdoptAsync(
        AutopilotStepContext context,
        GenerationRun row,
        Guid authorizationId,
        CancellationToken ct)
    {
        var run = context.Run;

        // The planner persists the winner as `picked` before it returns; a later MarkUsed names the episode, so a
        // crash between the advance and its save can leave either status behind and both mean "the pick exists".
        var ideaId = await db.EpisodeIdeas
            .Where(idea => idea.GenerationRunId == row.Id
                && idea.SeriesId == run.SeriesId
                && (idea.Status == IdeaStatus.Picked || idea.Status == IdeaStatus.Used))
            .Select(idea => (Guid?)idea.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (ideaId is { } adopted)
        {
            await CompleteAdoptedAsync(context, row, ct).ConfigureAwait(false);

            run.AttachIdea(adopted);
            run.AdvanceTo(AutopilotRunState.IdeaPicked, AutopilotRunStep.Idea, context.Now, authorizationId);

            LogIdeaAdopted(run.Id, adopted);

            return StepResult.Advanced(AutopilotRunStep.Idea);
        }

        return row.Status switch
        {
            // The holder is the tick that committed the row: paying a second time is exactly what must not happen.
            GenerationRunStatus.Queued or GenerationRunStatus.Running => InFlight(run.Id, row),

            // A row that ended without a pick spent its money and produced nothing: the slot's idea is finished.
            GenerationRunStatus.Failed => Fail(context, row.ErrorCode ?? ErrorCodes.IdeasGenerationFailed),
            GenerationRunStatus.Cancelled => Fail(context, ErrorCodes.IdeasGenerationFailed),
            _ => Fail(context, ErrorCodes.IdeasNoIdea),
        };
    }

    /// <summary>
    /// Closes the row a crash left behind: the planner can commit its pick and die before the orchestrator's save, so
    /// the row stays queued (or running) although the call it paid for finished. Nothing else writes that row — no job
    /// follows an ideas row — so adoption starts and succeeds it here, at the cost the call already recorded, and
    /// settles its reservation. Both transitions are no-ops once reached, which makes a second adoption harmless
    /// (CR-8).
    /// </summary>
    /// <param name="context">The run and the tick's clock reading.</param>
    /// <param name="row">The row this run's idempotency key already committed.</param>
    /// <param name="ct">Cancels the cost read.</param>
    private async ValueTask CompleteAdoptedAsync(
        AutopilotStepContext context,
        GenerationRun row,
        CancellationToken ct)
    {
        if (row.Status is not (GenerationRunStatus.Queued or GenerationRunStatus.Running))
        {
            // `succeeded` already holds its cost and settlement; a failed or cancelled row with a pick is a data
            // defect this step must not paper over.
            return;
        }

        if (row.Status == GenerationRunStatus.Queued)
        {
            row.Start(context.Now);
        }

        var cost = row.ReservationId is { } reserved
            ? await db.UsageRecords
                .Where(record => record.ReservationId == reserved)
                .SumAsync(record => record.CostUsd, ct)
                .ConfigureAwait(false)
            : 0m;

        row.Succeed(cost, resultStoryVersionId: null, resultFactSheetId: null, context.Now);

        if (row.ReservationId is not { } reservation)
        {
            return;
        }

        var settled = await ledger.SettleAsync(new ReservationId(reservation), ct).ConfigureAwait(false);

        if (settled.IsError)
        {
            LogLedgerFailed(context.Run.Id, "settle", settled.FirstError.Code);
        }
    }

    /// <summary>Records the planner's failure on both the generation row and the run, and answers the step's result.</summary>
    private async ValueTask<StepResult> RecordFailure(
        AutopilotStepContext context,
        GenerationRun row,
        string code,
        ReservationId reservation)
    {
        if (code == ErrorCodes.ProviderFundsExhausted)
        {
            // Plan §3.6 row 5 reads the code off this row, and plan §3.5 holds the run the way the story step does
            // (`story_generating → paused`). Both writes ride the orchestrator's save with the Stopped result, so the
            // terminal row and the pause commit together; a Retry would save neither and every later tick would adopt
            // the queued row as in flight, idling the slot until slot_missed (CR-1). The series pause is T4's.
            row.Fail(code, detail: null, countsTowardLimit: false, context.Now);

            await RefundAsync(context, reservation, code, CancellationToken.None).ConfigureAwait(false);

            return Held(context, code);
        }

        row.Fail(code, detail: null, countsTowardLimit: false, context.Now);

        // The run mutation and this row are saved together with the Stopped result, but the refund is its own
        // transaction and must not wait on that save: a reservation nobody settles would hold credits forever. The
        // tick's own token is not used, because a cancelled tick is one of the ways a failure is recorded and the
        // release must still happen.
        await RefundAsync(context, reservation, code, CancellationToken.None).ConfigureAwait(false);

        return Fail(context, code);
    }

    /// <summary>Releases the reservation of a slot that produced nothing; a ledger error never replaces the outcome.</summary>
    private async ValueTask RefundAsync(
        AutopilotStepContext context,
        ReservationId reservation,
        string code,
        CancellationToken ct)
    {
        var refunded = await ledger.RefundAsync(reservation, code, ct).ConfigureAwait(false);

        if (refunded.IsError)
        {
            LogLedgerFailed(context.Run.Id, "refund", refunded.FirstError.Code);
        }
    }

    /// <summary>Fails the run with <paramref name="code"/>: a slot that cannot produce its idea fails instead of stalling.</summary>
    private StepResult Fail(AutopilotStepContext context, string code)
    {
        context.Run.Fail(code, context.Now);

        LogFailed(context.Run.Id, code);

        return StepResult.Stopped(code);
    }

    /// <summary>
    /// Answers Retry: the run is left untouched, so nothing about it is saved and the next tick tries again.
    /// </summary>
    private StepResult Retry(AutopilotStepContext context, string code)
    {
        LogRetried(context.Run.Id, code);

        return StepResult.Retry(code);
    }

    /// <summary>
    /// Holds the run with <see cref="AutopilotPauseReason.ProviderFundsExhausted"/>: the slot keeps its row and waits
    /// for the owner, exactly as a held story run does (plan §3.5, §3.6 row 5). The paused run is `Stopped`, so the
    /// pause is saved with the terminal generation row in one save.
    /// </summary>
    private StepResult Held(AutopilotStepContext context, string code)
    {
        context.Run.Pause(AutopilotPauseReason.ProviderFundsExhausted, context.Now);

        LogHeld(context.Run.Id, code);

        return StepResult.Stopped(code);
    }

    private StepResult InFlight(Guid runId, GenerationRun row)
    {
        // The status name is read once: the log call stays cheap even when its level is off (CA1873).
        var status = row.Status.ToString();
        LogInFlight(runId, status);

        return new StepResult(StepResultKind.Idle, AutopilotStepSupport.IdeaInFlightCode);
    }

    [LoggerMessage(
        EventId = 6559,
        Level = LogLevel.Warning,
        Message = "autopilot_idea_channel_unusable runId={RunId} code={Code}")]
    private partial void LogChannelUnusable(Guid runId, string code);

    [LoggerMessage(
        EventId = 6540,
        Level = LogLevel.Warning,
        Message = "autopilot_idea_slot_missed runId={RunId} seriesId={SeriesId}")]
    private partial void LogSlotMissed(Guid runId, Guid seriesId);

    [LoggerMessage(
        EventId = 6541,
        Level = LogLevel.Information,
        Message = "autopilot_idea_authorization_required runId={RunId} seriesId={SeriesId}")]
    private partial void LogAuthorizationRequired(Guid runId, Guid seriesId);

    [LoggerMessage(
        EventId = 6542,
        Level = LogLevel.Warning,
        Message = "autopilot_idea_failed runId={RunId} code={Code}")]
    private partial void LogFailed(Guid runId, string code);

    [LoggerMessage(
        EventId = 6543,
        Level = LogLevel.Information,
        Message = "autopilot_idea_retried runId={RunId} code={Code}")]
    private partial void LogRetried(Guid runId, string code);

    [LoggerMessage(
        EventId = 6548,
        Level = LogLevel.Warning,
        Message = "autopilot_idea_held runId={RunId} code={Code}")]
    private partial void LogHeld(Guid runId, string code);

    [LoggerMessage(
        EventId = 6544,
        Level = LogLevel.Information,
        Message = "autopilot_idea_picked runId={RunId} ideaId={IdeaId}")]
    private partial void LogIdeaPicked(Guid runId, Guid ideaId);

    [LoggerMessage(
        EventId = 6545,
        Level = LogLevel.Information,
        Message = "autopilot_idea_adopted runId={RunId} ideaId={IdeaId}")]
    private partial void LogIdeaAdopted(Guid runId, Guid ideaId);

    [LoggerMessage(
        EventId = 6546,
        Level = LogLevel.Information,
        Message = "autopilot_idea_in_flight runId={RunId} status={Status}")]
    private partial void LogInFlight(Guid runId, string status);

    [LoggerMessage(
        EventId = 6598,
        Level = LogLevel.Error,
        Message = "autopilot_idea_ledger_failed runId={RunId} operation={Operation} code={Code}")]
    private partial void LogLedgerFailed(Guid runId, string operation, string code);

    [LoggerMessage(
        EventId = 6612,
        Level = LogLevel.Error,
        Message = "autopilot_idea_row_not_committed runId={RunId} exceptionType={ExceptionType}")]
    private partial void LogRowNotCommitted(Guid runId, string exceptionType);

    [LoggerMessage(
        EventId = 6547,
        Level = LogLevel.Debug,
        Message = "autopilot_idea_series_held runId={RunId} seriesId={SeriesId}")]
    private partial void LogSeriesHeld(Guid runId, Guid seriesId);
}
