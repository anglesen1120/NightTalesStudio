using NightTales.Application.Abstractions.Idempotency;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Domain.Common;

namespace NightTales.Application.Features.Platform.Jobs;

/// <summary>
/// Deletes the <c>idempotency_keys</c> rows whose replay window closed a further day ago (09.1.3 plan
/// §7, README D6). A key lives 24 h from its claim, so the grace day only keeps a slow retry of an
/// already-expired key replayable a little longer.
/// </summary>
/// <remarks>
/// The job is idempotent and safe to re-run: it deletes by <c>expires_at</c>, never by id, and the
/// batching lives in <see cref="IIdempotencyCleanupStore"/>.
/// </remarks>
public sealed class IdempotencyKeyCleanupJob(IIdempotencyCleanupStore store, IClock clock) : IBackgroundJob
{
    /// <summary>The grace period a row keeps after <c>expires_at</c> before this job removes it.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    /// <summary>Removes every row expired before <see cref="Grace"/> ago.</summary>
    public async Task RunAsync(CancellationToken cancellationToken) =>
        await store.DeleteExpiredAsync(clock.UtcNow - Grace, cancellationToken).ConfigureAwait(false);
}
