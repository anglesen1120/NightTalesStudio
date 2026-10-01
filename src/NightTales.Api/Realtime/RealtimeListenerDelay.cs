namespace NightTales.Api.Realtime;

/// <summary>
/// The reconnect pause of the listener's loop (09.8.1 plan §9: the schedule is asserted without
/// waiting).
/// </summary>
/// <remarks>
/// The loop has exactly one thing to wait for between reconnect attempts, and a test must be able to
/// walk the whole schedule (1, 2, 4, 8, 16, 30 s — 09 README BD11) without spending it. Injecting the
/// pause keeps production at <see cref="Task.Delay(TimeSpan, CancellationToken)"/> and makes the
/// schedule observable.
/// </remarks>
public interface IRealtimeListenerDelay
{
    /// <summary>Waits <paramref name="delay"/>, or throws <see cref="OperationCanceledException"/> on shutdown.</summary>
    /// <param name="delay">How long to wait.</param>
    /// <param name="cancellationToken">The loop's shutdown token.</param>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>The production pause: a plain <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
public sealed class SystemRealtimeListenerDelay : IRealtimeListenerDelay
{
    /// <inheritdoc />
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
