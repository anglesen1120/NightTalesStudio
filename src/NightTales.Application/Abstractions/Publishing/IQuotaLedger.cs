using ErrorOr;

namespace NightTales.Application.Abstractions.Publishing;

/// <summary>
/// The platform upload quota ledger (06.3.1-T1, plan §3). It counts units per platform and Pacific day, so a
/// publish reserves its units before the upload is sent and releases them when the upload fails before any
/// bytes are accepted. The port names no provider SDK type.
/// </summary>
public interface IQuotaLedger
{
    /// <summary>
    /// Reserves one unit for <paramref name="platform"/> on the Pacific day of <paramref name="now"/>. Fails with
    /// a 429 <c>youtube_quota_exhausted</c> error (carrying <c>retryAfterSeconds</c>) when the day is spent.
    /// </summary>
    Task<ErrorOr<QuotaReservation>> TryReserveAsync(string platform, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Gives a reservation back. Succeeds even when the day row has nothing left to release.</summary>
    Task<ErrorOr<Success>> ReleaseAsync(QuotaReservation reservation, CancellationToken ct = default);

    /// <summary>
    /// Gives several reservations back in one call (06.4.1-T1 §Design step 6, STATUS S72-3). One reservation per
    /// unit: the same <c>(platform, day)</c> appearing twice releases two units, and an empty list is a success
    /// that touches nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the port grew a batch.</b> A disconnect cancels every open publication of one channel and gives their
    /// units back in the same unit of work; one <see cref="ReleaseAsync"/> call per row meant one decrement
    /// <em>and one gauge read-back</em> per row, so a channel with a handful of queued uploads published the
    /// <c>youtube_quota_used</c> gauge a handful of times for one logical change (S72-3). The batch lets the
    /// adapter answer the whole cascade with one statement per day and one gauge publication.
    /// </para>
    /// <para>
    /// <b>The default is the honest fallback.</b> The implementation loops <see cref="ReleaseAsync"/>, so an
    /// adapter — or a test double — that only knows the single release keeps the exact per-row semantics and
    /// stays source-compatible. An adapter that can do better (the real ledger, one statement per distinct day)
    /// overrides this member; nothing else in this layer needs to know which one it is talking to.
    /// </para>
    /// </remarks>
    /// <param name="reservations">One entry per unit to release; empty is a no-op success.</param>
    /// <param name="ct">Cancels the release.</param>
    /// <returns><c>Success</c>, or the first error a single release would have answered.</returns>
    async Task<ErrorOr<Success>> ReleaseManyAsync(
        IReadOnlyList<QuotaReservation> reservations,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reservations);

        foreach (var reservation in reservations)
        {
            var released = await ReleaseAsync(reservation, ct).ConfigureAwait(false);

            if (released.IsError)
            {
                return released.Errors;
            }
        }

        return Result.Success;
    }
}
