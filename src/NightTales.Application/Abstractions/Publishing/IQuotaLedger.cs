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
}
