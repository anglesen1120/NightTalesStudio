using ErrorOr;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Usage;

namespace NightTales.Infrastructure.Money;

/// <summary>
/// The phase-P <see cref="ICreditLedger"/> (02 README D1, F03): there are no credits yet, so it only meters.
/// <see cref="ReserveAsync"/> hands out a fresh reservation id and never rejects; nothing is written to
/// <c>credit_ledger</c> or <c>credit_lots</c>; <see cref="SettleAsync"/> and <see cref="RefundAsync"/> succeed
/// without effect, which also makes a repeated refund of one reservation idempotent. 12.1.2 replaces the
/// registration at M2 (02 CR-2).
/// </summary>
public sealed class MeteringOnlyCreditLedger : ICreditLedger
{
    /// <inheritdoc />
    public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
        Guid userId,
        CreditOperation op,
        Guid referenceId,
        UsageBound bound,
        CancellationToken ct) =>
        ValueTask.FromResult<ErrorOr<ReservationId>>(new ReservationId(Guid.CreateVersion7()));

    /// <inheritdoc />
    public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct) =>
        ValueTask.FromResult<ErrorOr<Success>>(Result.Success);

    /// <inheritdoc />
    public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct) =>
        ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
}
