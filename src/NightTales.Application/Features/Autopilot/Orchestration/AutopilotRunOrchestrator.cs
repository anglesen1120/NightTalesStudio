using System.Diagnostics;
using System.Linq.Expressions;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Orchestration;

/// <summary>
/// The run state machine's driver (02.8.3 plan §3.5, §7.1/§7.2): one handler call per
/// <see cref="AdvanceAsync"/>, one DI scope pinned to the run's owner per call, one save guarded by the row's
/// concurrency token.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope discipline.</b> <c>ISystemContext.For(run.UserId)</c> runs before the first <c>DbContext</c> use in
/// the scope, the tenant filter stays on, and <c>IgnoreQueryFilters</c> is never used here (09 README D10; the
/// tick's cross-user read is the allow-listed <see cref="ISystemWorkQuery"/> pair scan). A run whose owner is
/// unknown therefore fails loudly instead of reading other rows.
/// </para>
/// <para>
/// <b>Compare-and-set only.</b> The row is saved once, under the <c>xmin</c> token the EF configuration puts on
/// it; a <see cref="DbUpdateConcurrencyException"/> means another writer won and is answered
/// <see cref="StepResultKind.Idle"/> — the tracked entity disappears with the scope, so no half-applied change
/// survives.
/// </para>
/// <para>
/// <b>Two guards of the tick.</b> A producing run is skipped this tick when another producing run of the same
/// series is not later than it (<see cref="AutopilotRunOptions.MaxProducingRunsPerSeries"/>; the later run is
/// the one that steps back, so the older run is never starved), and a producing run whose
/// <see cref="AutopilotRun.LastTransitionAt"/> is older than <see cref="AutopilotRunOptions.StuckRunMinutes"/>
/// while its handler keeps answering idle/retry is logged once per tick (no state change).
/// </para>
/// <para>
/// D19: every log line and metric tag carries ids, state/step names, closed codes and durations only.
/// </para>
/// </remarks>
public sealed partial class AutopilotRunOrchestrator(
    IServiceScopeFactory scopes,
    ISystemWorkQuery systemWork,
    AutopilotRunOptions options,
    AutopilotRunMetrics metrics,
    IClock clock,
    ILogger<AutopilotRunOrchestrator> logger) : IAutopilotRunOrchestrator
{
    /// <summary>The code a call answers when the owner id is missing (the row would be unreadable).</summary>
    public const string UserRequiredCode = ErrorCodes.AutopilotUserRequired;

    /// <summary>The code a call answers when the run is not visible to its owner.</summary>
    public const string RunNotFoundCode = ErrorCodes.AutopilotRunNotFound;

    /// <summary>The code a call answers when the handler or the rows it needs failed.</summary>
    public const string StepFailedCode = ErrorCodes.AutopilotStepFailed;

    /// <summary>The code recorded when the save lost the race and nothing was written.</summary>
    public const string ConcurrencyLostCode = "concurrency_lost";

    /// <summary>The code recorded when another producing run of the series is not later than this one.</summary>
    public const string ProducingRunAheadCode = "producing_run_ahead";

    /// <inheritdoc />
    public Task<ErrorOr<AdvanceResult>> AdvanceAsync(Guid runId, Guid userId, CancellationToken ct) =>
        AdvanceCoreAsync(runId, userId, markChecked: false, ct);

    /// <summary>
    /// One step of one run.
    /// </summary>
    /// <remarks>
    /// <b>Why the check stamp is opt-in.</b> The row's <c>xmin</c> is its compare-and-set token, so a write this call
    /// does not need is a write that can make a concurrent tick's save lose the race. The rotation stamp therefore
    /// belongs to the due scan alone (<see cref="AdvanceDueAsync"/>); an outbox-driven or direct
    /// <see cref="AdvanceAsync"/> leaves the row exactly as the handler did.
    /// <para>
    /// <b>The stamp does bump <c>xmin</c>.</b> The statement is a plain <c>UPDATE</c>, so a concurrent outbox-driven
    /// <see cref="AdvanceAsync"/> that loaded the run just before can lose its compare-and-set and answer
    /// <c>concurrency_lost</c>. That is benign: the loser writes nothing, the scan already did the run's move or
    /// stamped it, and the event is re-polled. The stamp itself is what keeps a poison run (one whose handler keeps
    /// throwing or whose series is not visible) from holding the head of every pass (D-8).
    /// </para>
    /// </remarks>
    /// <param name="runId">The run.</param>
    /// <param name="userId">Its owner, from the event or the scan.</param>
    /// <param name="markChecked">
    /// Stamp <c>updated_at</c> when nothing moved, so the owner's due scan rotates (PF-1, SEC-2).
    /// </param>
    /// <param name="ct">Cancels the step and its save.</param>
    /// <returns>What the step did, or why it could not.</returns>
    private async Task<ErrorOr<AdvanceResult>> AdvanceCoreAsync(
        Guid runId,
        Guid userId,
        bool markChecked,
        CancellationToken ct)
    {
        if (userId == Guid.Empty)
        {
            LogUserRequired(runId);

            return Error.Validation(UserRequiredCode, "An autopilot run needs its owner (09 README D10).");
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();

            // Before any DbContext in this scope: the filter compares every user-owned row against this id.
            using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(userId);

            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var handlers = AutopilotStepHandlers.Index(scope.ServiceProvider.GetServices<IAutopilotStepHandler>());

            var run = await db.AutopilotRuns
                .SingleOrDefaultAsync(candidate => candidate.Id == runId, ct)
                .ConfigureAwait(false);
            if (run is null)
            {
                LogRunNotFound(runId, userId);

                return Error.NotFound(RunNotFoundCode, "The run does not exist for this owner.");
            }

            if (AutopilotRunStateMachine.IsTerminal(run.State))
            {
                return new AdvanceResult(run.State, StepResultKind.Idle, null);
            }

            var now = clock.UtcNow;

            var series = await db.Series
                .SingleOrDefaultAsync(candidate => candidate.Id == run.SeriesId, ct)
                .ConfigureAwait(false);
            if (series is null)
            {
                LogSeriesMissing(runId, run.SeriesId, run.State);

                // A run whose series is not visible can never move, so it must not keep the head of every pass: the
                // scan stamps it like the other idle exits and it rotates behind the rows nobody has looked at (D-8).
                await NoteCheckedAsync(db, runId, now, markChecked, ct).ConfigureAwait(false);

                return Error.Failure(StepFailedCode, "The run's series is not visible to its owner.");
            }

            var parsed = AutopilotSettingsValidator.Validate(series.AutopilotSettings);
            if (parsed.IsError)
            {
                // A settings document that fails validation is a skip, not a pause (task file §Failure handling):
                // the series' planner/owner must fix it and the tick re-reads it next time.
                LogSettingsInvalid(runId, run.SeriesId, parsed.FirstError.Code);
                await NoteCheckedAsync(db, runId, now, markChecked, ct).ConfigureAwait(false);

                return new AdvanceResult(run.State, StepResultKind.Idle, ErrorCodes.AutopilotSettingsInvalid);
            }

            if (!handlers.TryGetValue(run.State, out var handler))
            {
                // A non-terminal state no handler claims (the aggregate's own states aside) keeps its place in the
                // rotation but cannot move here.
                await NoteCheckedAsync(db, runId, now, markChecked, ct).ConfigureAwait(false);

                return new AdvanceResult(run.State, StepResultKind.Idle, null);
            }

            var originalState = run.State;

            if (AutopilotRunStateMachine.IsProducing(run.State)
                && await IsBehindAnotherProducingRunAsync(db, run, ct).ConfigureAwait(false))
            {
                metrics.ConcurrencyViolation();
                LogConcurrencyViolation(run.Id, run.SeriesId, now);
                await NoteCheckedAsync(db, runId, now, markChecked, ct).ConfigureAwait(false);

                return new AdvanceResult(originalState, StepResultKind.Idle, ProducingRunAheadCode);
            }

            var timingOptions = options.ToTimingOptions();
            var context = new AutopilotStepContext(
                run,
                series,
                parsed.Value,
                AutopilotTiming.Compute(run.SlotAt, parsed.Value, timingOptions),
                timingOptions,
                now);

            var stopwatch = Stopwatch.StartNew();
            StepResult result;
            try
            {
                result = await handler.HandleAsync(context, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Type only: a handler's message could quote a story, a brief or a source URL (D19).
                LogStepFailed(runId, originalState, exception.GetType().Name);

                // A poison run would otherwise keep its updated_at and the head of its pass every tick, starving the
                // rows behind it once enough of them throw (D-8): the scan stamps it exactly as it stamps an idle
                // exit, so it rotates to the back. The stamp bumps xmin, which a concurrent outbox-driven advance
                // answers as concurrency_lost — a benign no-op the event re-polls.
                await NoteCheckedAsync(db, runId, now, markChecked, ct).ConfigureAwait(false);

                return Error.Failure(StepFailedCode, "The autopilot step failed; the next tick retries.");
            }
            finally
            {
                stopwatch.Stop();
                metrics.StepDuration(AutopilotRunMetrics.Wire(handler.State), stopwatch.Elapsed);
            }

            switch (result.Kind)
            {
                case StepResultKind.Advanced:
                case StepResultKind.Stopped:
                    try
                    {
                        await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        LogConcurrencyLost(runId, originalState);

                        return new AdvanceResult(originalState, StepResultKind.Idle, ConcurrencyLostCode);
                    }

                    if (result.Kind == StepResultKind.Stopped)
                    {
                        metrics.RunStopped(run.State, run.Outcome is { } outcome
                            ? AutopilotRunMetrics.Wire(outcome.ToString())
                            : result.Code ?? AutopilotRunMetrics.OtherTagValue);
                    }

                    return new AdvanceResult(run.State, result.Kind, result.Code);

                default:
                    LogIfStuck(run, result.Kind, now);
                    await NoteCheckedAsync(db, runId, now, markChecked, ct).ConfigureAwait(false);

                    return new AdvanceResult(run.State, result.Kind, result.Code);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogStepThrew(runId, exception.GetType().Name);

            return Error.Failure(StepFailedCode, "The autopilot step failed; the next tick retries.");
        }
    }

    /// <inheritdoc />
    public Task<ErrorOr<AdvanceResult>> AdvanceWhileProgressingAsync(
        Guid runId,
        Guid userId,
        int maxSteps,
        CancellationToken ct) =>
        AdvanceWhileProgressingCoreAsync(runId, userId, maxSteps, markChecked: false, ct);

    /// <summary>
    /// Walks up to <paramref name="maxSteps"/> steps of one run, stopping at the first step that did not advance.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="userId">Its owner.</param>
    /// <param name="maxSteps">The chain's budget.</param>
    /// <param name="markChecked">Stamp <c>updated_at</c> when the chain ends without moving (the due scan only).</param>
    /// <param name="ct">Cancels the steps and their saves.</param>
    /// <returns>The chain's last result, or the first error.</returns>
    private async Task<ErrorOr<AdvanceResult>> AdvanceWhileProgressingCoreAsync(
        Guid runId,
        Guid userId,
        int maxSteps,
        bool markChecked,
        CancellationToken ct)
    {
        var budget = Math.Max(maxSteps, 1);
        AdvanceResult? last = null;

        for (var step = 0; step < budget; step++)
        {
            var result = await AdvanceCoreAsync(runId, userId, markChecked, ct).ConfigureAwait(false);
            if (result.IsError)
            {
                return result.Errors;
            }

            last = result.Value;

            if (last.Kind != StepResultKind.Advanced)
            {
                break;
            }
        }

        return last ?? new AdvanceResult(AutopilotRunState.Planned, StepResultKind.Idle, null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Two passes, actionable first.</b> The scan goes through the ordered
    /// <see cref="ISystemWorkQuery"/> overload (the allow-listed cross-user read of 09 README D10) in two passes:
    /// the states whose next move is the tick's own work (<see cref="TickDriven"/>) get the whole budget first, and
    /// whatever is left goes to the states that wait on an external event and only need a periodic re-read
    /// (<see cref="ExternalWait"/>). A run that needs the tick is therefore never queued behind an idle-waiting row,
    /// whatever the tenant mix (review findings PF-1, SEC-2).
    /// </para>
    /// <para>
    /// <b>A rotation, not a heap order.</b> Each pass is ordered by <c>updated_at</c> then id, and the scan re-stamps
    /// <c>updated_at</c> when a run had nothing to do (the plain <see cref="AdvanceAsync"/> does not write: its row's
    /// <c>xmin</c> must stay untouched for a concurrent writer). A row that cannot progress this tick therefore moves
    /// behind every row that has not been looked at, and the id tiebreak keeps the order total.
    /// </para>
    /// <para>
    /// <b>What the rotation promises.</b> While the actionable pass leaves budget, every due row of both classes is
    /// reached within <c>ceil(class size / budget)</c> ticks. When the actionable rows alone fill the budget the
    /// waiting pass gets nothing that tick — those runs are being overtaken by work that must happen, which is the
    /// intended priority — and the scan says so with <c>autopilot_tick_truncated</c>.
    /// </para>
    /// <para>
    /// Every run is handled in its own call and a failure is logged and skipped, so one bad run never stops the
    /// others. A pass that returns exactly the budget it asked for is logged
    /// (<c>autopilot_tick_truncated</c>), which is what tells "the budget was exactly full" from "the budget could
    /// not keep up" (PF-3).
    /// </para>
    /// </remarks>
    /// <param name="now">The tick's clock reading.</param>
    /// <param name="max">The budget of runs this tick.</param>
    /// <param name="ct">Cancels the scan and every run.</param>
    /// <returns>The due runs handled without an error; a failing run is logged and skipped.</returns>
    public async Task<int> AdvanceDueAsync(DateTimeOffset now, int max, CancellationToken ct)
    {
        if (max <= 0)
        {
            return 0;
        }

        var due = new List<(Guid Id, Guid UserId)>();

        await foreach (var pair in systemWork
            .PendingAsync<AutopilotRun, DateTimeOffset>(TickDriven(now), run => run.UpdatedAt, max, ct)
            .ConfigureAwait(false))
        {
            due.Add(pair);
        }

        var actionable = due.Count;
        var remaining = max - actionable;

        if (remaining > 0)
        {
            await foreach (var pair in systemWork
                .PendingAsync<AutopilotRun, DateTimeOffset>(ExternalWait(now), run => run.UpdatedAt, remaining, ct)
                .ConfigureAwait(false))
            {
                due.Add(pair);
            }
        }

        // Either pass returning exactly the budget it asked for means the queue, not the clock or an empty set,
        // ended the scan (PF-3).
        if (actionable == max || (remaining > 0 && due.Count - actionable == remaining))
        {
            LogDueTruncated(now, due.Count, max);
        }

        var advanced = 0;

        foreach (var (runId, userId) in due)
        {
            try
            {
                var result = await AdvanceWhileProgressingCoreAsync(
                        runId,
                        userId,
                        options.MaxStepsPerTick,
                        markChecked: true,
                        ct)
                    .ConfigureAwait(false);
                if (result.IsError)
                {
                    LogDueRunFailed(runId, result.FirstError.Code);
                    continue;
                }

                advanced++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogDueRunThrew(runId, exception.GetType().Name);
            }
        }

        LogDueCompleted(due.Count, advanced);

        return advanced;
    }

    /// <summary>
    /// The tick's due-run predicate: a row whose <c>start_at</c> has arrived, that is not terminal and that is
    /// not <see cref="AutopilotRunState.Paused"/> (the state values are spelled out because an expression tree
    /// cannot call <see cref="AutopilotRunStateMachine.IsTerminal"/>; the migration's
    /// <c>(state, start_at)</c> index serves the scan).
    /// </summary>
    /// <param name="now">The tick's clock reading.</param>
    /// <returns>The filter for <see cref="ISystemWorkQuery.PendingAsync{T, TKey}"/>.</returns>
    public static System.Linq.Expressions.Expression<Func<AutopilotRun, bool>> Due(DateTimeOffset now) =>
        run => run.StartAt <= now
            && run.State != AutopilotRunState.Published
            && run.State != AutopilotRunState.PublishedPrivateLocked
            && run.State != AutopilotRunState.GateFailed
            && run.State != AutopilotRunState.CancelledByOwner
            && run.State != AutopilotRunState.Failed
            && run.State != AutopilotRunState.Paused;

    /// <summary>
    /// The due rows whose next move is the tick's own work: the state machine and the in-flight generation the step
    /// polls are the only thing that advances them, so they are served before anything else.
    /// </summary>
    /// <param name="now">The tick's clock reading.</param>
    /// <returns>The filter of the first scan pass.</returns>
    public static System.Linq.Expressions.Expression<Func<AutopilotRun, bool>> TickDriven(DateTimeOffset now) =>
        All(Due(now), run => run.State == AutopilotRunState.Planned
            || run.State == AutopilotRunState.IdeaPicked
            || run.State == AutopilotRunState.Researching
            || run.State == AutopilotRunState.StoryGenerating
            || run.State == AutopilotRunState.StoryGate
            || run.State == AutopilotRunState.Rendering
            || run.State == AutopilotRunState.VideoGate);

    /// <summary>
    /// The due rows that wait on an external event — the owner's accept or answer, consent, the veto notice, the
    /// publication row — and need only a periodic re-read. They are served with whatever budget the actionable pass
    /// leaves, so an owner who never answers cannot starve a run that needs the tick.
    /// </summary>
    /// <param name="now">The tick's clock reading.</param>
    /// <returns>The filter of the second scan pass.</returns>
    public static System.Linq.Expressions.Expression<Func<AutopilotRun, bool>> ExternalWait(DateTimeOffset now) =>
        All(Due(now), run => run.State == AutopilotRunState.AwaitingAuthorization
            || run.State == AutopilotRunState.WaitingOwner
            || run.State == AutopilotRunState.PublicationCreated
            || run.State == AutopilotRunState.VetoWindow
            || run.State == AutopilotRunState.Scheduled);

    /// <summary>
    /// Whether another producing run of the same series is not later than <paramref name="run"/> — "not later"
    /// meaning an earlier <see cref="AutopilotRun.StartAt"/>, or the same instant and a lower id (the v7 ids
    /// order with insertion). The count of those rows is compared with
    /// <see cref="AutopilotRunOptions.MaxProducingRunsPerSeries"/>.
    /// </summary>
    private async Task<bool> IsBehindAnotherProducingRunAsync(
        IAppDbContext db,
        AutopilotRun run,
        CancellationToken ct)
    {
        var others = await db.AutopilotRuns
            .AsNoTracking()
            .Where(candidate => candidate.SeriesId == run.SeriesId
                && candidate.Id != run.Id
                && (candidate.State == AutopilotRunState.Planned
                    || candidate.State == AutopilotRunState.IdeaPicked
                    || candidate.State == AutopilotRunState.Researching
                    || candidate.State == AutopilotRunState.StoryGenerating
                    || candidate.State == AutopilotRunState.StoryGate
                    || candidate.State == AutopilotRunState.WaitingOwner
                    || candidate.State == AutopilotRunState.Rendering
                    || candidate.State == AutopilotRunState.VideoGate
                    || candidate.State == AutopilotRunState.PublicationCreated))
            .Select(candidate => new { candidate.Id, candidate.StartAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var notLater = others.Count(other =>
            other.StartAt < run.StartAt
            || (other.StartAt == run.StartAt && other.Id.CompareTo(run.Id) < 0));

        return notLater >= Math.Max(options.MaxProducingRunsPerSeries, 1);
    }

    private void LogIfStuck(AutopilotRun run, StepResultKind kind, DateTimeOffset now)
    {
        if (!AutopilotRunStateMachine.IsProducing(run.State)
            || now - run.LastTransitionAt < TimeSpan.FromMinutes(options.StuckRunMinutes))
        {
            return;
        }

        LogRunStuck(run.Id, run.SeriesId, run.State, kind, (long)(now - run.LastTransitionAt).TotalMinutes);
    }

    /// <summary>
    /// Stamps "the tick looked at this run and had nothing to do": one <c>UPDATE</c> of <c>updated_at</c>, no state
    /// change and no other tracked entity flushed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This write is what makes the due scan a rotation (PF-1, SEC-2): the scan orders by <c>updated_at</c>, so a run
    /// that answered idle moves behind every due run that has not been looked at, and a healthy run is reached
    /// within <c>ceil(due / budget)</c> ticks instead of never. Without it the same idle head would fill the budget
    /// every tick and a run that needs the tick would reach <c>slot_missed</c>.
    /// </para>
    /// <para>
    /// <see cref="AutopilotRun.LastTransitionAt"/> is deliberately untouched: the stuck-run log and the whole state
    /// machine read it as "when the run last moved", and a check is not a move.
    /// </para>
    /// <para>
    /// The statement bypasses the change tracker on purpose: a step that returned idle may have left tracked
    /// entities in the scope, and the tick must not commit them. A write that fails is logged and ignored — the row
    /// keeps its place in the queue, which costs one tick, not the run.
    /// </para>
    /// </remarks>
    private async Task NoteCheckedAsync(
        IAppDbContext db,
        Guid runId,
        DateTimeOffset now,
        bool markChecked,
        CancellationToken ct)
    {
        // Only the due scan rotates: see AdvanceCoreAsync's remark on the xmin token.
        if (!markChecked)
        {
            return;
        }

        try
        {
            await db.AutopilotRuns
                .Where(candidate => candidate.Id == runId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.UpdatedAt, now), ct)
                .ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            LogCheckStampFailed(runId, exception.GetType().Name);
        }
    }

    /// <summary>One predicate of both <paramref name="left"/> and <paramref name="right"/>, over one parameter.</summary>
    private static Expression<Func<AutopilotRun, bool>> All(
        Expression<Func<AutopilotRun, bool>> left,
        Expression<Func<AutopilotRun, bool>> right)
    {
        var parameter = left.Parameters[0];
        var body = Expression.AndAlso(left.Body, new Rebind(right.Parameters[0], parameter).Visit(right.Body)!);

        return Expression.Lambda<Func<AutopilotRun, bool>>(body, parameter);
    }

    /// <summary>Replaces one lambda parameter with another, so two predicates can share one expression.</summary>
    private sealed class Rebind(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == from ? to : base.VisitParameter(node);
    }

    [LoggerMessage(
        EventId = 6610,
        Level = LogLevel.Warning,
        Message = "autopilot_tick_truncated at={At} scanned={Scanned} max={Max}")]
    private partial void LogDueTruncated(DateTimeOffset at, int scanned, int max);

    [LoggerMessage(
        EventId = 6611,
        Level = LogLevel.Warning,
        Message = "autopilot_run_check_stamp_failed runId={RunId} exceptionType={ExceptionType}")]
    private partial void LogCheckStampFailed(Guid runId, string exceptionType);

    [LoggerMessage(
        EventId = 6690,
        Level = LogLevel.Warning,
        Message = "autopilot_run_user_required runId={RunId}")]
    private partial void LogUserRequired(Guid runId);

    [LoggerMessage(
        EventId = 6511,
        Level = LogLevel.Warning,
        Message = "autopilot_run_not_found runId={RunId} userId={UserId}")]
    private partial void LogRunNotFound(Guid runId, Guid userId);

    [LoggerMessage(
        EventId = 6512,
        Level = LogLevel.Warning,
        Message = "autopilot_run_series_missing runId={RunId} seriesId={SeriesId} state={State}")]
    private partial void LogSeriesMissing(Guid runId, Guid seriesId, AutopilotRunState state);

    [LoggerMessage(
        EventId = 6691,
        Level = LogLevel.Warning,
        Message = "autopilot_run_settings_invalid runId={RunId} seriesId={SeriesId} code={Code}")]
    private partial void LogSettingsInvalid(Guid runId, Guid seriesId, string code);

    [LoggerMessage(
        EventId = 6692,
        Level = LogLevel.Warning,
        Message = "autopilot_run_step_failed runId={RunId} state={State} exceptionType={ExceptionType}")]
    private partial void LogStepFailed(Guid runId, AutopilotRunState state, string exceptionType);

    [LoggerMessage(
        EventId = 6693,
        Level = LogLevel.Warning,
        Message = "autopilot_run_concurrency_violation runId={RunId} seriesId={SeriesId} at={At}")]
    private partial void LogConcurrencyViolation(Guid runId, Guid seriesId, DateTimeOffset at);

    [LoggerMessage(
        EventId = 6516,
        Level = LogLevel.Information,
        Message = "autopilot_run_concurrency_lost runId={RunId} state={State}")]
    private partial void LogConcurrencyLost(Guid runId, AutopilotRunState state);

    [LoggerMessage(
        EventId = 6517,
        Level = LogLevel.Warning,
        Message = "autopilot_run_stuck runId={RunId} seriesId={SeriesId} state={State} result={Result} minutes={Minutes}")]
    private partial void LogRunStuck(Guid runId, Guid seriesId, AutopilotRunState state, StepResultKind result, long minutes);

    [LoggerMessage(
        EventId = 6518,
        Level = LogLevel.Warning,
        Message = "autopilot_tick_run_failed runId={RunId} code={Code}")]
    private partial void LogDueRunFailed(Guid runId, string code);

    [LoggerMessage(
        EventId = 6519,
        Level = LogLevel.Warning,
        Message = "autopilot_tick_run_threw runId={RunId} exceptionType={ExceptionType}")]
    private partial void LogDueRunThrew(Guid runId, string exceptionType);

    [LoggerMessage(
        EventId = 6534,
        Level = LogLevel.Warning,
        Message = "autopilot_run_step_threw runId={RunId} exceptionType={ExceptionType}")]
    private partial void LogStepThrew(Guid runId, string exceptionType);

    [LoggerMessage(
        EventId = 6535,
        Level = LogLevel.Information,
        Message = "autopilot_tick_completed due={Due} advanced={Advanced}")]
    private partial void LogDueCompleted(int due, int advanced);
}

/// <summary>
/// The one place the run-state → handler map is built: one handler per source state, and two handlers claiming
/// the same state is a code error that fails the start-up (02.8.3 plan §3.5, task file step 5).
/// </summary>
public static class AutopilotStepHandlers
{
    /// <summary>Indexes <paramref name="handlers"/> by the state each advances out of.</summary>
    /// <param name="handlers">The handlers of the current scope.</param>
    /// <returns>One handler per state.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handlers"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Two handlers claim the same state.</exception>
    public static IReadOnlyDictionary<AutopilotRunState, IAutopilotStepHandler> Index(
        IEnumerable<IAutopilotStepHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        var index = new Dictionary<AutopilotRunState, IAutopilotStepHandler>();

        foreach (var handler in handlers)
        {
            if (!index.TryAdd(handler.State, handler))
            {
                throw new InvalidOperationException(
                    $"Two autopilot step handlers claim the state {handler.State}: "
                    + $"{index[handler.State].GetType().FullName} and {handler.GetType().FullName}. "
                    + "The plan §3.5 machine has exactly one handler per source state.");
            }
        }

        return index;
    }
}
