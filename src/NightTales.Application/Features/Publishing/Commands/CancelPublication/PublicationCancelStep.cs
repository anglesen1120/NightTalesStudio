using ErrorOr;
using NightTales.Application.Features.Publishing.Dtos;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Features.Publishing.Commands.CancelPublication;

/// <summary>
/// The one place that moves a publication to <c>cancelled</c> (06.2.6-T1 §Design step 4) and records what the audit
/// projection needs: the status the row left and who cancelled it (06 README D17).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two callers, one rule.</b> The cancel route cancels one row for its owner (<c>actor</c> =
/// <see cref="CancelAuditKeys.ActorUser"/>) and 06.4.1-T1's disconnect cancels every open row of a channel
/// (<c>actor</c> = <see cref="CancelAuditKeys.ActorDisconnect"/>, STATUS S72-4). Both need the same aggregate call and
/// the same pre-transition status, so the step lives here instead of growing a second copy in the disconnect.
/// </para>
/// <para>
/// <b>The aggregate owns the state machine.</b> <see cref="Publication.Cancel"/> accepts <c>awaiting_consent</c> and
/// <c>scheduled</c>, treats <c>cancelled</c> as a no-op and refuses every other status with the 409
/// <c>publication_invalid_state</c>; this step only reads the value it is about to overwrite.
/// </para>
/// </remarks>
internal static class PublicationCancelStep
{
    /// <summary>Cancels one loaded row and returns the ids and the actor the audit row reports.</summary>
    /// <param name="publication">The row to cancel; the caller has loaded (and, on the route paths, locked) it.</param>
    /// <param name="now">The cancel instant; one instant is used for the whole command.</param>
    /// <param name="actor">The frozen <c>actor</c> value (<see cref="CancelAuditKeys"/>).</param>
    /// <returns>The projection, or the aggregate's own refusal.</returns>
    internal static ErrorOr<PublicationCancelledOutcome> Cancel(
        Publication publication,
        DateTimeOffset now,
        string actor)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        // Read before the transition: the status is the only history the row keeps, because the state itself is
        // overwritten.
        var previousStatus = PublicationWire.Status(publication.Status);

        if (publication.Cancel(now) is { IsError: true } refused)
        {
            return refused.Errors;
        }

        return new PublicationCancelledOutcome(publication.Id, publication.EpisodeId, previousStatus, actor);
    }
}
