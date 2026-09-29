using System.Collections.Concurrent;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;

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
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _signals = new(StringComparer.Ordinal);

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
    /// <paramref name="count"/> times (a retry is a second <c>Scheduled</c> application).
    /// </summary>
    public async Task WaitForStatesAsync(string jobId, string stateName, int count, TimeSpan timeout)
    {
        var key = $"{jobId}|{stateName}";

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

            await signal.Task.WaitAsync(timeout);

            // The signal is single-shot: replace the completed one so the next iteration blocks again.
            _signals.TryUpdate(
                key,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                signal);
        }
    }

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
