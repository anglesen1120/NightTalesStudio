namespace NightTales.Application.Features.Platform.Retention;

/// <summary>
/// The single-flight guard of <c>RetentionSweepJob</c> (09.7.3-T1): two Worker instances, or a Hangfire
/// retry that overlaps a slow run, must never sweep at once.
/// </summary>
/// <remarks>
/// The PostgreSQL implementation takes <c>pg_try_advisory_lock(hashtext('retention_sweep'))</c> on a
/// session, so a Worker that dies mid-sweep releases the lock with its connection.
/// </remarks>
public interface IRetentionSweepLock
{
    /// <summary>Tries to take the lock without waiting.</summary>
    /// <param name="ct">Cancels the statement.</param>
    /// <returns>
    /// The handle whose disposal releases the lock, or <see langword="null"/> when another run holds it.
    /// </returns>
    Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct);
}
