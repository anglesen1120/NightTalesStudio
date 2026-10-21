using NightTales.Domain.Common;

namespace NightTales.Domain.Platform.Events;

/// <summary>
/// A platform feature flag changed (09 README BD13, 09.9.2 plan §5.4): the outbox stores it as
/// <c>feature_flags_changed.v1</c> <b>[RULED — README §B10 WT13]</b> and the Worker's
/// <c>FeatureFlagsChangedHandler</c> drops the key from that instance's read cache.
/// </summary>
/// <remarks>
/// <para>
/// It is a platform event — nothing here belongs to a user — which is why <c>user_id</c> is
/// <see langword="null"/> and the dispatcher delivers it without pinning a tenant (09 README D10/D11).
/// </para>
/// <para>
/// The payload is the whole eviction signal: a consumer needs the key and nothing else. <c>Enabled</c>
/// travels with it because the writer already knows it, so a future reader can log or re-derive state
/// without a second query.
/// </para>
/// </remarks>
/// <param name="Key">The flag that changed.</param>
/// <param name="Enabled">The state it now has.</param>
/// <param name="OccurredAt">When the change committed, from the writer's clock.</param>
[OutboxEvent("feature_flags_changed.v1")]
[PlatformEvent]
public sealed record FeatureFlagsChanged(string Key, bool Enabled, DateTimeOffset OccurredAt) : IDomainEvent;
