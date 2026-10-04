using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Domain.Research.Events;

namespace NightTales.Application.Features.Research.Sources.Outbox;

/// <summary>
/// The <c>source_fetched.v1</c> consumer (02.5.2-T2, plan §5.5): tells the owner's open pages that the episode changed,
/// with <see cref="RealtimeEventName.EpisodeUpdated"/> and ids only, so the sources list refetches. No new event type
/// (02 CR-7).
/// </summary>
/// <remarks>
/// Delivery is at least once, and a repeated invalidation only makes the client refetch once more. A notifier
/// failure propagates, so the dispatcher retries the row instead of marking it processed.
/// </remarks>
/// <param name="notifier">The producer seam of the real-time platform.</param>
public sealed class SourceFetchedRealtimeHandler(IRealtimeNotifier notifier) : IOutboxHandler<SourceFetched>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(SourceFetched domainEvent, OutboxContext context, CancellationToken cancellationToken)
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
