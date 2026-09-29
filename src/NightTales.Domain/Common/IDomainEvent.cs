namespace NightTales.Domain.Common;

/// <summary>
/// A domain event raised by an aggregate (09 README D3). Payloads carry ids, enums and small
/// values only; the outbox interceptor (09.2.4-T1) turns them into <c>outbox_messages</c> rows.
/// </summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}
