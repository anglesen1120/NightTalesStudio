namespace NightTales.Application.Abstractions.Outbox;

/// <summary>
/// What one delivery of one outbox message tells its handlers (09 README D11).
/// </summary>
/// <param name="MessageId">The <c>outbox_messages.id</c> of the row being delivered.</param>
/// <param name="UserId">
/// The user the message runs as — the aggregate's <c>IUserOwned.UserId</c> — or <c>null</c> for a
/// platform event.
/// </param>
/// <param name="Attempt">The 1-based number of this delivery: <c>1</c> on the first try, <c>2</c> after one failure.</param>
/// <param name="OccurredAt">When the aggregate raised the event, not when it was dispatched.</param>
public sealed record OutboxContext(Guid MessageId, Guid? UserId, int Attempt, DateTimeOffset OccurredAt);
