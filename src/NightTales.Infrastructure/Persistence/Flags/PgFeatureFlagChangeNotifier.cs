using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Flags;
using NightTales.Domain.Common;
using NightTales.Domain.Platform.Events;
using NightTales.Infrastructure.Persistence.Outbox;

namespace NightTales.Infrastructure.Persistence.Flags;

/// <summary>
/// The one implementation of <see cref="IFeatureFlagChangeNotifier"/> (09 README BD13, 09.9.2-T2
/// §Eviction): it queues the <c>feature_flags_changed.v1</c> outbox row and writes
/// <c>SELECT pg_notify('feature_flags_changed', &lt;key&gt;)</c> — both on the caller's context, so both
/// join the writer's transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two paths, one transaction.</b> The <c>NOTIFY</c> is the ruled cross-instance signal
/// <b>[RULED — README §B10 WT13]</b>: PostgreSQL delivers it <b>at commit</b> and discards it on
/// rollback, so a failed <c>flags set</c> never announces a change that did not happen. The outbox row is
/// the second path the read service honours — the Worker's <c>FeatureFlagsChangedHandler</c> evicts on
/// delivery — and it is written here because the CLI writer goes through <c>ExecuteUpdateAsync</c> and
/// <c>FeatureFlag</c> is not an <c>AggregateRoot</c>, so <c>OutboxSaveChangesInterceptor</c> would never
/// see an event raised by an aggregate.
/// </para>
/// <para>
/// The row is built to the interceptor's own contract (<c>OutboxSaveChangesInterceptor.ToRow</c>): the
/// stable <c>type</c> name resolved through <see cref="OutboxEventRegistry"/>, the payload serialized with
/// <see cref="OutboxJson.Options"/> for that same concrete type, <c>user_id</c> null (the event is
/// <c>[PlatformEvent]</c>, so the dispatcher delivers it without pinning a tenant), <c>occurred_at</c> and
/// <c>next_attempt_at</c> from the scope's clock, the current activity as <c>trace_parent</c>, and the
/// column defaults for <c>attempts</c> and <c>created_at</c>. Nothing else writes a row into
/// <c>outbox_messages</c>, so the two producers must stay identical; a change to one belongs in both.
/// </para>
/// <para>
/// The payload is the flag key and its new state (at most ~100 bytes), far under PostgreSQL's 8,000-byte
/// <c>NOTIFY</c> cap, so there is no drop path and no truncation.
/// </para>
/// </remarks>
/// <param name="database">The caller's write context, whose transaction both writes join.</param>
/// <param name="events">Resolves the stable outbox <c>type</c> name of the event.</param>
/// <param name="clock">The scope's clock; the row's instant is the change's instant.</param>
public sealed class PgFeatureFlagChangeNotifier(
    AppDbContext database,
    OutboxEventRegistry events,
    IClock clock) : IFeatureFlagChangeNotifier
{
    /// <summary>
    /// The one statement this notifier issues besides the outbox row: a single <c>SELECT pg_notify</c> with
    /// the key as its only parameter. The channel is a literal built from the one constant, never input —
    /// PostgreSQL does not parameterize identifiers.
    /// </summary>
    public const string NotifySql = "SELECT pg_notify('" + FeatureFlagsCache.ChangedChannel + "', {0})";

    /// <inheritdoc />
    public async Task NotifyChangedAsync(string key, bool enabled, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var now = clock.UtcNow;
        var changed = new FeatureFlagsChanged(key, enabled, now);

        // The outbox row joins the transaction UnitOfWorkBehavior opened, so it is committed by the same
        // SaveChanges that writes the audit row (D5): the row, the audit row and the notification commit or
        // roll back together.
        database.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = events.NameOf(typeof(FeatureFlagsChanged)),
            Payload = Serialize(changed),
            UserId = null,
            OccurredAt = now,
            NextAttemptAt = now,
            TraceParent = Activity.Current?.Id,
        });

        await database.Database
            .ExecuteSqlRawAsync(NotifySql, [key], ct)
            .ConfigureAwait(false);
    }

    /// <summary>The event as the payload JSON of its own concrete type — the interceptor's own shape.</summary>
    private static string Serialize(FeatureFlagsChanged changed)
    {
        using var document = JsonSerializer.SerializeToDocument(changed, OutboxJson.Options);

        return document.RootElement.GetRawText();
    }
}
