using NightTales.Domain.Common;

namespace NightTales.Domain.Series.Events;

/// <summary>
/// A series was created (09 README D11/D16). The outbox interceptor stores it as
/// <c>series_created.v1</c> in the same command transaction as the row, and the Worker dispatcher
/// delivers it once a handler exists — this wave registers none (09.4.1 plan §5), so a message of
/// this type is marked processed with nothing to run.
/// </summary>
/// <param name="SeriesId">The created series.</param>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("series_created.v1")]
public sealed record SeriesCreated(Guid SeriesId, Guid UserId, DateTimeOffset OccurredAt) : IDomainEvent;
