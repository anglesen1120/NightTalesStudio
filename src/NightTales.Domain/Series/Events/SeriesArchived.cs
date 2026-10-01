using NightTales.Domain.Common;

namespace NightTales.Domain.Series.Events;

/// <summary>
/// A series was archived (09 README D11/D16). Raised once — archiving an already archived series is a
/// no-op — and stored as <c>series_archived.v1</c> in the same command transaction as the update. No
/// handler is registered in this wave (09.4.1 plan §5).
/// </summary>
/// <param name="SeriesId">The archived series.</param>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("series_archived.v1")]
public sealed record SeriesArchived(Guid SeriesId, Guid UserId, DateTimeOffset OccurredAt) : IDomainEvent;
