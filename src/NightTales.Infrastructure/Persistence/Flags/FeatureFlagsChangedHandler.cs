using Microsoft.Extensions.Caching.Memory;
using NightTales.Application.Abstractions.Flags;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Domain.Platform.Events;

namespace NightTales.Infrastructure.Persistence.Flags;

/// <summary>
/// The Worker's half of the flag eviction (09 README BD13, 09.9.2-T2 §Eviction): it drops the changed key
/// from this instance's cache when a <c>feature_flags_changed.v1</c> message is delivered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Evict only, and idempotent by contract.</b> The outbox delivers at least once (09 README D11), so a
/// redelivery repeats a <c>Remove</c> of a key that is simply absent afterwards — there is no state to
/// corrupt and nothing to deduplicate. The handler takes no transaction and writes no row: the audit and
/// the row update belong to the writer (BD15 keeps the audit row out of the handler).
/// </para>
/// <para>
/// The 30 s window is the backstop if this message — or the writer's <c>NOTIFY</c> — is lost, which is why
/// a failed delivery is recoverable and never a correctness problem.
/// </para>
/// </remarks>
/// <param name="cache">The process-wide cache the reader fills.</param>
public sealed class FeatureFlagsChangedHandler(IMemoryCache cache) : IOutboxHandler<FeatureFlagsChanged>
{
    /// <inheritdoc />
    public ValueTask HandleAsync(
        FeatureFlagsChanged domainEvent,
        OutboxContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        cancellationToken.ThrowIfCancellationRequested();

        if (FeatureFlagKey.IsValid(domainEvent.Key))
        {
            cache.Remove(FeatureFlagsCache.Key(domainEvent.Key));
        }

        return ValueTask.CompletedTask;
    }
}
