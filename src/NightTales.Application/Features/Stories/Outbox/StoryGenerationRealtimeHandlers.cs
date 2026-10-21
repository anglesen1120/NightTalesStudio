using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Domain.Stories.Generation.Events;

namespace NightTales.Application.Features.Stories.Outbox;

/// <summary>
/// The <c>story_generation_stage_changed.v1</c> consumer (02.2.2-T3, F24): one <see cref="RealtimeEventName.EpisodeUpdated"/>
/// per stage transition, ids only, so the episode page refetches the progress. The episode raises the event on transition
/// only, so a retried step does not spam the UI.
/// </summary>
/// <param name="notifier">The realtime notifier.</param>
public sealed class StoryGenerationStageChangedRealtimeHandler(IRealtimeNotifier notifier) : IOutboxHandler<StoryGenerationStageChanged>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(StoryGenerationStageChanged domainEvent, OutboxContext context, CancellationToken cancellationToken)
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

/// <summary>The <c>story_generated.v1</c> consumer (02.2.2-T3, D13): the episode is now in <c>story_review</c>.</summary>
/// <param name="notifier">The realtime notifier.</param>
public sealed class StoryGeneratedRealtimeHandler(IRealtimeNotifier notifier) : IOutboxHandler<StoryGenerated>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(StoryGenerated domainEvent, OutboxContext context, CancellationToken cancellationToken)
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

/// <summary>The <c>story_generation_failed.v1</c> consumer (02.2.2-T3, D13): the episode is back in its previous status.</summary>
/// <param name="notifier">The realtime notifier.</param>
public sealed class StoryGenerationFailedRealtimeHandler(IRealtimeNotifier notifier) : IOutboxHandler<StoryGenerationFailed>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(StoryGenerationFailed domainEvent, OutboxContext context, CancellationToken cancellationToken)
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
