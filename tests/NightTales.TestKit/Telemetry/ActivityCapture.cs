using System.Diagnostics;

namespace NightTales.TestKit.Telemetry;

/// <summary>
/// Collects every activity of every source while it is alive (09.9.3-T2). Start it <em>after</em> the host
/// under test has started: <see cref="ActivityListener"/>s are called in registration order, so a listener
/// added later sees the activity after the OpenTelemetry pipeline's own processors have run — the state an
/// exporter would read. Reads are lazy: tag values are taken when asked, never copied early.
/// </summary>
public sealed class ActivityCapture : IDisposable
{
    private readonly List<Activity> _activities = [];
    private readonly List<(Func<Activity, bool> Predicate, TaskCompletionSource<Activity> Source)> _waiters = [];
    private readonly ActivityListener _listener;

    /// <summary>Starts listening to every <see cref="ActivitySource"/>.</summary>
    public ActivityCapture()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = static _ => true,
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_activities)
                {
                    _activities.Add(activity);
                    foreach (var waiter in _waiters.Where(w => w.Predicate(activity)).ToList())
                    {
                        _waiters.Remove(waiter);
                        waiter.Source.TrySetResult(activity);
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>A snapshot of every activity stopped so far.</summary>
    public IReadOnlyList<Activity> Activities
    {
        get
        {
            lock (_activities)
            {
                return [.. _activities];
            }
        }
    }

    /// <summary>
    /// Completes when an activity matching <paramref name="predicate"/> has stopped — immediately when one
    /// already has. The server finishes a request's activity after the client has its response, so a test
    /// that asserts on the request span awaits this instead of sleeping.
    /// </summary>
    /// <param name="predicate">Selects the activity.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <returns>The matching activity.</returns>
    public Task<Activity> WhenStopped(Func<Activity, bool> predicate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (_activities)
        {
            if (_activities.Find(a => predicate(a)) is { } existing)
            {
                return Task.FromResult(existing);
            }

            var source = new TaskCompletionSource<Activity>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((predicate, source));

            return source.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Every attribute value, status description and event attribute of every captured activity as one text —
    /// the surface a raw secret could leak through.
    /// </summary>
    /// <returns>One line per activity.</returns>
    public string AllText() =>
        string.Join('\n', Activities.Select(activity =>
            activity.OperationName
            + " | " + string.Join(' ', activity.TagObjects.Select(tag => tag.Key + "=" + tag.Value))
            + " | " + activity.StatusDescription
            + " | " + string.Join(
                ' ', activity.Events.SelectMany(e => e.Tags).Select(tag => tag.Key + "=" + tag.Value))));

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();
}
