using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Application.Features.Stories.Generation;

/// <summary>How a failed run ends (02.2.2-T3 outcome table): the D8 code, safe detail, counting and the refund reason.</summary>
/// <param name="Code">The D8 job-outcome code, never an HTTP code.</param>
/// <param name="Detail">Safe text such as <c>fact_sheet_outdated</c>; never prompt, brief or story text (D19).</param>
/// <param name="CountsTowardLimit">Whether the failure counts toward FR-02-7.</param>
/// <param name="RefundReason">One of <c>story_invalid_output</c>, <c>story_moderated</c>, <c>story_generation_failed</c>, <c>provider_unavailable</c>, <c>stale_run</c>.</param>
public sealed record StoryFailure(string Code, string? Detail, bool CountsTowardLimit, string RefundReason);

/// <summary>What a successful run saved (02.2.2-T3 step 7).</summary>
/// <param name="GenerationRunId">The run.</param>
/// <param name="VersionIds">Every story version the run added to the context (one, or two once a revision exists).</param>
/// <param name="CurrentVersionId">The version the episode points at.</param>
public sealed record StoryCompletion(Guid GenerationRunId, IReadOnlyList<Guid> VersionIds, Guid CurrentVersionId);

/// <summary>What the sweeper read about a run, enforced again by the atomic claim of its terminal write.</summary>
/// <param name="Status">The status the sweeper saw: <c>queued</c> (branch b) or <c>running</c> (branch a).</param>
/// <param name="StaleBefore">The run is stale only while <c>COALESCE(started_at, queued_at)</c> is before this instant.</param>
public sealed record StaleClaim(GenerationRunStatus Status, DateTimeOffset StaleBefore);

/// <summary>
/// The only writer of run and episode state during a story generation (02.2.2-T3, plan §3.4), so both aggregates change
/// together: <c>StartAsync</c>, <c>RecordStageAsync</c>, <c>PauseAsync</c>, <c>CompleteAsync</c>, <c>FailAsync</c> and
/// <c>CancelAsync</c>. Stage events are emitted on transition only (the episode drops a repeat): at most seven per run,
/// and the optional single revision pass adds its own <c>revising</c> → <c>checking</c> → <c>reviewing</c> walk
/// (02.6.2-T1).
/// </summary>
/// <remarks>
/// Every call first checks that the run is not terminal and, for a story run, that it is the episode's active run
/// (02 CR-29): a late redelivery for run A is rejected before the episode is touched and is never applied to a later
/// generation by run B. The refund and the settlement run only after the call that moved the run committed, so the
/// terminal-status guard makes both idempotent per run.
/// </remarks>
public sealed partial class StoryGenerationProgress(
    IAppDbContext db,
    ICreditLedger ledger,
    IClock clock,
    StoryGenerationMetrics metrics,
    ILogger<StoryGenerationProgress> logger)
{
    /// <summary>The error code of a call for a run that already ended.</summary>
    public const string RunTerminalCode = "generation_run_terminal";

    /// <summary>The error code of a call for a run that is not the episode's active run.</summary>
    public const string RunNotActiveCode = "generation_run_not_active";

    /// <summary>The error code of a call for a run that does not exist.</summary>
    public const string RunNotFoundCode = "generation_run_not_found";

    private readonly List<GenerationStage> _stageCalls = [];

    // The ledger call after a terminal commit has its own budget: neither the run deadline nor the host shutdown may skip it (SEC-4).
    private static readonly TimeSpan LedgerTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The stages recorded through this instance, in call order (a repeat call is listed; the episode event is not repeated).</summary>
    public IReadOnlyList<GenerationStage> StageCalls => _stageCalls;

    /// <summary>
    /// Claims a queued run atomically (<c>UPDATE … WHERE status = 'queued'</c> inside the write transaction), starts it and
    /// records stage <c>writing</c>. A run that is already running, or ended, is rejected: a second job for the same run stops here.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>Success, or the guard's rejection.</returns>
    public async Task<ErrorOr<Success>> StartAsync(Guid runId, CancellationToken ct)
    {
        await using var write = await BeginAsync(ct).ConfigureAwait(false);

        var loaded = await LoadAsync(runId, RunClaim.Queued, recovery: false, ct).ConfigureAwait(false);
        if (loaded.IsError)
        {
            return Rejected(runId, loaded.FirstError);
        }

        var (run, episode) = loaded.Value;
        var now = clock.UtcNow;

        run.Start(now);

        var advanced = await AdvanceAsync(run, episode, GenerationStage.Writing, now, ct).ConfigureAwait(false);
        if (!advanced.IsError)
        {
            await write.CommitAsync(ct).ConfigureAwait(false);
        }

        return advanced;
    }

    /// <summary>Moves a running run to <paramref name="stage"/> and emits one stage event on a transition.</summary>
    /// <param name="runId">The run.</param>
    /// <param name="stage">The stage that starts.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>Success, or the guard's rejection (including a run the sweeper or an admin already ended).</returns>
    public async Task<ErrorOr<Success>> RecordStageAsync(Guid runId, GenerationStage stage, CancellationToken ct)
    {
        await using var write = await BeginAsync(ct).ConfigureAwait(false);

        var loaded = await LoadAsync(runId, RunClaim.Running, recovery: false, ct).ConfigureAwait(false);
        if (loaded.IsError)
        {
            return Rejected(runId, loaded.FirstError);
        }

        var (run, episode) = loaded.Value;

        var advanced = await AdvanceAsync(run, episode, stage, clock.UtcNow, ct).ConfigureAwait(false);
        if (!advanced.IsError)
        {
            await write.CommitAsync(ct).ConfigureAwait(false);
        }

        return advanced;
    }

    /// <summary>Counts one writer invalid-output retry on the run.</summary>
    /// <param name="runId">The run.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>Success, or the guard's rejection.</returns>
    public async Task<ErrorOr<Success>> RecordAttemptAsync(Guid runId, CancellationToken ct)
    {
        await using var write = await BeginAsync(ct).ConfigureAwait(false);

        var loaded = await LoadAsync(runId, RunClaim.Running, recovery: false, ct).ConfigureAwait(false);
        if (loaded.IsError)
        {
            return Rejected(runId, loaded.FirstError);
        }

        loaded.Value.Run.RecordAttempt(clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await write.CommitAsync(ct).ConfigureAwait(false);

        return Result.Success;
    }

    /// <summary>Writes the prompt version and the per-step model record (D5, D21).</summary>
    /// <param name="runId">The run.</param>
    /// <param name="promptVersion">The composite prompt version.</param>
    /// <param name="modelsJson">The model record, a JSON document.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>Success, or the guard's rejection.</returns>
    public async Task<ErrorOr<Success>> RecordProvenanceAsync(Guid runId, string promptVersion, string modelsJson, CancellationToken ct)
    {
        await using var write = await BeginAsync(ct).ConfigureAwait(false);

        var loaded = await LoadAsync(runId, RunClaim.Running, recovery: false, ct).ConfigureAwait(false);
        if (loaded.IsError)
        {
            return Rejected(runId, loaded.FirstError);
        }

        loaded.Value.Run.RecordProvenance(promptVersion, modelsJson, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await write.CommitAsync(ct).ConfigureAwait(false);

        return Result.Success;
    }

    /// <summary>
    /// Holds the run (TL T19): it goes back to <c>queued</c> with <paramref name="reason"/>, stops counting, and the
    /// episode stays <c>story_generating</c>. No refund: the reservation stays for the resume.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="reason">Why the run is held.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>Success, or the guard's rejection.</returns>
    public async Task<ErrorOr<Success>> PauseAsync(Guid runId, GenerationPauseReason reason, CancellationToken ct)
    {
        await using var write = await BeginAsync(ct).ConfigureAwait(false);

        var loaded = await LoadAsync(runId, RunClaim.Open, recovery: false, ct).ConfigureAwait(false);
        if (loaded.IsError)
        {
            return Rejected(runId, loaded.FirstError);
        }

        loaded.Value.Run.MarkPaused(reason, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await write.CommitAsync(ct).ConfigureAwait(false);

        return Result.Success;
    }

    /// <summary>
    /// Ends the run as <c>succeeded</c>, moves the episode to <c>story_review</c> and commits the story versions the caller
    /// added to the context, the run and the outbox <c>StoryGenerated</c> row in one transaction; then settles the
    /// reservation. <c>cost_usd</c> is the sum of the run reservation's usage records. A run that another actor already
    /// ended is rejected with nothing committed and nothing settled.
    /// </summary>
    /// <param name="completion">What the run saved.</param>
    /// <param name="ct">Cancels the write; use a token that is not the run deadline.</param>
    /// <returns>Success, or the guard's rejection.</returns>
    public async Task<ErrorOr<Success>> CompleteAsync(StoryCompletion completion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(completion);

        GenerationRun run;

        await using (var write = await BeginAsync(ct).ConfigureAwait(false))
        {
            var loaded = await LoadAsync(completion.GenerationRunId, RunClaim.Running, recovery: false, ct).ConfigureAwait(false);
            if (loaded.IsError)
            {
                return Rejected(completion.GenerationRunId, loaded.FirstError);
            }

            Episode? episode;
            (run, episode) = loaded.Value;
            var now = clock.UtcNow;
            var reservation = run.ReservationId;

            var cost = reservation is null
                ? 0m
                : await db.UsageRecords
                    .Where(record => record.ReservationId == reservation)
                    .SumAsync(record => record.CostUsd, ct)
                    .ConfigureAwait(false);

            run.Succeed(cost, completion.CurrentVersionId, null, now);

            if (episode is not null)
            {
                var moved = episode.CompleteStoryGeneration(run.Id, run.Trigger, completion.VersionIds, completion.CurrentVersionId, now);
                if (moved.IsError)
                {
                    return moved.Errors;
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await write.CommitAsync(ct).ConfigureAwait(false);
        }

        _stageCalls.Add(GenerationStage.Done);
        metrics.RunFinished(Wire(run.Kind), "succeeded", Wire(run.Trigger));

        await SettleAsync(run).ConfigureAwait(false);

        return Result.Success;
    }

    /// <summary>
    /// Ends the run as <c>failed</c>, returns the episode to its previous status (F04) and refunds the reservation after the
    /// write committed. A run that another actor already ended is rejected: it is never overwritten and never refunded twice.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="failure">The outcome.</param>
    /// <param name="ct">Cancels the write; use a token that is not the run deadline.</param>
    /// <param name="recovery">
    /// <see langword="true"/> for the sweeper: an episode that already moved on is left alone and only the run ends.
    /// </param>
    /// <param name="stale">
    /// The sweeper's claim: the status it read and the time before which the run counts as stale. The claim carries both, so a
    /// run that a worker started, restarted or finished since the sweeper's read is rejected instead of ended. Null for a job's own call.
    /// </param>
    /// <returns>Success, or the guard's rejection.</returns>
    public async Task<ErrorOr<Success>> FailAsync(
        Guid runId,
        StoryFailure failure,
        CancellationToken ct,
        bool recovery = false,
        StaleClaim? stale = null)
    {
        ArgumentNullException.ThrowIfNull(failure);

        GenerationRun run;

        await using (var write = await BeginAsync(ct).ConfigureAwait(false))
        {
            var loaded = await LoadAsync(runId, RunClaim.Open, recovery, ct, stale).ConfigureAwait(false);
            if (loaded.IsError)
            {
                return Rejected(runId, loaded.FirstError);
            }

            Episode? episode;
            (run, episode) = loaded.Value;
            var now = clock.UtcNow;

            run.Fail(failure.Code, failure.Detail, failure.CountsTowardLimit, now);

            if (episode is not null)
            {
                var code = GenerationErrorCode.Create(failure.Code);
                if (code.IsError)
                {
                    return code.Errors;
                }

                var moved = episode.FailStoryGeneration(run.Id, run.Trigger, run.PreviousEpisodeStatus ?? EpisodeStatus.Draft, code.Value, now);
                if (moved.IsError)
                {
                    return moved.Errors;
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await write.CommitAsync(ct).ConfigureAwait(false);
        }

        metrics.RunFinished(Wire(run.Kind), "failed", Wire(run.Trigger));

        await RefundAsync(run, failure.RefundReason).ConfigureAwait(false);

        return Result.Success;
    }

    /// <summary>
    /// Ends the run as <c>cancelled</c> and returns the episode to its previous status. With no <paramref name="errorCode"/>
    /// this is a user or admin cancel (counted, T1's <c>Cancel(now)</c>); with one it is the sweeper's cancel of a stuck
    /// queued run (CR-29: the code is set and <c>counts_toward_limit</c> is false). A run that another actor already ended is rejected.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="refundReason">The ledger reason.</param>
    /// <param name="errorCode">The D8 code, or <see langword="null"/> for a plain cancel.</param>
    /// <param name="detail">Safe detail for the sweeper's cancel.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <param name="recovery">See <see cref="FailAsync"/>.</param>
    /// <param name="stale">See <see cref="FailAsync"/>.</param>
    /// <returns>Success, or the guard's rejection.</returns>
    public async Task<ErrorOr<Success>> CancelAsync(
        Guid runId,
        string refundReason,
        string? errorCode,
        string? detail,
        CancellationToken ct,
        bool recovery = false,
        StaleClaim? stale = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refundReason);

        GenerationRun run;

        await using (var write = await BeginAsync(ct).ConfigureAwait(false))
        {
            var loaded = await LoadAsync(runId, RunClaim.Open, recovery, ct, stale).ConfigureAwait(false);
            if (loaded.IsError)
            {
                return Rejected(runId, loaded.FirstError);
            }

            Episode? episode;
            (run, episode) = loaded.Value;
            var now = clock.UtcNow;

            if (errorCode is null)
            {
                run.Cancel(now);
            }
            else
            {
                run.Cancel(errorCode, detail, countsTowardLimit: false, now);
            }

            if (episode is not null)
            {
                var moved = episode.CancelStoryGeneration(run.Id, run.Trigger, run.PreviousEpisodeStatus ?? EpisodeStatus.Draft, now);
                if (moved.IsError)
                {
                    return moved.Errors;
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await write.CommitAsync(ct).ConfigureAwait(false);
        }

        metrics.RunFinished(Wire(run.Kind), "cancelled", Wire(run.Trigger));

        await RefundAsync(run, refundReason).ConfigureAwait(false);

        return Result.Success;
    }

    /// <summary>
    /// Drops every unsaved change of the context, after a failed save: the run and episode are read again from the database
    /// before the failure is written, so a half-applied state is never saved.
    /// </summary>
    public void DiscardPendingChanges()
    {
        if (db is DbContext context)
        {
            context.ChangeTracker.Clear();
        }
    }

    private async Task<ErrorOr<Success>> AdvanceAsync(
        GenerationRun run,
        Episode? episode,
        GenerationStage stage,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var advanced = run.AdvanceStage(stage, now);
        if (advanced.IsError)
        {
            return advanced.Errors;
        }

        if (episode is not null)
        {
            var marked = episode.MarkGenerationStage(run.Id, stage, run.Trigger, now);
            if (marked.IsError)
            {
                return marked.Errors;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        _stageCalls.Add(stage);

        return Result.Success;
    }

    /// <summary>
    /// Claims the run for one write and loads it. The claim is a conditional <c>UPDATE … WHERE id AND status IN (…)</c> inside the
    /// caller's transaction: it takes the row lock, so a concurrent job, the sweeper or an admin ending the run waits for this
    /// write and then fails its own claim, and a run that is already terminal (or not in the expected status) is rejected on
    /// the committed state. The tracked run and episode are then reloaded, so no stale instance of the job's own context is trusted.
    /// </summary>
    private async Task<ErrorOr<(GenerationRun Run, Episode? Episode)>> LoadAsync(
        Guid runId,
        RunClaim claim,
        bool recovery,
        CancellationToken ct,
        StaleClaim? stale = null)
    {
        var now = clock.UtcNow;

        // The sweeper's claim names the one status it read and the stale time condition, both evaluated by the same
        // UPDATE under the row lock: a run a worker started (or restarted) since the sweeper's read no longer matches.
        if (stale is not null)
        {
            claim = stale.Status == GenerationRunStatus.Queued ? RunClaim.Queued : RunClaim.Running;
        }

        var allowQueued = claim is RunClaim.Queued or RunClaim.Open;
        var allowRunning = claim is RunClaim.Running or RunClaim.Open;
        var checkStale = stale is not null;
        var staleBefore = stale?.StaleBefore ?? now;

        var claimed = await db.GenerationRuns
            .Where(candidate => candidate.Id == runId
                && ((allowQueued && candidate.Status == GenerationRunStatus.Queued)
                    || (allowRunning && candidate.Status == GenerationRunStatus.Running))
                && (!checkStale || (candidate.StartedAt ?? candidate.QueuedAt) < staleBefore))
            .ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.UpdatedAt, now), ct)
            .ConfigureAwait(false);
        if (claimed == 0)
        {
            return await ClaimRejectedAsync(runId, ct).ConfigureAwait(false);
        }

        var run = await FreshAsync(db.GenerationRuns, runId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return Error.NotFound(RunNotFoundCode, "The generation run does not exist.");
        }

        // Only a story run moves its episode; the other kinds never do (plan §3.4).
        if (run.Kind != GenerationKind.Story || run.EpisodeId is not { } episodeId)
        {
            return (run, (Episode?)null);
        }

        var episode = await FreshAsync(db.Episodes, episodeId, ct).ConfigureAwait(false);
        var active = episode is { Status: EpisodeStatus.StoryGenerating }
            && !await db.GenerationRuns
                .AnyAsync(
                    other => other.EpisodeId == episodeId
                        && other.Kind == GenerationKind.Story
                        && other.Id != run.Id
                        && (other.Status == GenerationRunStatus.Queued || other.Status == GenerationRunStatus.Running),
                    ct)
                .ConfigureAwait(false);

        if (active)
        {
            return (run, episode);
        }

        // CR-29: the call is for a run that is not this episode's active generation. The episode is not touched.
        return recovery
            ? (run, (Episode?)null)
            : Error.Conflict(RunNotActiveCode, "The generation run is not the episode's active run.");
    }

    // The entity as committed: a tracked instance is re-read (identity resolution would return its old values), an untracked one is loaded.
    private async Task<TEntity?> FreshAsync<TEntity>(DbSet<TEntity> set, Guid id, CancellationToken ct)
        where TEntity : Entity
    {
        var context = Context;
        var tracked = context.ChangeTracker.Entries<TEntity>().FirstOrDefault(entry => entry.Entity.Id == id);
        if (tracked is null)
        {
            return await set.SingleOrDefaultAsync(candidate => candidate.Id == id, ct).ConfigureAwait(false);
        }

        await tracked.ReloadAsync(ct).ConfigureAwait(false);

        return tracked.State == EntityState.Detached ? null : tracked.Entity;
    }

    // Why a claim matched no row, read from the committed state: missing, already ended, or not in the status the call needs.
    private async Task<ErrorOr<(GenerationRun Run, Episode? Episode)>> ClaimRejectedAsync(Guid runId, CancellationToken ct)
    {
        var status = await db.GenerationRuns
            .AsNoTracking()
            .Where(candidate => candidate.Id == runId)
            .Select(candidate => (GenerationRunStatus?)candidate.Status)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return status switch
        {
            null => Error.NotFound(RunNotFoundCode, "The generation run does not exist."),
            GenerationRunStatus.Succeeded or GenerationRunStatus.Failed or GenerationRunStatus.Cancelled =>
                Error.Conflict(RunTerminalCode, "The generation run already ended."),
            _ => Error.Conflict(RunNotActiveCode, "The generation run is not in the status this call needs."),
        };
    }

    private DbContext Context =>
        db as DbContext ?? throw new InvalidOperationException("The progress writer needs the EF Core context behind IAppDbContext.");

    private async Task<WriteScope> BeginAsync(CancellationToken ct)
    {
        // An ambient transaction (the unit of work) already spans the call; otherwise the call owns one, so the claim's row lock
        // is held until the terminal state, the episode and the outbox rows are committed together.
        if (db.Database.CurrentTransaction is not null)
        {
            return new WriteScope(null);
        }

        var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        return new WriteScope(transaction);
    }

    // Runs on its own 10 s budget, not on the run's deadline token or the host-shutdown token, so neither skips it after the
    // terminal commit. No reconciliation job exists to repair a skipped call: a debiting ledger (12.1.2) owns that gap.
    private async Task SettleAsync(GenerationRun run)
    {
        if (run.ReservationId is not { } reservation)
        {
            return;
        }

        using var budget = new CancellationTokenSource(LedgerTimeout);

        try
        {
            var settled = await ledger.SettleAsync(new ReservationId(reservation), budget.Token).ConfigureAwait(false);
            if (settled.IsError)
            {
                LogLedgerFailed(run.Id, "settle", settled.FirstError.Code);
            }
        }
        catch (Exception exception)
        {
            // Type only: a provider or database message can echo content (D19).
            LogLedgerFailed(run.Id, "settle", exception.GetType().Name);
        }
    }

    private async Task RefundAsync(GenerationRun run, string reason)
    {
        if (run.ReservationId is not { } reservation)
        {
            return;
        }

        using var budget = new CancellationTokenSource(LedgerTimeout);

        try
        {
            var refunded = await ledger.RefundAsync(new ReservationId(reservation), reason, budget.Token).ConfigureAwait(false);
            if (refunded.IsError)
            {
                LogLedgerFailed(run.Id, "refund", refunded.FirstError.Code);
            }
        }
        catch (Exception exception)
        {
            LogLedgerFailed(run.Id, "refund", exception.GetType().Name);
        }
    }

    private enum RunClaim
    {
        Queued,
        Running,
        Open,
    }

    // The write transaction of one call; disposing it without a commit rolls the claim and every pending write back.
    private sealed class WriteScope(IDbContextTransaction? transaction) : IAsyncDisposable
    {
        public Task CommitAsync(CancellationToken ct) => transaction is null ? Task.CompletedTask : transaction.CommitAsync(ct);

        public ValueTask DisposeAsync() => transaction is null ? ValueTask.CompletedTask : transaction.DisposeAsync();
    }

    private ErrorOr<Success> Rejected(Guid runId, Error error)
    {
        LogRejected(runId, error.Code);

        return error;
    }

    private static string Wire<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    [LoggerMessage(
        EventId = 6100,
        Level = LogLevel.Warning,
        Message = "generation_progress_rejected runId={RunId} code={Code}")]
    private partial void LogRejected(Guid runId, string code);

    [LoggerMessage(
        EventId = 6101,
        Level = LogLevel.Warning,
        Message = "generation_ledger_failed runId={RunId} operation={Operation} code={Code}")]
    private partial void LogLedgerFailed(Guid runId, string operation, string code);
}
