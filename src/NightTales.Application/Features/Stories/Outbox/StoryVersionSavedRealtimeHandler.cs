using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Domain.Stories.Events;

namespace NightTales.Application.Features.Stories.Outbox;

/// <summary>
/// The <c>story_version_saved.v1</c> consumer (02.3.1-T1, 02 CR-7): tells the owner's open pages that the episode changed,
/// with <see cref="RealtimeEventName.EpisodeUpdated"/> and ids only, so the editor and the episode page refetch.
/// </summary>
/// <remarks>
/// Delivery is at least once, and a repeated invalidation only makes the client refetch once more. A notifier failure
/// propagates, so the dispatcher retries the row instead of marking it processed.
/// </remarks>
/// <param name="notifier">The producer seam of the real-time platform.</param>
public sealed class StoryVersionSavedRealtimeHandler(IRealtimeNotifier notifier) : IOutboxHandler<StoryVersionSaved>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(StoryVersionSaved domainEvent, OutboxContext context, CancellationToken cancellationToken)
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
