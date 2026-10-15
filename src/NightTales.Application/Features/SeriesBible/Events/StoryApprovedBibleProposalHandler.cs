using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.SeriesBible.Jobs;
using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Events;
using NightTales.Domain.Stories.Generation;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.SeriesBible.Events;

/// <summary>
/// Consumes <c>story_approved.v1</c> and queues the one paid bible-proposal run of that approval (02.6.3-T2
/// plan §7.1 steps 2–7): the guards, the budget admission, the <c>generation_runs</c> row, the credit
/// reservation and the enqueue of <see cref="ProposeBibleUpdateJob"/>. It is the only writer of a
/// <c>bible_proposal</c> run row; the job that follows owns the paid call and the effect on the bible.
/// </summary>
/// <remarks>
/// <para>
/// <b>Guard order</b> (plan §7.1 step 2 and this task's steps 4): the series must exist and not be archived;
/// a run with the same key must not exist (delivery is at-least-once); the feature's kill switch must be on; a
/// true-crime autopilot approval is refused (defensive: true crime never auto-approves); then the worst case is
/// priced and the budget guard admitted. Everything after that — the run row and the reservation — is one
/// transaction, so the guard's platform-wide advisory lock is held until the run row commits and the next
/// admission sees this run's open bound.
/// </para>
/// <para>
/// <b>Idempotency.</b> <c>bible_proposal</c> is a series-level kind, so <c>generation_runs.episode_id</c> is
/// <c>NULL</c> and the episode-level index can never be the guard. The key
/// <c>bible:{episodeId}:{storyVersionId}</c> is: it lets a genuine re-approval of a <em>new</em> version queue a
/// new run, while a redelivery of the same approval finds the row of the first delivery. A redelivery that finds
/// the run still <c>queued</c> enqueues its job again, because a delivery whose enqueue died after its commit
/// would otherwise leave a run nobody runs (the job is idempotent on the run).
/// </para>
/// <para>
/// <b>One proposal at a time.</b> The D2 partial unique index <c>ux_generation_runs_active_series</c> admits one
/// <c>queued</c>/<c>running</c> series-level run per series and kind, so a second approval of the same series
/// while the first is still running meets it. The handler does not fail and does not skip: it refunds the
/// reservation it just made and re-schedules this same work in two minutes through
/// <c>IJobScheduler.Schedule</c>, at most <see cref="MaxHandoffTries"/> times; after that it logs
/// <c>bible_proposal_dropped</c> and drops the proposal (a missed summary line is acceptable, plan §7.1 step 7).
/// </para>
/// <para>
/// <b>Budget denial</b> writes a <c>failed</c> run with <c>spend_cap_reached</c> and
/// <c>counts_toward_limit = false</c>, reserves nothing and queues no job: a nice-to-have must never break the
/// budget (plan §7.1 step 3).
/// </para>
/// <para>
/// <b>Logs</b> carry ids, the idempotency key's parts and the worst-case cost only — never bible text, the story
/// body or a prompt (D19).
/// </para>
/// </remarks>
/// <param name="db">The tracked write model of the delivery's scope, pinned to the event's user.</param>
/// <param name="unitOfWork">The transaction that carries the budget guard's advisory lock and the run row.</param>
/// <param name="costEstimator">The worst case of one proposal call at the active price version.</param>
/// <param name="budgetGuard">The platform spend cap (D1: checked before every paid dispatch).</param>
/// <param name="creditLedger">The credit reservation (metering-only in phase P).</param>
/// <param name="scheduler">The job port: the proposal job, or this work again after the two-minute delay.</param>
/// <param name="proposalOptions">The kill switch and op caps of the feature, observed so a change applies without a restart.</param>
/// <param name="clock">The clock every stamp of this delivery is taken from.</param>
/// <param name="logger">The outcome log of this delivery.</param>
public sealed partial class StoryApprovedBibleProposalHandler(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    IBibleProposalCostEstimator costEstimator,
    IBudgetGuard budgetGuard,
    ICreditLedger creditLedger,
    IJobScheduler scheduler,
    IOptionsMonitor<BibleProposalJobOptions> proposalOptions,
    IClock clock,
    ILogger<StoryApprovedBibleProposalHandler> logger) : IOutboxHandler<StoryApproved>
{
    /// <summary>The number of the first delivery of one approval's work.</summary>
    private const int FirstTry = 1;

    /// <summary>
    /// How many deliveries one approval's work may spend on the series-level run index before the proposal is
    /// dropped (plan §7.1 step 7: "max 10 tries, then logs and drops it").
    /// </summary>
    private const int MaxHandoffTries = 10;

    /// <summary>The delay of a deferred delivery (plan §7.1 step 7: "re-schedules itself in 2 min").</summary>
    private static readonly TimeSpan HandoffDelay = TimeSpan.FromMinutes(2);

    /// <summary>The <c>error_detail</c> of a run the budget guard refused.</summary>
    private const string BudgetGuardDenied = "budget_guard_denied";

    /// <summary>The refund reason of a reservation whose run was deferred (D1: a reservation is never left open).</summary>
    private const string DeferredReason = "bible_proposal_deferred";

    /// <summary>The refund reason of a reservation whose run could not be committed at all (D1).</summary>
    private const string NotQueuedReason = "bible_proposal_generation_failed";

    /// <inheritdoc />
    public ValueTask HandleAsync(
        StoryApproved domainEvent,
        OutboxContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return new ValueTask(QueueProposalAsync(
            domainEvent.SeriesId,
            domainEvent.EpisodeId,
            domainEvent.StoryVersionId,
            domainEvent.ApprovalSource,
            FirstTry,
            cancellationToken));
    }

    /// <summary>
    /// Re-runs the queueing work of one approval after a deferred delivery (plan §7.1 step 7), called by
    /// <c>BibleProposalHandoffJob</c> in the Worker.
    /// </summary>
    /// <remarks>
    /// The delivery scope must already be pinned to the episode's owner: this method reads and writes through the
    /// tenant-filtered context and never bypasses a filter. It applies the same guards as
    /// <see cref="HandleAsync"/>, so a deferred delivery that finds the run of the first approval already
    /// committed skips instead of queueing a second one.
    /// </remarks>
    /// <param name="handoff">The deferred work, with the number of deliveries it already spent.</param>
    /// <param name="cancellationToken">Cancels the delivery (the Worker's shutdown token).</param>
    /// <returns>A task that completes when the work was queued, skipped or deferred again.</returns>
    public Task HandleHandoffAsync(BibleProposalHandoff handoff, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handoff);

        return QueueProposalAsync(
            handoff.SeriesId,
            handoff.EpisodeId,
            handoff.StoryVersionId,
            handoff.ApprovalSource,
            handoff.Tries,
            cancellationToken);
    }

    /// <summary>
    /// The guards, the admission and the queueing of one approval's proposal run — the body shared by the outbox
    /// delivery and a deferred handoff.
    /// </summary>
    private async Task QueueProposalAsync(
        Guid seriesId,
        Guid episodeId,
        Guid storyVersionId,
        ApprovalSource approvalSource,
        int tries,
        CancellationToken cancellationToken)
    {
        // 1. The series. The delivery scope is pinned to the event's user, so another tenant's series is a
        //    missing row and the work is skipped (D10; a bible proposal is never built across tenants).
        var series = await db.Series
            .SingleOrDefaultAsync(candidate => candidate.Id == seriesId, cancellationToken)
            .ConfigureAwait(false);
        if (series is null)
        {
            LogSeriesMissing(seriesId);
            return;
        }

        if (series.ArchivedAt is not null)
        {
            LogSeriesArchived(seriesId);
            return;
        }

        // 2. The replay guard: delivery is at-least-once, and the key names both the episode and the version, so a
        //    genuine re-approval of a new version is a different key and still queues its own run.
        var idempotencyKey = IdempotencyKey(episodeId, storyVersionId);
        var existing = await db.GenerationRuns
            .AsNoTracking()
            .Where(run => run.IdempotencyKey == idempotencyKey)
            .Select(run => new { run.Id, run.Status })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            LogReplay(seriesId, episodeId, existing.Id, existing.Status);

            // A committed run whose enqueue died leaves a queued run nobody runs; re-enqueueing is safe because
            // the job is idempotent on the run (a terminal run is skipped, a running one belongs to its worker).
            if (existing.Status == GenerationRunStatus.Queued)
            {
                EnqueueProposalJob(seriesId, episodeId, existing.Id);
            }

            return;
        }

        // 3. The kill switch (plan §11): off means no run row at all, so a host that is rolled back spends
        //    nothing and stores nothing.
        if (!proposalOptions.CurrentValue.Enabled)
        {
            LogDisabled(seriesId, episodeId);
            return;
        }

        // 4. Defensive: true crime never auto-approves, so this combination can only arrive from a defect.
        if (series.Topic == Topic.TrueCrime && approvalSource == ApprovalSource.Autopilot)
        {
            LogTrueCrimeAutopilot(seriesId, episodeId);
            return;
        }

        // 5. The worst case, then the admission. The transaction opens first, so the guard's platform-wide
        //    advisory lock is held until this run row commits and the next admission counts its open bound.
        var bound = await costEstimator.WorstCaseAsync(cancellationToken).ConfigureAwait(false);
        if (bound.IsError)
        {
            // Nothing is queued and nothing is stored, so a later delivery (or a re-approval) retries this work
            // once the catalogue carries the price version the estimator needs.
            LogNotPriced(seriesId, episodeId, Describe(bound.Errors));
            return;
        }

        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        var budget = await budgetGuard
            .CheckAsync(new CostBound(bound.Value.WorstCaseUsd), cancellationToken)
            .ConfigureAwait(false);
        if (budget.IsError)
        {
            await RecordBudgetDenialAsync(series, episodeId, storyVersionId, approvalSource, idempotencyKey, bound.Value, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // 6. The reservation before the row, because the row records it: the reservation names the series (the
        //    run id does not exist yet), and the run row is what makes it visible to the guard.
        var reservation = await creditLedger
            .ReserveAsync(series.UserId, CreditOperation.BibleProposal, series.Id, bound.Value, cancellationToken)
            .ConfigureAwait(false);
        if (reservation.IsError)
        {
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
            LogReservationRefused(seriesId, episodeId, Describe(reservation.Errors));
            return;
        }

        var reservationId = reservation.Value;
        var queued = Queue(series, approvalSource, idempotencyKey, reservationId.Value, bound.Value);
        if (queued.IsError)
        {
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
            await RefundAsync(reservationId, NotQueuedReason, cancellationToken).ConfigureAwait(false);
            LogQueueRefused(seriesId, episodeId, Describe(queued.Errors));
            return;
        }

        var run = queued.Value;
        db.GenerationRuns.Add(run);

        try
        {
            var committed = await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (committed.IsError)
            {
                // A mapped refusal already rolled the transaction back; the run row did not land, so the
                // reservation is released and the message is left due for the dispatcher to deliver again.
                await RefundAsync(reservationId, NotQueuedReason, cancellationToken).ConfigureAwait(false);

                throw new InvalidOperationException(
                    $"The bible proposal run of series {seriesId} could not be queued: {Describe(committed.Errors)}");
            }
        }
        catch (DbUpdateException exception) when (BibleProposalRunConflict.IsSeriesLevelRunInProgress(exception))
        {
            // Another proposal of this series is still queued or running: the proposals of one series run strictly
            // one after another, so this delivery is deferred instead of failed (plan §7.1 step 7).
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
            await RefundAsync(reservationId, DeferredReason, cancellationToken).ConfigureAwait(false);
            Defer(seriesId, episodeId, storyVersionId, approvalSource, tries);

            return;
        }

        LogQueued(seriesId, episodeId, run.Id, bound.Value.WorstCaseUsd);

        EnqueueProposalJob(seriesId, episodeId, run.Id);
    }

    /// <summary>
    /// Queues the run of a budget-refused approval as a <c>failed</c> row with <c>spend_cap_reached</c> (plan §7.1
    /// step 3): the refusal is observable in the same table as every other outcome, no reservation is made and no
    /// job is queued.
    /// </summary>
    private async Task RecordBudgetDenialAsync(
        SeriesAggregate series,
        Guid episodeId,
        Guid storyVersionId,
        ApprovalSource approvalSource,
        string idempotencyKey,
        UsageBound bound,
        CancellationToken cancellationToken)
    {
        var queued = Queue(series, approvalSource, idempotencyKey, reservationId: null, bound);
        if (queued.IsError)
        {
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
            LogQueueRefused(series.Id, episodeId, Describe(queued.Errors));
            return;
        }

        var run = queued.Value;
        run.Fail(ErrorCodes.SpendCapReached, BudgetGuardDenied, countsTowardLimit: false, clock.UtcNow);
        db.GenerationRuns.Add(run);

        var committed = await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (committed.IsError)
        {
            throw new InvalidOperationException(
                $"The refused bible proposal run of series {series.Id} could not be recorded: {Describe(committed.Errors)}");
        }

        LogSpendCapReached(series.Id, episodeId, storyVersionId, bound.WorstCaseUsd);
    }

    /// <summary>
    /// The D2 run of one approval: series-level (<c>episode_id</c> is <c>NULL</c> — the approved episode is
    /// recorded on the bible's proposal and in this key), <c>system</c> for a user approval and <c>autopilot</c>
    /// for an autopilot one, and never counting toward the FR-02-7 daily allowance (D2: only <c>kind = story</c>
    /// with <c>trigger = user</c> does).
    /// </summary>
    private ErrorOr<GenerationRun> Queue(
        SeriesAggregate series,
        ApprovalSource approvalSource,
        string idempotencyKey,
        Guid? reservationId,
        UsageBound bound) =>
        GenerationRun.Queue(
            series.UserId,
            series.Id,
            episodeId: null,
            GenerationKind.BibleProposal,
            approvalSource == ApprovalSource.Autopilot ? GenerationTrigger.Autopilot : GenerationTrigger.System,
            idempotencyKey,
            previousEpisodeStatus: null,
            inputFactSheetId: null,
            reservationId,
            bound.WorstCaseUsd,
            clock.UtcNow,
            countsTowardLimit: false);

    /// <summary>Enqueues the proposal job of one run, ids only (09.2.6).</summary>
    private void EnqueueProposalJob(Guid seriesId, Guid episodeId, Guid generationRunId) =>
        scheduler.Enqueue<ProposeBibleUpdateJob>((job, token) => job.RunAsync(seriesId, episodeId, generationRunId, token));

    /// <summary>
    /// Re-schedules this same work in two minutes, or drops it after <see cref="MaxHandoffTries"/> deliveries
    /// (plan §7.1 step 7). Dropping is deliberate: a missed summary line is acceptable, a delivery that fails
    /// forever is not.
    /// </summary>
    private void Defer(Guid seriesId, Guid episodeId, Guid storyVersionId, ApprovalSource approvalSource, int tries)
    {
        if (tries >= MaxHandoffTries)
        {
            LogDropped(seriesId, episodeId, storyVersionId, tries);
            return;
        }

        var next = tries + 1;
        scheduler.Schedule<BibleProposalHandoffJob>(
            (job, token) => job.RunAsync(seriesId, episodeId, storyVersionId, approvalSource, next, token),
            HandoffDelay);

        LogDeferred(seriesId, episodeId, next);
    }

    /// <summary>Releases a reservation; a failed release is logged and never replaces the outcome of the delivery.</summary>
    private async Task RefundAsync(ReservationId reservationId, string reason, CancellationToken cancellationToken)
    {
        var refunded = await creditLedger
            .RefundAsync(reservationId, reason, cancellationToken)
            .ConfigureAwait(false);
        if (refunded.IsError)
        {
            LogRefundFailed(reservationId.Value, Describe(refunded.Errors));
        }
    }

    /// <summary>The D2 idempotency key of one approval: one episode's approved version is proposed once.</summary>
    private static string IdempotencyKey(Guid episodeId, Guid storyVersionId) =>
        $"bible:{episodeId}:{storyVersionId}";

    /// <summary>The codes of an outcome, never the descriptions and never content (09 README D17).</summary>
    private static string Describe(IReadOnlyList<Error> errors) =>
        string.Join(", ", errors.Select(error => error.Code));

    [LoggerMessage(EventId = 6320, Level = LogLevel.Warning, Message = "bible_proposal_skipped series_missing seriesId={SeriesId}")]
    private partial void LogSeriesMissing(Guid seriesId);

    [LoggerMessage(EventId = 6321, Level = LogLevel.Information, Message = "bible_proposal_skipped series_archived seriesId={SeriesId}")]
    private partial void LogSeriesArchived(Guid seriesId);

    [LoggerMessage(EventId = 6322, Level = LogLevel.Information, Message = "bible_proposal_replay seriesId={SeriesId} episodeId={EpisodeId} runId={RunId} status={Status}")]
    private partial void LogReplay(Guid seriesId, Guid episodeId, Guid runId, GenerationRunStatus status);

    [LoggerMessage(EventId = 6323, Level = LogLevel.Information, Message = "bible_proposal_skipped disabled seriesId={SeriesId} episodeId={EpisodeId}")]
    private partial void LogDisabled(Guid seriesId, Guid episodeId);

    [LoggerMessage(EventId = 6324, Level = LogLevel.Warning, Message = "bible_proposal_skipped true_crime_autopilot seriesId={SeriesId} episodeId={EpisodeId}")]
    private partial void LogTrueCrimeAutopilot(Guid seriesId, Guid episodeId);

    [LoggerMessage(EventId = 6325, Level = LogLevel.Warning, Message = "bible_proposal_skipped not_priced seriesId={SeriesId} episodeId={EpisodeId} errorCodes={ErrorCodes}")]
    private partial void LogNotPriced(Guid seriesId, Guid episodeId, string errorCodes);

    [LoggerMessage(EventId = 6326, Level = LogLevel.Warning, Message = "bible_proposal_skipped reservation_refused seriesId={SeriesId} episodeId={EpisodeId} errorCodes={ErrorCodes}")]
    private partial void LogReservationRefused(Guid seriesId, Guid episodeId, string errorCodes);

    [LoggerMessage(EventId = 6327, Level = LogLevel.Warning, Message = "bible_proposal_skipped run_refused seriesId={SeriesId} episodeId={EpisodeId} errorCodes={ErrorCodes}")]
    private partial void LogQueueRefused(Guid seriesId, Guid episodeId, string errorCodes);

    [LoggerMessage(EventId = 6328, Level = LogLevel.Warning, Message = "bible_proposal_spend_cap_reached seriesId={SeriesId} episodeId={EpisodeId} storyVersionId={StoryVersionId} worstCaseUsd={WorstCaseUsd}")]
    private partial void LogSpendCapReached(Guid seriesId, Guid episodeId, Guid storyVersionId, decimal worstCaseUsd);

    [LoggerMessage(EventId = 6329, Level = LogLevel.Information, Message = "bible_proposal_queued seriesId={SeriesId} episodeId={EpisodeId} runId={RunId} worstCaseUsd={WorstCaseUsd}")]
    private partial void LogQueued(Guid seriesId, Guid episodeId, Guid runId, decimal worstCaseUsd);

    [LoggerMessage(EventId = 6330, Level = LogLevel.Information, Message = "bible_proposal_deferred seriesId={SeriesId} episodeId={EpisodeId} try={TryNumber}")]
    private partial void LogDeferred(Guid seriesId, Guid episodeId, int tryNumber);

    [LoggerMessage(EventId = 6331, Level = LogLevel.Warning, Message = "bible_proposal_dropped seriesId={SeriesId} episodeId={EpisodeId} storyVersionId={StoryVersionId} tries={Tries}")]
    private partial void LogDropped(Guid seriesId, Guid episodeId, Guid storyVersionId, int tries);

    [LoggerMessage(EventId = 6332, Level = LogLevel.Warning, Message = "bible_proposal_refund_failed reservationId={ReservationId} errorCodes={ErrorCodes}")]
    private partial void LogRefundFailed(Guid reservationId, string errorCodes);
}
