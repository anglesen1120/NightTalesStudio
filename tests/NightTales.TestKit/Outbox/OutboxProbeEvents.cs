using NightTales.Domain.Common;

namespace NightTales.TestKit.Outbox;

/// <summary>
/// The probe event of the outbox integration tests (09.2.4-T1). It is a real
/// <see cref="IDomainEvent"/> with a real <see cref="OutboxEventAttribute"/> name, so the harness's
/// <c>OutboxEventRegistry</c> resolves it exactly as it resolves a Domain event.
/// </summary>
/// <param name="AggregateId">The aggregate that raised it — the tests never need a real one.</param>
/// <param name="Label">A payload value the handler can assert on.</param>
/// <param name="OccurredAt">When it was raised; a stored row carries it as <c>occurred_at</c>.</param>
[OutboxEvent("probe_outbox_event.v1")]
public sealed record OutboxProbeEvent(Guid AggregateId, string Label, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The probe event that may run without a user: it carries <see cref="PlatformEventAttribute"/>, so the
/// interceptor stores it with a <c>null</c> user and the dispatcher delivers it without
/// <c>ISystemContext.For</c>.
/// </summary>
/// <param name="OccurredAt">When it was raised.</param>
[OutboxEvent("probe_outbox_platform_event.v1")]
[PlatformEvent]
public sealed record OutboxProbePlatformEvent(DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// What the always-failing probe handler throws (09.2.4-T1). Its type name is what the dispatcher stores
/// in <c>error</c>, so the tests assert on it.
/// </summary>
public sealed class OutboxProbeFailureException(string message) : Exception(message);
