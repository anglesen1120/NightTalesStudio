using ErrorOr;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Domain.Research;
using NightTales.Fetcher.Core.Fetching;
using NightTales.Fetcher.Heartbeat;
using NightTales.Fetcher.InternalApi;

namespace NightTales.Fetcher;

/// <summary>
/// The lease loop (02.5.2-T3 Design, 02 D10/F15): leases at most <c>MaxLeasesPlatformWide</c> minus the tasks in flight,
/// runs each task under a <see cref="TaskHeartbeat"/>, posts the result and never lets one task kill the loop. It opens no
/// socket: liveness is the ability to lease, so <c>ConsecutiveFailuresBeforeExit</c> failed lease calls, or a 401, end the
/// process with a non-zero code for the supervisor to restart.
/// </summary>
/// <remarks>
/// Cancellation has three sources and each is told apart by the token that fired: host shutdown or a fatal exit
/// (<c>ct</c>, nothing is posted), a lost lease or the T105 bound (<see cref="TaskHeartbeat.Token"/>, nothing is posted),
/// and anything else, which is a failed fetch and is posted.
/// </remarks>
public sealed partial class FetcherLoop(
    IFetcherApi api,
    ISourceFetcher fetcher,
    IPublicSuffixList psl,
    IOptions<FetcherOptions> fetcherOptions,
    IOptions<FetcherLoopOptions> loopOptions,
    IOptions<FetcherApiOptions> apiOptions,
    TimeProvider time,
    FetcherMetrics metrics,
    IFetcherExit exit,
    ILogger<FetcherLoop> log,
    ISourceFinderRunner? finder = null) : BackgroundService
{
    private const string FetchSource = "fetch_source";
    private const string FindSources = "find_sources";
    private static readonly TimeSpan MaxFailureBackoff = TimeSpan.FromSeconds(30);

    private readonly FetcherOptions _fetcher = fetcherOptions.Value;
    private readonly FetcherLoopOptions _loop = loopOptions.Value;
    private readonly string _workerId = apiOptions.Value.EffectiveWorkerId;
    private int _exiting;
    private CancellationTokenSource? _run;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var inFlight = new Dictionary<Guid, Task>();
        var failures = 0;
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _run = run;
        try
        {
            while (!run.IsCancellationRequested)
            {
                foreach (var finished in inFlight.Where(pair => pair.Value.IsCompleted).Select(pair => pair.Key).ToList())
                {
                    inFlight.Remove(finished);
                }

                var want = _fetcher.MaxLeasesPlatformWide - inFlight.Count;
                if (want <= 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(_loop.BusyPollMilliseconds), time, run.Token).ConfigureAwait(false);
                    continue;
                }

                var leased = await api.LeaseAsync(_workerId, want, run.Token).ConfigureAwait(false);
                if (leased.IsError)
                {
                    if (leased.FirstError.Code == FetcherApiErrors.UnauthorizedCode)
                    {
                        Fatal("the internal API refused the fetcher token");
                        break;
                    }

                    failures++;
                    LogLeaseFailed(failures, _loop.ConsecutiveFailuresBeforeExit, leased.FirstError.Code);
                    if (failures >= _loop.ConsecutiveFailuresBeforeExit)
                    {
                        Fatal("the internal API is unreachable; exiting for a supervisor restart");
                        break;
                    }

                    await Task.Delay(FailureBackoff(failures), time, run.Token).ConfigureAwait(false);
                    continue;
                }

                failures = 0;
                foreach (var item in leased.Value.Take(want))
                {
                    if (!inFlight.ContainsKey(item.TaskId))
                    {
                        inFlight[item.TaskId] = RunTaskSafeAsync(item, run.Token);
                    }
                }

                if (leased.Value.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(_loop.IdlePollMilliseconds), time, run.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            // Host shutdown or a fatal exit: fall through to the drain.
        }
        finally
        {
            // Stop leasing, cancel the in-flight work and wait for it: their leases expire in <= 90 s and the sweeper requeues.
            await run.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(inFlight.Values).ConfigureAwait(false);
            _run = null;
        }
    }

    private TimeSpan FailureBackoff(int failures)
    {
        var delay = TimeSpan.FromMilliseconds(_loop.IdlePollMilliseconds * Math.Pow(2, failures - 1));
        return delay > MaxFailureBackoff ? MaxFailureBackoff : delay;
    }

    private void Fatal(string reason)
    {
        if (Interlocked.Exchange(ref _exiting, 1) == 0)
        {
            LogFatal(reason);
            exit.RequestExit(1, reason);
        }

        try
        {
            _run?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The loop already ended.
        }
    }

    /// <summary>One task; never throws (a bad URL or a faulted fetch is a result or a log line, never a dead loop).</summary>
    private async Task RunTaskSafeAsync(LeasedTask item, CancellationToken ct)
    {
        try
        {
            await RunTaskAsync(item, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            metrics.RecordTask("cancelled", string.Empty);
        }
        catch (Exception ex)
        {
            // Only the type: a message can carry a URL.
            metrics.RecordTask("faulted", string.Empty);
            LogTaskFaulted(item.TaskId, item.Kind, item.Attempt, ex.GetType().Name);
        }
    }

    private async Task RunTaskAsync(LeasedTask item, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        var isFetch = string.Equals(item.Kind, FetchSource, StringComparison.Ordinal);
        TimeSpan? bound = isFetch ? TimeSpan.FromSeconds(_fetcher.TotalTimeoutSeconds + _fetcher.ExtractTimeoutSeconds) : null;
        using var heartbeat = new TaskHeartbeat(
            api, item.TaskId, _workerId, TimeSpan.FromSeconds(_fetcher.HeartbeatSeconds), bound, time, log, ct);

        string domain = string.Empty;
        FetchResultBody? body;
        if (isFetch)
        {
            (body, domain) = await FetchSourceAsync(item, heartbeat.Token, ct).ConfigureAwait(false);
        }
        else if (string.Equals(item.Kind, FindSources, StringComparison.Ordinal))
        {
            body = await FindSourcesAsync(item, heartbeat.Token, ct).ConfigureAwait(false);
        }
        else
        {
            LogUnknownKind(item.TaskId, item.Kind);
            metrics.RecordTask("abandoned", "unknown_kind");
            return;
        }

        if (heartbeat.Token.IsCancellationRequested || body is null)
        {
            Abandon(item, heartbeat, domain, started, body is null && !heartbeat.Token.IsCancellationRequested);
            return;
        }

        var posted = await api.PostResultAsync(item.TaskId, body, ct).ConfigureAwait(false);
        // The outcome actually posted: the client may have replaced an over-cap done body with failed/source_too_large (D19).
        var final = posted.IsError ? body : posted.Value;
        var status = final.Status;
        var code = ResultBodies.CodeOf(final);
        var outcome = "posted";
        if (posted.IsError)
        {
            var error = posted.FirstError;
            outcome = error.Code;
            if (error.Code == FetcherApiErrors.UnauthorizedCode)
            {
                Fatal("the internal API refused the fetcher token");
            }
            else if (FetcherApiErrors.IsRetryable(error))
            {
                // Three attempts failed: the lease expires and the sweeper requeues (attempt 2), accepted by the Design.
                outcome = "gave_up";
            }

            status = "unposted";
        }

        metrics.RecordTask(status, code);
        var elapsedSeconds = time.GetElapsedTime(started).TotalSeconds;
        metrics.RecordDuration(elapsedSeconds);
        var httpStatus = final.HttpStatus ?? 0;
        LogTaskFinished(item.TaskId, item.Kind, item.Attempt, status, code, httpStatus, elapsedSeconds, domain, outcome);
    }

    private void Abandon(LeasedTask item, TaskHeartbeat heartbeat, string domain, long started, bool noResult)
    {
        var reason = noResult ? "no_result" : heartbeat.Reason.ToString();
        if (heartbeat.Reason == HeartbeatStop.Unauthorized)
        {
            Fatal("the internal API refused the fetcher token");
        }

        metrics.RecordTask("abandoned", reason);
        metrics.RecordDuration(time.GetElapsedTime(started).TotalSeconds);
        LogAbandoned(item.TaskId, item.Kind, item.Attempt, reason, time.GetElapsedTime(started).TotalSeconds, domain);
    }

    private async Task<(FetchResultBody? Body, string Domain)> FetchSourceAsync(LeasedTask item, CancellationToken work, CancellationToken ct)
    {
        // Defence in depth: the task's URL is parsed again here. A bad one is a bug upstream and must never reach the row as a
        // user-facing 400 code, so it is reported as source_unreachable (D8) and logged.
        var parsed = SourceUrl.Parse(item.Url, psl);
        if (parsed.IsError)
        {
            LogMalformedUrl(item.TaskId, item.Attempt);
            return (ResultBodies.Failed(_workerId, SourceErrors.SourceUnreachableCode), string.Empty);
        }

        var url = parsed.Value;
        var domain = url.RegistrableDomain;
        var request = new SourceFetchRequest(new Uri(url.Value, UriKind.Absolute), domain, item.Attempt);
        try
        {
            var fetched = await fetcher.FetchAsync(request, work).ConfigureAwait(false);
            return (
                fetched.IsError ? ResultBodies.FromError(_workerId, fetched.FirstError) : ResultBodies.Done(_workerId, fetched.Value, _fetcher),
                domain);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            // Lease lost, task gone or the T105 bound: the caller drops the result.
            return (null, domain);
        }
        catch (OperationCanceledException)
        {
            // Neither token fired: a per-fetch timeout that escaped as a cancellation is a failed fetch.
            return (ResultBodies.Failed(_workerId, SourceErrors.SourceUnreachableCode), domain);
        }
        catch (Exception ex)
        {
            LogFetcherThrew(item.TaskId, item.Attempt, domain, ex.GetType().Name);
            return (ResultBodies.Failed(_workerId, SourceErrors.SourceUnreachableCode), domain);
        }
    }

    private async Task<FetchResultBody?> FindSourcesAsync(LeasedTask item, CancellationToken work, CancellationToken ct)
    {
        if (finder is null)
        {
            // 02.7.2-T3 owns the runner. Until then the Api answers every find_sources body with 400
            // fetch_task_kind_unsupported and leaves the task leased (02.5.2-T2 hand-off), so a POST could only fail:
            // abandon, log, and let the lease expire.
            LogNoFinder(item.TaskId, item.Attempt);
            return null;
        }

        try
        {
            var found = await finder.FindAsync(item, work).ConfigureAwait(false);
            return found.IsError
                ? new FetchResultBody(_workerId, "failed", ErrorCode: "find_sources_generation_failed")
                : new FetchResultBody(_workerId, "done", Candidates: found.Value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogFinderThrew(item.TaskId, item.Attempt, ex.GetType().Name);
            return new FetchResultBody(_workerId, "failed", ErrorCode: "find_sources_generation_failed");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "lease call failed ({Failures}/{Limit}): {ErrorCode}")]
    private partial void LogLeaseFailed(int failures, int limit, string errorCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "fetcher exiting non-zero: {Reason}")]
    private partial void LogFatal(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "fetch task faulted: task {TaskId} kind {Kind} attempt {Attempt} {ExceptionType}")]
    private partial void LogTaskFaulted(Guid taskId, string kind, int attempt, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "leased task {TaskId} has unknown kind {Kind}; left to expire")]
    private partial void LogUnknownKind(Guid taskId, string kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "malformed source URL on leased task {TaskId} attempt {Attempt}; reporting source_unreachable")]
    private partial void LogMalformedUrl(Guid taskId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "source fetcher threw for task {TaskId} attempt {Attempt} domain {Domain}: {ExceptionType}")]
    private partial void LogFetcherThrew(Guid taskId, int attempt, string domain, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "no source finder runner registered; find_sources task {TaskId} attempt {Attempt} left to expire")]
    private partial void LogNoFinder(Guid taskId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "source finder threw for task {TaskId} attempt {Attempt}: {ExceptionType}")]
    private partial void LogFinderThrew(Guid taskId, int attempt, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "task {TaskId} kind {Kind} attempt {Attempt} {Status} code {ErrorCode} http {HttpStatus} in {DurationSeconds:F2}s domain {Domain} ({Outcome})")]
    private partial void LogTaskFinished(Guid taskId, string kind, int attempt, string status, string errorCode, int httpStatus, double durationSeconds, string domain, string outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "task {TaskId} kind {Kind} attempt {Attempt} abandoned without a result: {Reason} after {DurationSeconds:F2}s domain {Domain}")]
    private partial void LogAbandoned(Guid taskId, string kind, int attempt, string reason, double durationSeconds, string domain);
}
