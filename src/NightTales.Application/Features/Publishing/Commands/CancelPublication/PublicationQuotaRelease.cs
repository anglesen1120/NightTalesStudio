using ErrorOr;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Features.Publishing.Dtos;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Features.Publishing.Commands.CancelPublication;

/// <summary>
/// Gives back the quota unit a cancelled publication still holds (06 README D10, 06.2.6-T1 §Design step 5): the
/// release is made only when the row carries a reserved day, because D10 gives a unit back only for an upload
/// YouTube never received.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is not inline in the handler.</b> 06.4.1-T1's disconnect cancels every <c>awaiting_consent</c>/
/// <c>scheduled</c> publication of a channel with the same domain method and must release the same units (06
/// README D15), so the step is one reusable function of (row, ledger) and both callers share it instead of
/// growing a second copy of the rule.
/// </para>
/// <para>
/// <b>What counts as "nothing to release".</b> The key <c>platform_payload.quotaReservedDay</c> is written by the
/// upload job when it reserves and cleared by that job when it releases or finishes (06.2.3-T2), so its absence
/// says the row never passed the reserve step — a cancel of an <c>awaiting_consent</c>/<c>scheduled</c> row
/// usually finds no key at all. A key that is present but unusable (a hand-edited payload, a format change) is
/// the same "nothing to release", logged with the row's id and never an error: a cancel must not fail over a
/// payload nobody can read. Only the ledger's own refusal fails the command, which rolls the whole transaction
/// back — a committed cancel whose unit was never released would leave the bound open with nothing left to
/// release it.
/// </para>
/// <para>
/// <b>Never a row insert.</b> <see cref="IQuotaLedger.ReleaseAsync"/> decrements the day's existing row under a
/// <c>units_used &gt; 0</c> guard and is a no-op when the day has no row (a Pacific-midnight rollover between the
/// reserve and the cancel), so the counter never goes negative and no past day is ever created here. Nothing about
/// the row is logged but its id (06 README D17).
/// </para>
/// </remarks>
internal static partial class PublicationQuotaRelease
{
    /// <summary>Releases the row's reserved unit when the payload names a readable quota day.</summary>
    /// <param name="publication">The cancelled publication.</param>
    /// <param name="ledger">The ledger the unit is given back to.</param>
    /// <param name="logger">The log of an unusable key.</param>
    /// <param name="cancellationToken">Cancels the ledger statement.</param>
    /// <returns><c>Success</c>, or the ledger's own error.</returns>
    internal static async ValueTask<ErrorOr<Success>> ReleaseAsync(
        Publication publication,
        IQuotaLedger ledger,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(logger);

        var payload = publication.PlatformPayloadJson;
        var day = PublicationPlatformPayload.ReadQuotaReservedDay(payload);

        if (day is null)
        {
            if (PublicationPlatformPayload.IsQuotaReservedDayUnreadable(payload))
            {
                // The key is there and cannot be used: the release is skipped, the cancel is not failed, and the
                // line carries the row's id only.
                LogUnreadableReservedDay(logger, publication.Id);
            }

            return Result.Success;
        }

        // D10: the release is a decrement of the day's counter (never an insert), guarded by units_used > 0, so a
        // repeat of the same day's release cannot take the counter below zero.
        return await ledger
            .ReleaseAsync(new QuotaReservation(publication.Platform, day.Value), cancellationToken)
            .ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 6505,
        Level = LogLevel.Warning,
        Message = "publication_quota_reserved_day_unreadable publicationId={PublicationId}")]
    private static partial void LogUnreadableReservedDay(ILogger logger, Guid publicationId);
}
