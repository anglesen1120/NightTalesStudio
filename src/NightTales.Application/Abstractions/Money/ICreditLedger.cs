using ErrorOr;
using NightTales.Application.Abstractions.Usage;

namespace NightTales.Application.Abstractions.Money;

/// <summary>
/// The credit ledger port (02 README D1). In phase P it is metering-only (<c>MeteringOnlyCreditLedger</c>:
/// it never rejects and writes nothing); 12.1.2 replaces the registration at M2 with a ledger that debits lots
/// and may answer <c>insufficient_credits</c> from <see cref="ReserveAsync"/>.
/// </summary>
public interface ICreditLedger
{
    /// <summary>Reserves the worst case of one paid operation before it is dispatched.</summary>
    /// <param name="userId">The user the operation is for.</param>
    /// <param name="op">What is being paid for.</param>
    /// <param name="referenceId">The episode (or series) the operation belongs to.</param>
    /// <param name="bound">The worst-case bound that is reserved.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The reservation id, or a typed error that stops the command.</returns>
    ValueTask<ErrorOr<ReservationId>> ReserveAsync(
        Guid userId,
        CreditOperation op,
        Guid referenceId,
        UsageBound bound,
        CancellationToken ct);

    /// <summary>Settles a reservation at the cost recorded in <c>usage_records</c> for it.</summary>
    /// <param name="reservationId">The reservation to settle.</param>
    /// <param name="ct">Cancels the call.</param>
    ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct);

    /// <summary>Releases a reservation without a charge; repeating it for the same reservation is a success.</summary>
    /// <param name="reservationId">The reservation to release.</param>
    /// <param name="reason">A short, safe reason (never content).</param>
    /// <param name="ct">Cancels the call.</param>
    ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct);
}
