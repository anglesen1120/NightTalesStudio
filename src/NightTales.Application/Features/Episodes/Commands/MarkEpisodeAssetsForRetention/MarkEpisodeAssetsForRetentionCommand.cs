using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;

namespace NightTales.Application.Features.Episodes.Commands.MarkEpisodeAssetsForRetention;

/// <summary>
/// Sets <c>retention_until</c> on every live asset of one episode (09.5.1 plan §7), the first half of the
/// retention flow F14 fixes: cancelling an episode schedules its files for the 09.7.3 sweep without
/// deleting anything here.
/// </summary>
/// <remarks>
/// The command is sent by <c>EpisodeCancelledAssetRetentionHandler</c> from the Worker, not by a route,
/// and it is idempotent by contract: the outbox delivers at least once, so a second delivery must be
/// harmless (09 README D11/O4). It is a command rather than a raw <c>SaveChanges</c> in the handler so
/// the D4 pipeline — and the unit of work it opens — own the transaction (D11, 09.5.1 plan §7).
/// </remarks>
/// <param name="EpisodeId">The cancelled episode whose assets are scheduled.</param>
[NotAudited("The Worker's follow-up of episode.deleted, whose row the deleting command already wrote; this one only schedules that episode's assets.")]
public sealed record MarkEpisodeAssetsForRetentionCommand(Guid EpisodeId) : ICommand<ErrorOr<Success>>;
