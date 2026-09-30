using System.Collections.Concurrent;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;

namespace NightTales.TestKit.Threading;

/// <summary>
/// Observes the Hangfire state machine in-process (09 README D13): a test waits for a job to reach a
/// state instead of sleeping and re-reading storage, which is what "integration tests never sleep"
/// requires of the real-server tests.
/// </summary>
/// <remarks>
/// One instance per <see cref="WorkerHost"/>: the host prepends it to its own
/// <c>IJobFilterProvider</c> chain, so nothing here touches Hangfire's process-global provider
/// collection (which is not thread-safe and is shared by every parallel test class). The observer
/// only records; it never changes a state.
/// </remarks>
public sealed class JobStateObserver : IApplyStateFilter
{
    private static readonly TimeSpan CommitPollInterval = TimeSpan.FromMilliseconds(10);

    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _signals = new(StringComparer.Ordinal);
    private JobStorage? _storage;

    /// <summary>
    /// Binds the storage of the host this observer watches, so a wait returns only once the state is
    /// committed — <see cref="OnStateApplied"/> runs inside Hangfire's state transaction, before it
    /// commits, and a test that reads storage or changes the state right after would race it.
    /// </summary>
    /// <param name="storage">The storage of the host; a caller that never reads it may leave it unbound.</param>
    public void Attach(JobStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
    }

    /// <summary>Every state applied to <paramref name="jobId"/>, oldest first.</summary>
    public IReadOnlyList<string> StatesOf(string jobId) =>
        _states.TryGetValue(jobId, out var states) ? [.. states] : [];

    /// <summary>How often <paramref name="jobId"/> has been in <paramref name="stateName"/>.</summary>
    public int CountOf(string jobId, string stateName) =>
        StatesOf(jobId).Count(state => string.Equals(state, stateName, StringComparison.Ordinal));

    /// <summary>
    /// Waits until <paramref name="jobId"/> reaches <paramref name="stateName"/>; the wait is
    /// event-driven and fails with <see cref="TimeoutException"/> instead of hanging.
    /// </summary>
    public Task WaitForStateAsync(string jobId, string stateName, TimeSpan timeout) =>
        WaitForStatesAsync(jobId, stateName, count: 1, timeout);

    /// <summary>
    /// Waits until <paramref name="jobId"/> has been in <paramref name="stateName"/> at least
    /// <paramref name="count"/> times (a retry is a second <c>Scheduled</c> application) and the host
    /// storage has persisted them. The whole wait — the state signal and the persistence check — is
    /// bounded by <paramref name="timeout"/>.
    /// </summary>
    public async Task WaitForStatesAsync(string jobId, string stateName, int count, TimeSpan timeout)
    {
        var key = $"{jobId}|{stateName}";

        // One deadline for the whole wait: the signal and the commit confirmation share the caller's
        // timeout instead of each spending it in full (DR 09.2.6-T1).
        var deadline = DateTime.UtcNow.Add(timeout);

        while (CountOf(jobId, stateName) < count)
        {
            var signal = _signals.GetOrAdd(
                key,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

            // The state may have been applied between the count check and the registration; the signal
            // would then never fire, so the recorded states are consulted once more.
            if (CountOf(jobId, stateName) >= count)
            {
                signal.TrySetResult();
            }

            await signal.Task.WaitAsync(Remaining(deadline));

            // The signal is single-shot: replace the completed one so the next iteration blocks again.
            _signals.TryUpdate(
                key,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                signal);
        }

        await WaitForCommittedStatesAsync(jobId, stateName, count, deadline, timeout);
    }

    /// <summary>The time left before <paramref name="deadline"/>, never negative.</summary>
    private static TimeSpan Remaining(DateTime deadline)
    {
        var remaining = deadline - DateTime.UtcNow;

        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Waits until the host's storage has <b>persisted</b> at least <paramref name="count"/> applications
    /// of <paramref name="stateName"/> to <paramref name="jobId"/>, i.e. until the transactions that
    /// applied them committed. It is a no-op while no storage is bound.
    /// </summary>
    /// <remarks>
    /// The confirmation reads the persisted state history rather than the current state: the job may
    /// legitimately have moved on since — a zero-delay <c>Scheduled</c> or a short <c>Processing</c> is
    /// superseded within milliseconds — and a wait for a state that did commit must still return.
    /// </remarks>
    private async Task WaitForCommittedStatesAsync(
        string jobId,
        string stateName,
        int count,
        DateTime deadline,
        TimeSpan timeout)
    {
        var storage = _storage;
        if (storage is null)
        {
            return;
        }

        var monitoring = storage.GetMonitoringApi();
        while (true)
        {
            if (PersistedCount(monitoring, jobId, stateName) >= count)
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Job '{jobId}' applied state '{stateName}' in-process, but the storage of its host does not hold "
                    + $"{count} persisted application(s) of it after {timeout}.");
            }

            await Task.Delay(CommitPollInterval);
        }
    }

    /// <summary>How often the persisted history of <paramref name="jobId"/> holds <paramref name="stateName"/>.</summary>
    private static int PersistedCount(IMonitoringApi monitoring, string jobId, string stateName) =>
        monitoring.JobDetails(jobId)?.History?.Count(
            entry => string.Equals(entry.StateName, stateName, StringComparison.Ordinal)) ?? 0;

    /// <inheritdoc />
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(context);

        var states = _states.GetOrAdd(context.BackgroundJob.Id, _ => new ConcurrentQueue<string>());
        states.Enqueue(context.NewState.Name);

        if (_signals.TryGetValue($"{context.BackgroundJob.Id}|{context.NewState.Name}", out var signal))
        {
            signal.TrySetResult();
        }
    }

    /// <inheritdoc />
    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        // The observer only records applications; leaving a state is not of interest to a test.
    }

    /// <summary>This observer as a filter provider, for a host to prepend to its chain.</summary>
    public IJobFilterProvider AsFilterProvider()
    {
        var filters = new JobFilterCollection();
        filters.Add(this);

        return filters;
    }
}
