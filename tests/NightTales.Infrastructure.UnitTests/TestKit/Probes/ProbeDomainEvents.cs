using NightTales.Domain.Common;

namespace NightTales.Infrastructure.UnitTests.TestKit.Probes;

/// <summary>
/// Test-only domain event of <see cref="ProbeAggregate"/> (09.2.4-T1): it carries the real
/// <see cref="OutboxEventAttribute"/>, so the outbox interceptor stores it under the name
/// <c>probe_domain_event.v1</c>. Never declared in <c>src</c>.
/// </summary>
/// <param name="AggregateId">The aggregate that raised it.</param>
/// <param name="Name">The name it was renamed to — the payload value the test asserts on.</param>
/// <param name="OccurredAt">When it was raised.</param>
[OutboxEvent("probe_domain_event.v1")]
internal sealed record ProbeDomainEvent(Guid AggregateId, string Name, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Test-only platform event (09.2.4-T1): an event of an aggregate that has no user, which the interceptor
/// may store with a <c>null</c> <c>user_id</c>.
/// </summary>
/// <param name="OccurredAt">When it was raised.</param>
[OutboxEvent("probe_platform_event.v1")]
[PlatformEvent]
internal sealed record ProbePlatformEvent(DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Test-only event that needs a user and has none: raised by <see cref="ProbePlatformAggregate"/>, which is
/// not <see cref="IUserOwned"/>, so the interceptor must refuse the command (09 README D10/D11).
/// </summary>
/// <param name="AggregateId">The aggregate that raised it.</param>
/// <param name="OccurredAt">When it was raised.</param>
[OutboxEvent("probe_unattributed_event.v1")]
internal sealed record ProbeUnattributedEvent(Guid AggregateId, DateTimeOffset OccurredAt) : IDomainEvent;
