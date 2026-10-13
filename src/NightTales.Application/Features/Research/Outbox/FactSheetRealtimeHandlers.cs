using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Domain.Research.Events;

namespace NightTales.Application.Features.Research.Outbox;

/// <summary>
/// The <c>fact_sheet_generated.v1</c> consumer (02.5.3-T2, plan §5): tells the owner's open pages that the
/// episode's fact sheet changed, with <see cref="RealtimeEventName.EpisodeUpdated"/> and ids only, so the
/// research tab refetches the new draft version. The version number and the sheet id travel in the
/// <c>GET /episodes/{id}</c> DTO the client refetches, never in the NOTIFY body (09 README BD11/WT54).
/// </summary>
/// <param name="notifier">The realtime notifier.</param>
public sealed class FactSheetGeneratedRealtimeHandler(IRealtimeNotifier notifier) : IOutboxHandler<FactSheetGenerated>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(FactSheetGenerated domainEvent, OutboxContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        await notifier
            .NotifyAsync(
                domainEvent.UserId,
                new RealtimeEvent(
                    RealtimeEventName.EpisodeUpdated,
                    new RealtimeIds(EpisodeId: domainEvent.EpisodeId, SeriesId: domainEvent.SeriesId)),
                cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// The <c>fact_sheet_generation_failed.v1</c> consumer (02.5.3-T2, plan §5): the run ended without a draft, so the
/// research tab stops showing a generating panel. Ids only — the D8 code and the run are read back through
/// <c>GET /episodes/{id}</c> (09 README BD11/WT54: no new event type, F24).
/// </summary>
/// <param name="notifier">The realtime notifier.</param>
public sealed class FactSheetGenerationFailedRealtimeHandler(IRealtimeNotifier notifier) : IOutboxHandler<FactSheetGenerationFailed>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(FactSheetGenerationFailed domainEvent, OutboxContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        await notifier
            .NotifyAsync(
                domainEvent.UserId,
                new RealtimeEvent(
                    RealtimeEventName.EpisodeUpdated,
                    new RealtimeIds(EpisodeId: domainEvent.EpisodeId, SeriesId: domainEvent.SeriesId)),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
