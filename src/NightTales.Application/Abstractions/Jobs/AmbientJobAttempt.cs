namespace NightTales.Application.Abstractions.Jobs;

/// <summary>
/// The ambient attempt of the job running on this execution context (09.6.3-T2 step 5): the Infrastructure job
/// filter writes it just before a job body runs and clears it when the attempt ends, and
/// <see cref="AmbientJobAttempt"/> reads it.
/// </summary>
/// <remarks>
/// <para>
/// It is <see cref="AsyncLocal{T}"/> and not a service property because the filter that knows the attempt number
/// runs <em>before</em> the job's own DI scope exists, and one Worker process runs several attempts in parallel:
/// a static field would hand one attempt's number to another job (and D12 keeps job arguments ids-only, so the
/// number cannot travel as an argument either).
/// </para>
/// <para>
/// The value flows forward only: the filter sets it on the context its job body then runs in, and
/// <see cref="Set"/>'s handle clears it again, so an attempt never reads the number of the attempt before it.
/// </para>
/// </remarks>
public static class JobAttemptScope
{
    private static readonly AsyncLocal<Attempt?> Current = new();

    /// <summary>The attempt of the job running on this execution context, or <see langword="null"/> outside one.</summary>
    public static (int Attempt, int MaxAttempts)? Value => Current.Value is { } attempt
        ? (attempt.Number, attempt.Maximum)
        : null;

    /// <summary>
    /// Publishes <paramref name="attempt"/> of <paramref name="maxAttempts"/> for the rest of this execution
    /// context.
    /// </summary>
    /// <param name="attempt">The 1-based attempt now running.</param>
    /// <param name="maxAttempts">How many attempts the job class is allowed in total.</param>
    /// <returns>A handle that restores the previous value; dispose it when the attempt ends.</returns>
    public static IDisposable Set(int attempt, int maxAttempts)
    {
        var previous = Current.Value;
        Current.Value = new Attempt(attempt, maxAttempts);

        return new Handle(previous);
    }

    /// <summary>One published attempt.</summary>
    /// <param name="Number">The 1-based attempt number.</param>
    /// <param name="Maximum">The total number of attempts allowed.</param>
    private sealed record Attempt(int Number, int Maximum);

    private sealed class Handle(Attempt? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Current.Value = previous;
        }
    }
}

/// <summary>
/// The <see cref="IJobAttempt"/> a job resolves: the ambient attempt the scheduling filter published, or the
/// single last attempt when nothing published one (see <see cref="IJobAttempt"/>'s remarks).
/// </summary>
public sealed class AmbientJobAttempt : IJobAttempt
{
    /// <inheritdoc />
    public int Attempt => JobAttemptScope.Value?.Attempt ?? 1;

    /// <inheritdoc />
    public int MaxAttempts => JobAttemptScope.Value?.MaxAttempts ?? 1;

    /// <inheritdoc />
    public bool IsLastAttempt => Attempt >= MaxAttempts;
}
