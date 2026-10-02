using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;

namespace NightTales.MptAdapter;

/// <summary>
/// Consumes the tasks T2's poller hands over at <c>checking</c>/100 (<see cref="ICompletedTaskFeed"/>), runs the
/// check, publishes the output and writes the single <c>checking</c> → <c>succeeded</c>/<c>failed</c>/<c>cancelled</c>
/// transition (T3 step 1: T2 sets <c>checking</c>, this class never sets it again). One worker per
/// <see cref="AdapterOptions.CheckConcurrency"/> reads the feed, so that many checks run at once and no more
/// (D6a, Q2). The adapter never decides pass/fail of quality: <c>errors[]</c> in the report is stored and
/// published untouched (05 D10).
/// </summary>
public sealed class PostProcessor : BackgroundService
{
    private readonly ICompletedTaskFeed _feed;
    private readonly ITaskStore _store;
    private readonly ICheckRunner _runner;
    private readonly CheckReportReader _reader;
    private readonly OutputPublisher _publisher;
    private readonly DiskRuleEnforcer _disk;
    private readonly AdapterOptions _options;
    private readonly ILogger<PostProcessor> _log;
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private readonly ConcurrentDictionary<Task, byte> _retention = new();

    internal PostProcessor(
        ICompletedTaskFeed feed,
        ITaskStore store,
        ICheckRunner runner,
        CheckReportReader reader,
        OutputPublisher publisher,
        DiskRuleEnforcer disk,
        IOptions<AdapterOptions> options,
        ILogger<PostProcessor> log)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        _feed = feed;
        _store = store;
        _runner = runner;
        _reader = reader;
        _publisher = publisher;
        _disk = disk;
        _options = options.Value;
        _log = log;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable
            .Range(0, Math.Max(1, _options.CheckConcurrency))
            .Select(_ => Task.Run(() => WorkAsync(stoppingToken), CancellationToken.None))
            .ToArray();
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task WorkAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var record in _feed.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await ProcessAsync(record, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One bad task must never stop the consumer; the row stays at checking and a restart re-runs it.
                    _log.PostProcessFailed(exception, record.TaskId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>Runs the whole post-processing of one handed-over task. Idempotent per task.</summary>
    internal async Task ProcessAsync(RenderTaskRecord handed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handed);

        if (!_inFlight.TryAdd(handed.TaskId, 0))
        {
            return;
        }

        try
        {
            var row = await _store.TryGetAsync(handed.TaskId, cancellationToken).ConfigureAwait(false);
            if (row is null || row.IsTerminal)
            {
                return;
            }

            if (row.CancelRequested)
            {
                await FinishCancelledAsync(row, cancellationToken).ConfigureAwait(false);
                return;
            }

            Verdict verdict;
            try
            {
                verdict = await CheckAsync(row, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A durable-copy or output IO fault must not wedge the task at checking (CR-2/SEC-1).
                _log.PostProcessFailed(exception, row.TaskId);
                verdict = Verdict.Failed(OutputPublisher.CheckFailed, null);
            }

            // Cancel during checking: the process has exited now, so the dir can go (T3 acceptance).
            var latest = await _store.TryGetAsync(row.TaskId, cancellationToken).ConfigureAwait(false);
            if (latest is null)
            {
                return;
            }

            if (latest.CancelRequested)
            {
                await FinishCancelledAsync(latest, cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteVerdictAsync(latest, verdict, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(handed.TaskId, out _);
        }
    }

    private async Task<Verdict> CheckAsync(RenderTaskRecord row, CancellationToken cancellationToken)
    {
        if (!SafePath.IsPlainName(row.BackendTaskId))
        {
            return Verdict.Failed(OutputPublisher.CheckFailed, null);
        }

        var marker = await SubmitMarker.ReadAsync(_options, row.TaskId, cancellationToken).ConfigureAwait(false);
        if (marker is null)
        {
            return Verdict.Failed(OutputPublisher.CheckFailed, null);
        }

        var run = await _runner
            .RunAsync(new CheckRequest(row.TaskId, row.BackendTaskId!, marker.Aspect, marker.Subtitles), cancellationToken)
            .ConfigureAwait(false);
        var journalOut = AdapterText.Sanitize(run.Stdout, CheckRunner.MaxCapturedBytes);
        var journalErr = AdapterText.Sanitize(run.Stderr, CheckRunner.MaxCapturedBytes);
        _log.CheckStreams(row.TaskId, journalOut, journalErr);

        if (run.TimedOut)
        {
            return Verdict.Failed(CheckTimeout, null);
        }

        var report = await _reader.ReadAsync(row.TaskId, row.BackendTaskId!, cancellationToken).ConfigureAwait(false);
        if (run.ExitCode == 2)
        {
            _log.CheckOpsAlert(row.TaskId);
        }

        if (run.ExitCode != 0 || report is null)
        {
            return Verdict.Failed(OutputPublisher.CheckFailed, report);
        }

        var published = await _publisher
            .PublishAsync(row.BackendTaskId!, report.Report, marker.Subtitles, cancellationToken)
            .ConfigureAwait(false);
        return published.Output is null
            ? Verdict.Failed(published.FailureDetail ?? OutputPublisher.CheckFailed, report)
            : new Verdict(published.Output, null, report);
    }

    private async Task WriteVerdictAsync(RenderTaskRecord row, Verdict verdict, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var observed = verdict.Output is { } output
            ? row with
            {
                State = RenderStates.Succeeded,
                Stage = RenderStages.Done,
                Percent = 100,
                Output = output,
                Check = verdict.Report?.Raw,
                Outcome = null,
                FinishedAt = now,
            }
            : row with
            {
                State = RenderStates.Failed,
                Stage = RenderStages.Checking,
                Percent = 100,
                Check = verdict.Report?.Raw,
                Outcome = new RenderOutcomeV1(
                    RenderOutcomeCodes.RenderNodeError,
                    verdict.FailureDetail,
                    OutcomeMessages.For(RenderOutcomeCodes.RenderNodeError)),
                FinishedAt = now,
            };

        var stored = await WriteTerminalAsync(observed, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return;
        }

        switch (stored.State)
        {
            case RenderStates.Cancelled:
                await _disk.OnCancelledAsync(row.TaskId, row.BackendTaskId, cancellationToken).ConfigureAwait(false);
                break;
            case RenderStates.Succeeded:
                _log.TaskSucceeded(row.TaskId);
                break;
            default:
                _log.TaskFailed(row.TaskId, stored.Outcome?.Code, stored.Outcome?.Detail);
                TrackRetention(Task.Run(
                    () => _disk.OnFailedAsync(row.TaskId, row.BackendTaskId, cancellationToken),
                    CancellationToken.None));
                break;
        }
    }

    /// <summary>Keeps a pending retention task only until it finishes, then drops it and observes its failure (PERF-1).</summary>
    private void TrackRetention(Task retention)
    {
        _retention.TryAdd(retention, 0);
        _ = retention.ContinueWith(
            finished =>
            {
                _retention.TryRemove(finished, out _);
                if (finished.IsFaulted)
                {
                    _log.RetentionFailed(finished.Exception!.GetBaseException());
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task FinishCancelledAsync(RenderTaskRecord row, CancellationToken cancellationToken)
    {
        var cancelled = row with
        {
            State = RenderStates.Cancelled,
            Stage = RenderStages.Done,
            Percent = 100,
            Outcome = null,
            Output = null,
            Check = null,
            FinishedAt = DateTimeOffset.UtcNow,
        };
        await WriteTerminalAsync(cancelled, cancellationToken).ConfigureAwait(false);
        await _disk.OnCancelledAsync(row.TaskId, row.BackendTaskId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RenderTaskRecord?> WriteTerminalAsync(RenderTaskRecord observed, CancellationToken cancellationToken)
    {
        if (_store is ICancelResolvingStore resolving)
        {
            return await resolving.UpdateResolvingCancelAsync(observed, cancellationToken).ConfigureAwait(false);
        }

        if (!await _store.UpdateAsync(observed, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (await _store.TryGetAsync(observed.TaskId, cancellationToken).ConfigureAwait(false))?.ResolvePendingCancel();
    }

    /// <summary>The closed 05 D5 detail for a check that hit <see cref="AdapterOptions.CheckTimeoutSeconds"/>.</summary>
    internal const string CheckTimeout = "check_timeout";

    private sealed record Verdict(RenderOutputV1? Output, string? FailureDetail, CheckReport? Report)
    {
        internal static Verdict Failed(string detail, CheckReport? report) => new(null, detail, report);
    }
}
