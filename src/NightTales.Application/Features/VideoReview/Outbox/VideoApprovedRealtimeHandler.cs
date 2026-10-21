using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Domain.Episodes.Events;

namespace NightTales.Application.Features.VideoReview.Outbox;

/// <summary>
/// Projects <c>video_approved.v1</c> to an ids-only episode invalidation for the owner's open pages.
/// </summary>
/// <remarks>
/// Delivery is at least once; a repeated invalidation only causes another refetch. Notifier failures propagate
/// so the dispatcher retries instead of marking the row processed.
/// </remarks>
/// <param name="notifier">The producer seam of the real-time platform.</param>
public sealed class VideoApprovedRealtimeHandler(IRealtimeNotifier notifier) : IOutboxHandler<VideoApproved>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(
        VideoApproved domainEvent,
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
