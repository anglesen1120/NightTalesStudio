using Mediator;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Features.Episodes.Commands.MarkEpisodeAssetsForRetention;
using NightTales.Domain.Episodes.Events;

namespace NightTales.Application.Features.Episodes.Outbox;

/// <summary>
/// The <c>episode_cancelled.v1</c> consumer (09.5.1 plan §7): it turns a cancel into the retention
/// schedule of the episode's assets, through <see cref="ISender"/> so the D4 pipeline and its unit of
/// work apply (D11).
/// </summary>
/// <remarks>
/// <para>
/// It changes data, so it dispatches a command instead of writing with a raw <c>SaveChanges</c>: the
/// delivery scope is already pinned to the message's user (<c>ISystemContext.For</c>), which is what makes
/// the tenant-filtered <c>UPDATE</c> touch only that user's rows.
/// </para>
/// <para>
/// Delivery is at least once (09 README D11/O4), so the work it sends is idempotent — a redelivery
/// re-runs the same statement and leaves the same values. An error result is <em>not</em> swallowed: a
/// transient commit failure (503 <c>service_unavailable</c>) must leave the outbox row due so the
/// dispatcher retries, instead of marking the message processed with the assets unscheduled.
/// </para>
/// </remarks>
/// <param name="sender">The in-process mediator of the delivery's scope.</param>
public sealed class EpisodeCancelledAssetRetentionHandler(ISender sender) : IOutboxHandler<EpisodeCancelled>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(
        EpisodeCancelled domainEvent,
        OutboxContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var result = await sender
            .Send(new MarkEpisodeAssetsForRetentionCommand(domainEvent.EpisodeId), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsError)
        {
            // The codes only, never content: the dispatcher stores this text in the outbox row's `error`
            // column (09 README D17).
            throw new InvalidOperationException(
                "MarkEpisodeAssetsForRetentionCommand failed: "
                + string.Join(", ", result.Errors.Select(error => error.Code)));
        }
    }
}
