using System.Threading.Channels;
using ErrorOr;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;

namespace NightTales.MptAdapter;

/// <summary>
/// T2's completed-task seam: the MPT-side poller publishes a task that MPT finished, and 11.3.5-T3's
/// <c>PostProcessor</c> consumes it and owns the <c>checking → succeeded|failed</c> transition (T3 step 1:
/// "T2 sets <c>checking</c>; no double transition"). The queue is in-process on purpose: the adapter is one
/// service and a durable hand-off would be a second store for no gain.
/// </summary>
public interface ICompletedTaskFeed
{
    /// <summary>Hands a task at <c>checking</c>/100 to the post-processor. Idempotent per task (§7.1).</summary>
    void Publish(RenderTaskRecord record);

    /// <summary>Everything published so far, then everything published later, until cancelled.</summary>
    IAsyncEnumerable<RenderTaskRecord> ReadAllAsync(CancellationToken cancellationToken);
}

/// <summary>The unbounded in-process implementation of <see cref="ICompletedTaskFeed"/>.</summary>
public sealed class ChannelCompletedTaskFeed : ICompletedTaskFeed
{
    private readonly Channel<RenderTaskRecord> _channel =
        Channel.CreateUnbounded<RenderTaskRecord>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = false });

    /// <inheritdoc />
    public void Publish(RenderTaskRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _channel.Writer.TryWrite(record);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<RenderTaskRecord> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>
/// The durable half of D6's poll bookkeeping: consecutive failed polls per task (plan §3.1's <c>attempts</c>).
/// It is not on <see cref="ITaskStore"/> because it is the poller's own counter; a store that does not
/// implement it simply leaves the alerting disabled.
/// </summary>
internal interface IPollAttemptStore
{
    /// <summary>Increments the counter on a failed poll, resets it on a good one, and returns the new value.</summary>
    ValueTask<int> RecordPollOutcomeAsync(Guid taskId, bool failed, CancellationToken cancellationToken);
}

/// <summary>
/// D6's <c>MptTaskPoller</c>: the only reader of MPT (F19). Every cycle it walks the non-terminal tasks, maps
/// MPT's state and progress to the adapter's (05 D4), applies the two lost rules and the 05 D5 failure map,
/// finalises a cancelled task on MPT's terminal state, and hands a completed task to T3's seam.
/// </summary>
/// <remarks>
/// A Worker poll never reaches MPT: the endpoints read the store, so everything MPT says lands here first.
/// A task MPT has already finished (<c>checking</c>/100) is not polled again — the post-processor owns it, and
/// re-asking would only duplicate the hand-off. MPT never being reachable is not a task failure: the counter
/// climbs and the Worker's <c>render_timeout</c> decides (Failure handling).
/// </remarks>
public sealed class MptTaskPoller : BackgroundService
{
    /// <summary>Failure handling: 900 consecutive failed polls ≈ 30 min at D6's 2 s cadence.</summary>
    public const int OpsAlertAttempts = 900;

    /// <summary>Failure handling: the log backs off from 2 s to a 30 s cap.</summary>
    public const int MaxBackoffSeconds = 30;

    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);

    private readonly ITaskStore _store;
    private readonly MptClient _client;
    private readonly IMptProcessStartReader _startReader;
    private readonly TaskDirCleaner _cleaner;
    private readonly ICompletedTaskFeed _completedTasks;
    private readonly AdapterOptions _options;
    private readonly ILogger<MptTaskPoller> _log;

    private readonly Dictionary<Guid, DateTimeOffset> _lastFailureLog = [];
    private readonly Dictionary<Guid, int> _backoffSeconds = [];
    private readonly HashSet<Guid> _alerted = [];
    private DateTimeOffset? _lastPurge;
    private bool _unauthorizedLogged;

    public MptTaskPoller(
        ITaskStore store,
        MptClient client,
        IMptProcessStartReader startReader,
        TaskDirCleaner cleaner,
        ICompletedTaskFeed completedTasks,
        IOptions<AdapterOptions> options,
        ILogger<MptTaskPoller> log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(startReader);
        ArgumentNullException.ThrowIfNull(cleaner);
        ArgumentNullException.ThrowIfNull(completedTasks);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        _store = store;
        _client = client;
        _startReader = startReader;
        _cleaner = cleaner;
        _completedTasks = completedTasks;
        _options = options.Value;
        _log = log;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds));
        _log.PollerStarted(interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _log.PollCycleFailed(exception);
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One cycle. Internal so the tests can drive an exact observation instead of racing the timer; the host
    /// only ever runs it through <see cref="ExecuteAsync"/>.
    /// </summary>
    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var taskIds = await _store.ListNonTerminalAsync(cancellationToken).ConfigureAwait(false);
        foreach (var taskId in taskIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await PollTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _log.PollTaskFailed(exception, taskId);
            }
        }

        await PurgeIfDueAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>05 D4's progress boundaries; a stale call can never move the stored stage backwards.</summary>
    internal static string StageForProgress(int progress, string? previous = null)
    {
        var mapped = progress switch
        {
            <= 0 => RenderStages.Queued,
            < 30 => RenderStages.Voice,
            < 40 => RenderStages.Subtitles,
            < 50 => RenderStages.Images,
            < 100 => RenderStages.Assembling,
            _ => RenderStages.Done,
        };

        return previous is not null && Rank(previous) > Rank(mapped) ? previous : mapped;
    }

    private async Task PollTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var record = await _store.TryGetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (record is null || record.IsTerminal)
        {
            return;
        }

        if (record.BackendTaskId is not { Length: > 0 } mptTaskId)
        {
            // The submit is still in flight: there is nothing at MPT to ask about yet.
            return;
        }

        if (!record.CancelRequested
            && string.Equals(record.Stage, RenderStages.Checking, StringComparison.Ordinal)
            && record.Percent >= 100)
        {
            // MPT is done and the post-processor owns the row; there is nothing new to learn from MPT.
            return;
        }

        var processStart = await _startReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (processStart is { } started && record.BackendStartedAt is { } accepted && started > accepted)
        {
            await FinalizeLostAsync(record, OutcomeMapper.MptRestarted, cancellationToken).ConfigureAwait(false);
            return;
        }

        var status = await _client.GetStatusAsync(mptTaskId, record.TaskId, cancellationToken).ConfigureAwait(false);
        if (status.IsError)
        {
            await HandleStatusErrorAsync(record, status.FirstError, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ApplyStatusAsync(record, status.Value, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyStatusAsync(
        RenderTaskRecord record,
        MptTaskStatus status,
        CancellationToken cancellationToken)
    {
        // MPT answered, so the row's failure streak ends here even when the answer is a failure.
        await RecordOutcomeAsync(record.TaskId, failed: false, cancellationToken).ConfigureAwait(false);

        var snapshot = status.Snapshot;

        // 05 D4: a cancelled task never becomes succeeded — on MPT's terminal state it is cancelled at once.
        if (record.CancelRequested && snapshot.State is 1 or -1)
        {
            await FinalizeCancelledAsync(record, snapshot, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (snapshot.State)
        {
            case 4 when snapshot.Progress <= 0:
                // MPT accepted the task and queued it behind another (S11/C10): the row stays `queued` until
                // real progress arrives, which is what the queued fixture shows (`mpt.state 4`, `progress 0`).
                await WriteAsync(record with { MptLastSnapshot = snapshot }, cancellationToken).ConfigureAwait(false);
                break;

            case 4:
                // 05 D4: MPT writes `state` and `progress` without a transaction, so a running task can briefly
                // report progress 100. It is still running: the stage stays in the pre-terminal band and the
                // percent at the D5 cap, so `done`/100 is written only by the `checking` hand-off below or by
                // FinalizeCancelledAsync.
                var progress = Math.Min(snapshot.Progress, RenderTaskRecord.NonTerminalPercentCap);
                await WriteAsync(
                    record with
                    {
                        State = RenderStates.Running,
                        Stage = StageForProgress(progress, record.Stage),
                        Percent = progress,
                        MptLastSnapshot = snapshot,
                    },
                    cancellationToken).ConfigureAwait(false);
                break;

            case 1:
                await OnMptCompleteAsync(record, snapshot, cancellationToken).ConfigureAwait(false);
                break;

            case -1:
                await FinishFailureAsync(
                    record,
                    status,
                    OutcomeMapper.MapFailure(snapshot.FailedStage),
                    cancellationToken).ConfigureAwait(false);
                break;

            default:
                await FinishFailureAsync(
                    record,
                    status,
                    OutcomeMapper.UnknownStateError(),
                    cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task OnMptCompleteAsync(
        RenderTaskRecord record,
        MptStateV1 snapshot,
        CancellationToken cancellationToken)
    {
        var observed = record with
        {
            State = RenderStates.Running,
            Stage = RenderStages.Checking,
            Percent = 100,
            MptLastSnapshot = snapshot,
        };

        if (await WriteAsync(observed, cancellationToken).ConfigureAwait(false))
        {
            _completedTasks.Publish(observed);
            _log.CompletedHandedOver(record.TaskId);
        }
    }

    private async Task FinishFailureAsync(
        RenderTaskRecord record,
        MptTaskStatus status,
        RenderOutcomeV1 outcome,
        CancellationToken cancellationToken)
    {
        var snapshot = status.Snapshot;
        var observed = record with
        {
            State = RenderStates.Failed,
            Stage = StageForProgress(snapshot.Progress, record.Stage),
            Percent = snapshot.Progress,
            Outcome = outcome,
            FinishedAt = DateTimeOffset.UtcNow,
            MptLastSnapshot = snapshot,
        };

        await WriteAsync(observed, cancellationToken).ConfigureAwait(false);

        // S4: MPT's own words go to the journal and nowhere else — the status carries the D15 S4 sentence.
        if (!string.IsNullOrEmpty(status.Error))
        {
            _log.MptReportedFailure(record.TaskId, status.Error);
        }

        _log.TaskFailed(record.TaskId, outcome.Code, outcome.Detail);
    }

    private async Task FinalizeLostAsync(RenderTaskRecord record, string reason, CancellationToken cancellationToken)
    {
        await RecordOutcomeAsync(record.TaskId, failed: false, cancellationToken).ConfigureAwait(false);

        if (record.CancelRequested)
        {
            // The user already discarded the task: MPT no longer running it is the state to record.
            await FinalizeCancelledAsync(record, snapshot: null, cancellationToken).ConfigureAwait(false);
            return;
        }

        // The lost fixture: the last observed values are reported with no MPT state (05 §10 T63).
        var snapshot = new MptStateV1(
            null,
            record.MptLastSnapshot?.Progress ?? record.Percent,
            null,
            record.MptLastSnapshot?.Warnings ?? []);

        var observed = record with
        {
            State = RenderStates.Failed,
            Percent = snapshot.Progress,
            Outcome = OutcomeMapper.Lost(reason),
            FinishedAt = DateTimeOffset.UtcNow,
            MptLastSnapshot = snapshot,
        };

        await WriteAsync(observed, cancellationToken).ConfigureAwait(false);
        _log.TaskLost(record.TaskId, reason);
    }

    private async Task FinalizeCancelledAsync(
        RenderTaskRecord record,
        MptStateV1? snapshot,
        CancellationToken cancellationToken)
    {
        var observed = record with
        {
            State = RenderStates.Cancelled,
            Stage = RenderStages.Done,
            Percent = 100,
            Outcome = null,
            FinishedAt = DateTimeOffset.UtcNow,
            MptLastSnapshot = snapshot ?? record.MptLastSnapshot,
        };

        await WriteAsync(observed, cancellationToken).ConfigureAwait(false);

        // 05 D15 P4: a cancelled task's dir and relay files go at once, not after the 24 h rule.
        await _cleaner.CleanAsync(record.TaskId, record.BackendTaskId, cancellationToken).ConfigureAwait(false);
        _log.TaskCancelledCleaned(record.TaskId);
    }

    private async Task HandleStatusErrorAsync(
        RenderTaskRecord record,
        Error error,
        CancellationToken cancellationToken)
    {
        switch (error.Code)
        {
            case MptErrorCodes.TaskNotFound:
                await FinalizeLostAsync(record, OutcomeMapper.MptTaskMissing, cancellationToken).ConfigureAwait(false);
                return;

            case MptErrorCodes.Unauthorized:
                if (!_unauthorizedLogged)
                {
                    _unauthorizedLogged = true;
                    _log.MptUnauthorizedOnce();
                }

                return;

            default:
                await OnPollFailureAsync(record, error, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// Failure handling: the task stays non-terminal, its attempt counter climbs and the log backs off. The
    /// poller never marks such a task failed — the Worker's <c>render_timeout</c> owns that (05 D5).
    /// </summary>
    private async Task OnPollFailureAsync(
        RenderTaskRecord record,
        Error error,
        CancellationToken cancellationToken)
    {
        if (error.Code == MptErrorCodes.Unreachable)
        {
            // D6: the process start is re-read after every MPT connection error, so a restart that happened
            // while the adapter was not looking is seen on the next cycle.
            await _startReader.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        var attempts = await RecordOutcomeAsync(record.TaskId, failed: true, cancellationToken).ConfigureAwait(false);

        if (ShouldLogFailure(record.TaskId, attempts))
        {
            _log.PollNoAnswer(record.TaskId, error.Code, attempts);
        }

        if (attempts > OpsAlertAttempts && _alerted.Add(record.TaskId))
        {
            var seconds = Math.Max(1, _options.PollSeconds);
            _log.PollOpsAlert(record.TaskId, attempts, OpsAlertAttempts * seconds / 60, seconds);
        }
    }

    private async Task PurgeIfDueAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastPurge is { } last && now - last < PurgeInterval)
        {
            return;
        }

        _lastPurge = now;
        var purged = await _store
            .PurgeCleanedAsync(now.AddDays(-Math.Max(1, _options.PurgeAfterDays)), cancellationToken)
            .ConfigureAwait(false);

        if (purged > 0)
        {
            _log.PurgedCleanedRows(purged);
        }
    }

    /// <summary>
    /// The core's own update pattern (<c>TaskUpdates</c> is internal to the server project): merge and retry
    /// exactly once, so a cancel that raced this observation is not overwritten.
    /// </summary>
    private async Task<bool> WriteAsync(RenderTaskRecord observed, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (await _store.UpdateAsync(observed, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            var stored = await _store.TryGetAsync(observed.TaskId, cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                return false;
            }

            observed = stored.Merge(observed);
        }

        return false;
    }

    private async ValueTask<int> RecordOutcomeAsync(Guid taskId, bool failed, CancellationToken cancellationToken)
    {
        if (!failed)
        {
            _lastFailureLog.Remove(taskId);
            _backoffSeconds.Remove(taskId);
            _alerted.Remove(taskId);
        }

        if (_store is not IPollAttemptStore attempts)
        {
            return 0;
        }

        return await attempts.RecordPollOutcomeAsync(taskId, failed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Failure handling's exponential log back-off: 2 s doubling to a 30 s cap, per task.</summary>
    private bool ShouldLogFailure(Guid taskId, int attempts)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastFailureLog.TryGetValue(taskId, out var last)
            && _backoffSeconds.TryGetValue(taskId, out var seconds)
            && now - last < TimeSpan.FromSeconds(seconds))
        {
            return false;
        }

        var next = Math.Min(MaxBackoffSeconds, 1 << Math.Min(Math.Max(attempts, 1), 5));
        _lastFailureLog[taskId] = now;
        _backoffSeconds[taskId] = next;
        return true;
    }

    private static int Rank(string stage)
    {
        for (var index = 0; index < RenderStages.All.Count; index++)
        {
            if (string.Equals(RenderStages.All[index], stage, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return 0;
    }
}
