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
/// work. That is the whole of "a replay never pays twice".
/// </para>
/// <para>
/// <b>Error mapping</b> (task file §Failure handling, plan §7.3): <c>spend_cap_reached</c> fails the run (the
/// platform refuses the charge; the aggregate records the reason, the owner's notice is 02.8.3-T4's); a
/// <c>provider_funds_exhausted</c> refunds the reservation and answers <see cref="StepResultKind.Retry"/>, which is
/// 02.2.2's dispatch rule ("held, not failed"); Retry saves nothing, so the row stays queued and the hold itself —
/// including the series pause that closes the slot — is 02.8.3-T4's trigger 5, not something this step may claim
/// to persist. Every ideas error
/// (<c>ideas_no_idea</c> with its <c>variety_*</c> detail, <c>ideas_moderated</c>, <c>ideas_invalid_output</c>,
/// <c>ideas_generation_failed</c>) fails the run with that code and is never retried in the same slot
/// (02.8.4-T1's variety steering reads the recorded outcome). A pricing or budget-readiness error
/// (<c>price_catalog_empty</c>, <c>prompt_not_shipped</c>) is a deployment fault rather than this slot's fault, so
/// it answers Retry and spends nothing.
/// </para>
/// <para>
/// The text the planner returns is never logged (D19): the log lines carry run, series and idea ids and codes only.
/// </para>
/// </remarks>
public sealed partial class PlanIdeaStep(
    IAppDbContext db,
    IIdeaPlanner planner,
    IPriceCatalog prices,
    IIdeaCostEstimator ideaCosts,
    IBudgetGuard budget,
    ICreditLedger ledger,
    ILogger<PlanIdeaStep> logger) : IAutopilotStepHandler
{
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

        return await PayAsync(context, authorizationId.Value, ct).ConfigureAwait(false);
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
            return Fail(context, queued.FirstError.Code);
        }

        db.GenerationRuns.Add(queued.Value);

        // The planner's contract: the row is committed before the paid call and no transaction spans it.
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

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
            return await RecordFailure(context, queued.Value, pick.FirstError.Code, reservation.Value, ct)
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

    /// <summary>Records the planner's failure on both the generation row and the run, and answers the step's result.</summary>
    private async ValueTask<StepResult> RecordFailure(
        AutopilotStepContext context,
        GenerationRun row,
        string code,
        ReservationId reservation,
        CancellationToken ct)
    {
        if (code == ErrorCodes.ProviderFundsExhausted)
        {
            // 02.2.2's dispatch rule: the run is held, not failed. Nothing of this row survives the tick — Retry
            // saves nothing — so the row stays queued (a later tick adopts it as in flight) and the reservation is
            // refunded rather than left to hold credits no call will spend. Closing the slot is 02.8.3-T4's
            // trigger 5 (series pause), which also owns persisting the hold.
            await RefundAsync(context, reservation, code, ct).ConfigureAwait(false);

            return RetryHeld(context, code);
        }

        row.Fail(code, detail: null, countsTowardLimit: false, context.Now);

        // The run mutation and this row are saved together with the Stopped result, but the refund is its own
        // transaction and must not wait on that save: a reservation nobody settles would hold credits forever.
        await RefundAsync(context, reservation, code, ct).ConfigureAwait(false);

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

    /// <summary>Answers Retry for a held provider: the reservation is refunded and the run itself does not change.</summary>
    private StepResult RetryHeld(AutopilotStepContext context, string code)
    {
        LogHeld(context.Run.Id, code);

        return StepResult.Retry(code);
    }

    private StepResult InFlight(Guid runId, GenerationRun row)
    {
        // The status name is read once: the log call stays cheap even when its level is off (CA1873).
        var status = row.Status.ToString();
        LogInFlight(runId, status);

        return new StepResult(StepResultKind.Idle, AutopilotStepSupport.IdeaInFlightCode);
    }

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
        EventId = 6547,
        Level = LogLevel.Debug,
        Message = "autopilot_idea_series_held runId={RunId} seriesId={SeriesId}")]
    private partial void LogSeriesHeld(Guid runId, Guid seriesId);
}
