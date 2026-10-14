using NightTales.Domain.Common;

namespace NightTales.Domain.Series.Events;

/// <summary>
/// A manual save replaced a series' bible content (02.6.3 plan §5): the outbox stores it as
/// <c>series_bible_updated.v1</c> in the same command transaction as the row, so the fact is observable to
/// the platform. Nothing consumes it in phase P — this wave registers no handler, exactly as
/// <see cref="SeriesCreated"/> — and a live <c>SeriesBibleUpdated</c> notification is a one-line handler
/// (09.4.1 plan §5).
/// </summary>
/// <remarks>
/// The event is raised by the <see cref="Series"/> aggregate, not by the bible row: <c>SeriesBible</c> is an
/// <see cref="Entity"/> and the outbox interceptor collects the domain events of an
/// <see cref="AggregateRoot"/> only. Ids and the version number only (02 D19): never a title, a name, a rule
/// or a summary.
/// </remarks>
/// <param name="SeriesId">The series whose bible the save replaced.</param>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="Version">The version the save produced (the value the <c>sb-&lt;version&gt;</c> ETag names).</param>
/// <param name="ChangedBy">The user who performed the save; the owner in phase P.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("series_bible_updated.v1")]
public sealed record SeriesBibleUpdated(
    Guid SeriesId,
    Guid UserId,
    int Version,
    Guid ChangedBy,
    DateTimeOffset OccurredAt) : IDomainEvent;
