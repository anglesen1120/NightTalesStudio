using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Flags;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Persistence.Flags;

/// <summary>
/// The only implementation of <see cref="IFeatureFlags"/> (09 README BD13, 09.9.2 plan §5.1): it serves
/// the value loaded in the last <c>FeatureFlags:CacheSeconds</c> seconds (default 30) and reads again
/// once the window is over.
/// </summary>
/// <remarks>
/// <para>
/// <b>The window is measured against <see cref="IClock"/>, not the cache's own expiry.</b> The entry
/// carries its <c>LoadedAt</c> and is re-read once <c>now − LoadedAt</c> reaches the window, so a test
/// moves a FakeClock instead of sleeping and the window stays one contract instead of two that can drift.
/// The <c>AbsoluteExpirationRelativeToNow</c> on the entry is only a memory bound: a key nothing asks for
/// again does not sit in the cache forever.
/// </para>
/// <para>
/// <b>Fail-closed, never throwing.</b> An invalid key answers <see langword="false"/> without a lookup; a
/// key no row has is cached as <see langword="false"/> for the window (a legitimate answer, and the one
/// the design fixes); a failed read answers <see langword="false"/> and is deliberately <em>not</em>
/// cached, so a database that recovers is believed immediately and a transient failure never becomes a
/// 30-second value. The caller — a guarded route, a job or the CLI — therefore always gets a boolean and
/// never a 500.
/// </para>
/// <para>
/// Eviction overrides the window: the writer's <c>NOTIFY</c> reaches the invalidator and the Worker's
/// outbox handler, and both drop the key through <see cref="FeatureFlagsCache.Key"/>.
/// </para>
/// </remarks>
/// <param name="reader">The one-row database read behind the cache.</param>
/// <param name="cache">The process-wide cache shared with the invalidator and the outbox handler.</param>
/// <param name="clock">The scope's clock; the cache window is measured against it.</param>
/// <param name="options">The configured window.</param>
/// <param name="logger">The rate-limited warning lines; never a user or a payload.</param>
public sealed partial class CachedFeatureFlags(
    IFeatureFlagReader reader,
    IMemoryCache cache,
    IClock clock,
    IOptions<FeatureFlagsOptions> options,
    ILogger<CachedFeatureFlags> logger) : IFeatureFlags
{
    /// <inheritdoc />
    public async Task<bool> IsEnabledAsync(string key, CancellationToken ct)
    {
        if (!FeatureFlagKey.IsValid(key))
        {
            // An invalid key cannot name a row: fail closed before any lookup, so a typo in a route's
            // guard costs nothing and never probes the table.
            return false;
        }

        var cacheKey = FeatureFlagsCache.Key(key);
        if (cache.TryGetValue(cacheKey, out FeatureFlagsCacheEntry? entry)
            && entry is not null
            && clock.UtcNow - entry.LoadedAt < options.Value.CacheWindow)
        {
            return entry.Enabled;
        }

        bool enabled;
        try
        {
            enabled = await reader.GetAsync(key, ct).ConfigureAwait(false) ?? false;

            if (!enabled && WarnOnce(key))
            {
                // A key with no enabled row: one line per key per minute, with the key only. A typo in a
                // route's guard has to be findable without logging every request (09.9.2-T2 failure table).
                LogMissingRow(key);
            }
        }
        catch (OperationCanceledException)
        {
            // A cancelled caller is a cancelled request, not an error: it must reach the caller.
            throw;
        }
        catch (Exception exception)
        {
            // Fail closed and do not cache. The failure's *type* is logged, never its message: a provider
            // message can carry the connection string (09 README D17).
            if (WarnOnce(key))
            {
                LogReadFailed(key, exception.GetType().Name);
            }

            return false;
        }

        cache.Set(
            cacheKey,
            new FeatureFlagsCacheEntry(enabled, clock.UtcNow),
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = options.Value.CacheWindow });

        return enabled;
    }

    /// <summary>
    /// Whether this key may log now, and marks it so for
    /// <see cref="FeatureFlagsCache.WarningWindow"/>. The marker is a plain bit with its own short expiry,
    /// never a flag value, and it is shared by both warnings: one line per key per minute, whatever the
    /// reason.
    /// </summary>
    /// <param name="key">The flag key the warning is about.</param>
    private bool WarnOnce(string key)
    {
        var warnKey = FeatureFlagsCache.WarnKey(key);
        if (cache.TryGetValue(warnKey, out bool _))
        {
            return false;
        }

        cache.Set(warnKey, true, FeatureFlagsCache.WarningWindow);

        return true;
    }

    [LoggerMessage(EventId = 2322, Level = LogLevel.Warning, Message = "flags.read.missing key={Key}")]
    private partial void LogMissingRow(string key);

    [LoggerMessage(EventId = 2323, Level = LogLevel.Warning, Message = "flags.read.failed key={Key} type={FailureType}")]
    private partial void LogReadFailed(string key, string failureType);
}
