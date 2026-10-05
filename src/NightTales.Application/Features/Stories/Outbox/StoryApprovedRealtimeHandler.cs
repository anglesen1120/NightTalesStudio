using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Domain.Stories.Events;

namespace NightTales.Application.Features.Stories.Outbox;

/// <summary>
/// Projects <c>story_approved.v1</c> to an ids-only episode invalidation for the owner's open pages.
/// </summary>
/// <remarks>
/// Delivery is at least once; a repeated invalidation only causes another refetch. Notifier failures
/// propagate so the dispatcher retries instead of marking the row processed.
/// </remarks>
/// <param name="notifier">The producer seam of the real-time platform.</param>
public sealed class StoryApprovedRealtimeHandler(IRealtimeNotifier notifier) : IOutboxHandler<StoryApproved>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(StoryApproved domainEvent, OutboxContext context, CancellationToken cancellationToken)
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
