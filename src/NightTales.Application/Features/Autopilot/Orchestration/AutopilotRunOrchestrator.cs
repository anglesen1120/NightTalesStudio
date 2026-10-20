using System.Diagnostics;
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
    public async Task<ErrorOr<AdvanceResult>> AdvanceAsync(Guid runId, Guid userId, CancellationToken ct)
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

            var series = await db.Series
                .SingleOrDefaultAsync(candidate => candidate.Id == run.SeriesId, ct)
                .ConfigureAwait(false);
            if (series is null)
            {
                LogSeriesMissing(runId, run.SeriesId, run.State);

                return Error.Failure(StepFailedCode, "The run's series is not visible to its owner.");
            }

            var parsed = AutopilotSettingsValidator.Validate(series.AutopilotSettings);
            if (parsed.IsError)
            {
                // A settings document that fails validation is a skip, not a pause (task file §Failure handling):
                // the series' planner/owner must fix it and the tick re-reads it next time.
                LogSettingsInvalid(runId, run.SeriesId, parsed.FirstError.Code);

                return new AdvanceResult(run.State, StepResultKind.Idle, ErrorCodes.AutopilotSettingsInvalid);
            }

            if (!handlers.TryGetValue(run.State, out var handler))
            {
                // Waiting states the orchestrator does not own (awaiting_authorization, paused, …) stand idle
                // until their own event or the owner's resume drives them.
                return new AdvanceResult(run.State, StepResultKind.Idle, null);
            }

            var originalState = run.State;
            var now = clock.UtcNow;

            if (AutopilotRunStateMachine.IsProducing(run.State)
                && await IsBehindAnotherProducingRunAsync(db, run, ct).ConfigureAwait(false))
            {
                metrics.ConcurrencyViolation();
                LogConcurrencyViolation(run.Id, run.SeriesId, now);

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
    public async Task<ErrorOr<AdvanceResult>> AdvanceWhileProgressingAsync(
        Guid runId,
        Guid userId,
        int maxSteps,
        CancellationToken ct)
    {
        var budget = Math.Max(maxSteps, 1);
        AdvanceResult? last = null;

        for (var step = 0; step < budget; step++)
        {
            var result = await AdvanceAsync(runId, userId, ct).ConfigureAwait(false);
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
    /// The due scan goes through <see cref="ISystemWorkQuery.PendingAsync{T}"/> (the allow-listed cross-user
    /// read of 09 README D10): the predicate is "not terminal and <c>start_at &lt;= now</c>", plus "not paused"
    /// because only the owner's resume drives a paused run. The port has no ordering member, so this takes the
    /// batch in whatever order the provider returns; a run left past <paramref name="max"/> is picked up by the
    /// next tick fifteen minutes later. Every run is handled in its own call and a failure is logged and skipped,
    /// so one bad run never stops the others.
    /// </remarks>
    public async Task<int> AdvanceDueAsync(DateTimeOffset now, int max, CancellationToken ct)
    {
        if (max <= 0)
        {
            return 0;
        }

        var due = new List<(Guid Id, Guid UserId)>();

        await foreach (var pair in systemWork.PendingAsync<AutopilotRun>(Due(now), max, ct).ConfigureAwait(false))
        {
            due.Add(pair);
        }

        var advanced = 0;

        foreach (var (runId, userId) in due)
        {
            try
            {
                var result = await AdvanceWhileProgressingAsync(runId, userId, options.MaxStepsPerTick, ct)
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
    /// <returns>The filter for <see cref="ISystemWorkQuery.PendingAsync{T}"/>.</returns>
    public static System.Linq.Expressions.Expression<Func<AutopilotRun, bool>> Due(DateTimeOffset now) =>
        run => run.StartAt <= now
            && run.State != AutopilotRunState.Published
            && run.State != AutopilotRunState.PublishedPrivateLocked
            && run.State != AutopilotRunState.GateFailed
            && run.State != AutopilotRunState.CancelledByOwner
            && run.State != AutopilotRunState.Failed
            && run.State != AutopilotRunState.Paused;

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

    [LoggerMessage(
        EventId = 6510,
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
        EventId = 6513,
        Level = LogLevel.Warning,
        Message = "autopilot_run_settings_invalid runId={RunId} seriesId={SeriesId} code={Code}")]
    private partial void LogSettingsInvalid(Guid runId, Guid seriesId, string code);

    [LoggerMessage(
        EventId = 6514,
        Level = LogLevel.Warning,
        Message = "autopilot_run_step_failed runId={RunId} state={State} exceptionType={ExceptionType}")]
    private partial void LogStepFailed(Guid runId, AutopilotRunState state, string exceptionType);

    [LoggerMessage(
        EventId = 6515,
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
