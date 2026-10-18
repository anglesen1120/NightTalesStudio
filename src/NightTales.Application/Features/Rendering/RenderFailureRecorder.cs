using System.Globalization;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Domain.Rendering;
using NightTales.RenderNode.Contract;

namespace NightTales.Application.Features.Rendering;

/// <summary>
/// The single write path of a terminal render outcome (09.6.3-T1 steps 5 and 7): the failure recorder the
/// poller, <c>SubmitRenderJob</c> and 09.6.4-T1's cancel path all use.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order</b> is the one the task fixes (BD7, WT28): <c>Fail</c> → the retry decision (<c>CreateRetryAsync</c>
/// re-checks the budget and reserves the new attempt's own credits) → one refund by <c>reservation_id</c> →
/// one audit row → <c>RaiseFailed</c> → the episode back to <c>story_approved</c>, and that last step only when
/// no retry follows, because a retry row means the episode is still <c>rendering</c>.
/// </para>
/// <para>
/// <b>A cancel-requested row is never retried.</b> When the attempt carries <c>CancelRequestedAt</c> — the owner
/// asked for the cancel and the adapter has not answered it yet — the verdict is final whatever the code is: the
/// retry table is not consulted at all, so no second attempt is reserved and charged for an episode the owner
/// cancelled (WT51, 09.6.3-T1 F3/SEC-4).
/// </para>
/// <para>
/// <b>The refund never blocks the record.</b> The ledger call runs on its own 10-second budget (the
/// <c>ProposeBibleUpdateHandler</c>/<c>GenerateFactSheetPipeline</c> precedent), so a shutdown or a slow ledger
/// cannot skip the refund of a terminal attempt, and a failed refund is logged rather than thrown: the ledger is
/// idempotent per reservation, and the failure record itself is what the caller is committing.
/// </para>
/// <para>
/// <b>Ids and codes only.</b> The audit row carries the episode, the attempt, the code and the retry verdict —
/// never adapter text, params or story content (BD16, D19).
/// </para>
/// </remarks>
/// <param name="db">The tracked write model of the scope the job runs in.</param>
/// <param name="clock">The clock every stamp of the terminal transition is taken from.</param>
/// <param name="ledger">The credit ledger the failed or cancelled attempt is refunded through.</param>
/// <param name="retries">The retry policy the outcome code is put to.</param>
/// <param name="audit">The audit writer; it refuses to write without the caller's transaction (09.3.6-T1).</param>
/// <param name="logger">The outcome log of a terminal transition.</param>
public sealed partial class RenderFailureRecorder(
    IAppDbContext db,
    IClock clock,
    ICreditLedger ledger,
    IRenderRetryPolicy retries,
    IAuditWriter audit,
    ILogger<RenderFailureRecorder> logger) : IRenderFailureRecorder
{
    /// <summary>The <c>RefundAsync</c> reason of a failed render attempt (D1: a short, safe reason).</summary>
    public const string RefundReason = "render_failed";

    /// <summary>The <c>RefundAsync</c> reason of a render the adapter reported terminal <c>cancelled</c> (WT51).</summary>
    public const string CancelledRefundReason = "render_cancelled";

    /// <summary>The audit action of a failed attempt (its <c>render_timeout</c> watchdog included).</summary>
    public const string FailedAction = "render.failed";

    /// <summary>The audit action of a render cancelled after the adapter reported it cancelled.</summary>
    public const string CancelledAction = "render.cancelled";

    /// <summary>The <c>audit_logs.entity_type</c> of a terminal render attempt.</summary>
    public const string EntityType = "render_job";

    private static readonly TimeSpan LedgerTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public async Task<ErrorOr<RenderFailureResult>> FailAsync(
        RenderJob job,
        string code,
        bool? outcomeRetryable,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        RequireTransaction();

        var now = clock.UtcNow;

        var failed = job.Fail(code, now);
        if (failed.IsError)
        {
            return failed.Errors;
        }

        // The retry decision, and the only place a retry row is created. A refusal (no active price version, a
        // spend cap, an insufficient balance) is not an error of this transition: it means the attempt is final
        // (03 D8/CR-4, WT50).
        //
        // A row the owner already asked to cancel is never retried (WT51): asking for a cancel ends the attempt
        // whatever the adapter answers afterwards, so no second attempt is reserved for an episode the owner
        // cancelled. ShouldRetry is not even asked.
        Guid? retryJobId = null;
        if (job.CancelRequestedAt is null && retries.ShouldRetry(code, job.Attempt, outcomeRetryable))
        {
            var created = await retries.CreateRetryAsync(job, ct).ConfigureAwait(false);
            if (created.IsError)
            {
                LogRetryRefused(job.Id, code, created.FirstError.Code);
            }
            else
            {
                retryJobId = created.Value.Id;
            }
        }

        var final = retryJobId is null;

        await RefundAsync(job, RefundReason).ConfigureAwait(false);

        await audit.WriteAsync(
                new AuditEntry(
                    ActorUserId: job.UserId,
                    // The job runs under SystemContext: the row says the platform ended the attempt, on the
                    // owner's behalf (the ProposeBibleUpdateHandler shape for a system-context write).
                    ActorType: ActorType.System,
                    SubjectUserId: job.UserId,
                    Action: FailedAction,
                    Category: Category.Episodes,
                    Visibility: Visibility.User,
                    EntityType: EntityType,
                    EntityId: job.Id,
                    DataJson: Data(job, code, final, retryJobId),
                    Ip: null,
                    UserAgent: null,
                    ActorLabel: null,
                    OccurredAt: now),
                ct).ConfigureAwait(false);

        var raised = job.RaiseFailed(final);
        if (raised.IsError)
        {
            return raised.Errors;
        }

        if (final)
        {
            await MoveOutOfRenderingAsync(job, now, ct).ConfigureAwait(false);
        }

        LogFailed(job.Id, job.EpisodeId, code, job.Attempt, final, retryJobId);

        // The caller counts the retry after it commits (RetryCreated), so a commit this row never reached cannot
        // leave a counted retry behind (09.6.3-T1 F10).
        return new RenderFailureResult(final, retryJobId, code, retryJobId is not null);
    }

    /// <inheritdoc />
    public async Task<ErrorOr<Success>> CancelAsync(RenderJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        RequireTransaction();

        var now = clock.UtcNow;

        var cancelled = job.Cancel(now);
        if (cancelled.IsError)
        {
            return cancelled.Errors;
        }

        await MoveOutOfRenderingAsync(job, now, ct).ConfigureAwait(false);
        await RefundAsync(job, CancelledRefundReason).ConfigureAwait(false);

        await audit.WriteAsync(
                new AuditEntry(
                    ActorUserId: job.UserId,
                    ActorType: ActorType.System,
                    SubjectUserId: job.UserId,
                    Action: CancelledAction,
                    Category: Category.Episodes,
                    Visibility: Visibility.User,
                    EntityType: EntityType,
                    EntityId: job.Id,
                    DataJson: Data(job, RenderOutcomeCodes.RenderCancelled, final: true, retryJobId: null),
                    Ip: null,
                    UserAgent: null,
                    ActorLabel: null,
                    OccurredAt: now),
                ct).ConfigureAwait(false);

        LogCancelled(job.Id, job.EpisodeId, job.Attempt);

        return Result.Success;
    }

    /// <summary>
    /// Refunds the attempt's reservation exactly once, by reservation id (WT50), on a budget of its own.
    /// </summary>
    /// <remarks>
    /// The ledger's own budget is deliberate: the caller's token may already be cancelled by a shutdown, and a
    /// terminal transition must not skip the refund because of it (the <c>ProposeBibleUpdateHandler</c>
    /// precedent). <c>RefundAsync</c> is idempotent per reservation, so the sweep or the next delivery repeating
    /// it is a success.
    /// </remarks>
    /// <param name="job">The terminal attempt.</param>
    /// <param name="reason">The D1 reason of this refund.</param>
    private async Task RefundAsync(RenderJob job, string reason)
    {
        using var budget = new CancellationTokenSource(LedgerTimeout);

        try
        {
            var refunded = await ledger
                .RefundAsync(new ReservationId(job.ReservationId), reason, budget.Token)
                .ConfigureAwait(false);

            if (refunded.IsError)
            {
                LogLedgerRefundFailed(job.Id, refunded.FirstError.Code);
            }
        }
        catch (Exception exception)
        {
            // A refund that did not reach the ledger is logged, never thrown: the failure record is the
            // caller's commit, and the ledger retries a reservation refund idempotently.
            LogLedgerRefundFailed(job.Id, exception.GetType().Name);
        }
    }

    /// <summary>
    /// Returns the episode to <c>story_approved</c> (BD5, WT50): the attempt ended and the creator may fix the
    /// story or start another render.
    /// </summary>
    /// <param name="job">The terminal attempt whose episode leaves <c>rendering</c>.</param>
    /// <param name="now">The clock's reading.</param>
    /// <param name="ct">Cancels the read.</param>
    private async Task MoveOutOfRenderingAsync(RenderJob job, DateTimeOffset now, CancellationToken ct)
    {
        var episode = await db.Episodes
            .SingleOrDefaultAsync(candidate => candidate.Id == job.EpisodeId, ct)
            .ConfigureAwait(false);

        if (episode is null)
        {
            LogEpisodeMissing(job.Id, job.EpisodeId);

            return;
        }

        var moved = episode.RenderFailed(now);
        if (moved.IsError)
        {
            // The transition is idempotent for `story_approved`, so a mismatch means another writer already
            // decided the episode's state: the failure itself stands and is logged, not rolled back.
            LogEpisodeNotRendering(job.Id, job.EpisodeId, moved.FirstError.Code);
        }
    }

    /// <summary>Builds the audit row's <c>data</c> payload: ids, the attempt, the code and the retry verdict.</summary>
    /// <param name="job">The terminal attempt.</param>
    /// <param name="code">The 05 D5 outcome code it ended with.</param>
    /// <param name="final">Whether no retry follows.</param>
    /// <param name="retryJobId">The retry row, when one was created.</param>
    /// <returns>The payload as JSON text.</returns>
    private static string Data(RenderJob job, string code, bool final, Guid? retryJobId)
    {
        var data = new JsonObject
        {
            ["episodeId"] = job.EpisodeId.ToString("D", CultureInfo.InvariantCulture),
            ["renderJobId"] = job.Id.ToString("D", CultureInfo.InvariantCulture),
            ["attempt"] = job.Attempt,
            ["code"] = code,
            ["final"] = final,
        };

        if (retryJobId is { } retry)
        {
            data["retryJobId"] = retry.ToString("D", CultureInfo.InvariantCulture);
        }

        return data.ToJsonString();
    }

    /// <summary>
    /// Refuses to run outside an open transaction: the audit row of a terminal attempt must commit with the
    /// failure it records, and an audit write without one is a programming error (09.3.6-T1's
    /// <c>EfAuditWriter</c> guard, the same idea).
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is open on the scope's context.</exception>
    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "IRenderFailureRecorder requires an ambient transaction: the poller and SubmitRenderJob call "
                + "IUnitOfWork.BeginAsync first, and a failure must commit together with its refund, its audit "
                + "row and the retry it queues.");
        }
    }

    [LoggerMessage(
        EventId = 6210,
        Level = LogLevel.Warning,
        Message = "render_failure_retry_refused renderJobId={RenderJobId} code={Code} reason={Reason}")]
    private partial void LogRetryRefused(Guid renderJobId, string code, string reason);

    [LoggerMessage(
        EventId = 6211,
        Level = LogLevel.Error,
        Message = "render_failure_recorded renderJobId={RenderJobId} episodeId={EpisodeId} code={Code} attempt={Attempt} final={Final} retryJobId={RetryJobId}")]
    private partial void LogFailed(Guid renderJobId, Guid episodeId, string code, int attempt, bool final, Guid? retryJobId);

    [LoggerMessage(
        EventId = 6212,
        Level = LogLevel.Information,
        Message = "render_cancelled_recorded renderJobId={RenderJobId} episodeId={EpisodeId} attempt={Attempt}")]
    private partial void LogCancelled(Guid renderJobId, Guid episodeId, int attempt);

    [LoggerMessage(
        EventId = 6213,
        Level = LogLevel.Warning,
        Message = "render_ledger_refund_failed renderJobId={RenderJobId} reason={Reason}")]
    private partial void LogLedgerRefundFailed(Guid renderJobId, string reason);

    [LoggerMessage(
        EventId = 6214,
        Level = LogLevel.Warning,
        Message = "render_failure_episode_missing renderJobId={RenderJobId} episodeId={EpisodeId}")]
    private partial void LogEpisodeMissing(Guid renderJobId, Guid episodeId);

    [LoggerMessage(
        EventId = 6215,
        Level = LogLevel.Warning,
        Message = "render_failure_episode_not_rendering renderJobId={RenderJobId} episodeId={EpisodeId} code={Code}")]
    private partial void LogEpisodeNotRendering(Guid renderJobId, Guid episodeId, string code);
}
