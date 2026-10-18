using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Domain.Series.Events;

namespace NightTales.Application.Features.Autopilot.Events;

/// <summary>
/// The real-time half of the owner-action notice (02.8.2 plan §5, UC6): it publishes the ids-only
/// <c>EpisodeUpdated</c> invalidation for the run's episode, so an open episode page refetches the verdict that
/// stopped the run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Realtime only.</b> The notice itself — the <c>autopilot_owner_action_required</c> e-mail and the
/// <c>autopilot.owner_action_required</c> audit row — is written by <c>AutopilotNotificationService</c> in the gate
/// verdict's own transaction, so this delivery has nothing to write: it opens no unit of work, queues no e-mail and
/// reads no profile.
/// </para>
/// <para>
/// <b>Idempotent by nature.</b> The dispatcher marks the <c>outbox_messages</c> row processed <em>after</em> this
/// handler returns and redelivers it after a crash; a repeated invalidation only makes the client refetch, so no
/// deduplication is needed here (09 README D11's delivery contract). That is why the notice's write half moved out
/// of this delivery and into the verdict's transaction instead.
/// </para>
/// <para>
/// <b>Ids only.</b> The user and episode ids are all the NOTIFY body carries (09 README BD11/WT54); the titles, the
/// reason code and the failing checks travel in the <c>GET /episodes/{id}</c> DTO the client refetches, never a
/// story text, a brief, a fact sheet or a source URL.
/// </para>
/// </remarks>
/// <param name="notifier">The producer seam of the real-time platform (09.8.1-T1).</param>
public sealed class AutopilotOwnerActionRequiredHandler(IRealtimeNotifier notifier)
    : IOutboxHandler<AutopilotOwnerActionRequired>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(
        AutopilotOwnerActionRequired domainEvent,
        OutboxContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        await notifier
            .NotifyAsync(
                domainEvent.UserId,
                new RealtimeEvent(RealtimeEventName.EpisodeUpdated, new RealtimeIds(EpisodeId: domainEvent.EpisodeId)),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
