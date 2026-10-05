using Microsoft.Extensions.Logging;
using NightTales.Fetcher.InternalApi;

namespace NightTales.Fetcher.Heartbeat;

/// <summary>Why a <see cref="TaskHeartbeat"/> cancelled its <see cref="TaskHeartbeat.Token"/>.</summary>
public enum HeartbeatStop
{
    /// <summary>Not stopped: the work may run and its result may be posted.</summary>
    None,

    /// <summary>The Api answered 409 <c>fetch_lease_lost</c>: the sweeper owns the task.</summary>
    LeaseLost,

    /// <summary>The Api answered 404: the source was deleted mid-fetch.</summary>
    TaskGone,

    /// <summary>The Api answered 401: the token is wrong; the loop exits.</summary>
    Unauthorized,

    /// <summary>T105: the work outlived its bound; it is cancelled and no result is posted.</summary>
    Expired,

    /// <summary>The host is shutting down.</summary>
    Shutdown,
}

/// <summary>
/// Keeps one lease alive while its work runs (02.5.2-T3 step 4). A <see cref="PeriodicTimer"/> posts a heartbeat every
/// <c>Fetcher:HeartbeatSeconds</c>; a 409 or 404 cancels <see cref="Token"/>, so the work stops instead of finishing a
/// result the Api would reject. With <c>maxLifetime</c> set (T105, <c>fetch_source</c>) the heartbeat also cancels the
/// work and stops itself after that bound, so a stalled page cannot pin a lease slot: the lease is left to expire.
/// </summary>
public sealed partial class TaskHeartbeat : IDisposable
{
    private readonly IFetcherApi _api;
    private readonly Guid _taskId;
    private readonly string _workerId;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _work;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource? _expiry;
    private readonly CancellationTokenRegistration _expiryRegistration;
    private readonly Task _loop;
    private int _reason;

    /// <summary>Starts heartbeating.</summary>
    /// <param name="api">The internal API.</param>
    /// <param name="taskId">The leased task.</param>
    /// <param name="workerId">The worker holding the lease.</param>
    /// <param name="interval">The heartbeat period (60 s).</param>
    /// <param name="maxLifetime">The T105 bound for a <c>fetch_source</c> task; <see langword="null"/> for none.</param>
    /// <param name="time">The time source of the period and the bound.</param>
    /// <param name="log">The log.</param>
    /// <param name="shutdown">Host shutdown; linked into <see cref="Token"/>.</param>
    public TaskHeartbeat(
        IFetcherApi api,
        Guid taskId,
        string workerId,
        TimeSpan interval,
        TimeSpan? maxLifetime,
        TimeProvider time,
        ILogger log,
        CancellationToken shutdown)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);

        _api = api;
        _taskId = taskId;
        _workerId = workerId;
        _log = log;
        _work = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        Token = _work.Token;

        if (maxLifetime is { } bound)
        {
            _expiry = new CancellationTokenSource(bound, time);
            _expiryRegistration = _expiry.Token.Register(() => Stop(HeartbeatStop.Expired));
        }

        _loop = RunAsync(interval, time);
    }

    /// <summary>Cancelled when the lease is lost, the task is gone, the token is refused, the bound passed or the host stops.</summary>
    public CancellationToken Token { get; }

    /// <summary>The reason <see cref="Token"/> was cancelled, or <see cref="HeartbeatStop.None"/>.</summary>
    public HeartbeatStop Reason => (HeartbeatStop)Volatile.Read(ref _reason);

    private void Stop(HeartbeatStop reason)
    {
        // The first reason wins; a later one (shutdown after a lost lease) must not overwrite it.
        Interlocked.CompareExchange(ref _reason, (int)reason, (int)HeartbeatStop.None);
        try
        {
            _work.Cancel();
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposed: nothing left to stop.
        }
    }

    private async Task RunAsync(TimeSpan interval, TimeProvider time)
    {
        await Task.Yield();
        var stopToken = _stop.Token;
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            using var shutdownRegistration = Token.Register(() => Stop(HeartbeatStop.Shutdown));
            while (await timer.WaitForNextTickAsync(stopToken).ConfigureAwait(false))
            {
                var beat = await _api.HeartbeatAsync(_taskId, _workerId, stopToken).ConfigureAwait(false);
                if (!beat.IsError)
                {
                    continue;
                }

                var error = beat.FirstError;
                if (error.Code == FetcherApiErrors.LeaseLostCode)
                {
                    LogStopped(_taskId, "lease_lost");
                    Stop(HeartbeatStop.LeaseLost);
                }
                else if (error.Code == FetcherApiErrors.TaskNotFoundCode)
                {
                    LogStopped(_taskId, "task_not_found");
                    Stop(HeartbeatStop.TaskGone);
                }
                else if (error.Code == FetcherApiErrors.UnauthorizedCode)
                {
                    LogStopped(_taskId, "unauthorized");
                    Stop(HeartbeatStop.Unauthorized);
                }
                else
                {
                    // A transient failure: the lease lives 90 s, so the next tick may still save it.
                    LogMissed(_taskId, error.Code);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Dispose, expiry or shutdown.
        }
    }

    /// <summary>Stops heartbeating; the in-flight heartbeat is cancelled and not awaited.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        _expiryRegistration.Dispose();
        _expiry?.Dispose();
        _loop.ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        _work.Dispose();
        _stop.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "heartbeat stopped for task {TaskId}: {Reason}")]
    private partial void LogStopped(Guid taskId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "heartbeat missed for task {TaskId}: {ErrorCode}")]
    private partial void LogMissed(Guid taskId, string errorCode);
}
