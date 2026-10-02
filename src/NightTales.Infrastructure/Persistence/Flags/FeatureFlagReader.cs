using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;

namespace NightTales.Infrastructure.Persistence.Flags;

/// <summary>
/// Reads one flag row (09.9.2-T2 §Read port and cache): the only database access the read service has, so
/// the cache window is the only thing that can hide a change.
/// </summary>
/// <remarks>
/// The port exists so <c>CachedFeatureFlags</c>' clock and failure semantics are unit-testable without a
/// database, and so a second implementation (a rules-aware reader in 06.5.2, S) can replace it without
/// touching the cache.
/// </remarks>
public interface IFeatureFlagReader
{
    /// <summary>The flag's state, or <see langword="null"/> when no row has that key.</summary>
    /// <param name="key">The flag key; the caller validated its shape.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    Task<bool?> GetAsync(string key, CancellationToken ct);
}

/// <summary>
/// The one-row EF read behind the port (09.9.2 plan §5.1): one indexed primary-key lookup, no tracking
/// and no graph, so a guarded endpoint costs at most <c>1 / CacheSeconds</c> statements per key.
/// </summary>
/// <remarks>
/// It reads through the same <c>feature_flags</c> configuration the writer uses. The table is platform
/// data and not <c>IUserOwned</c> (09 README D10), so the tenant filter never applies and no
/// <c>IgnoreQueryFilters</c> is needed — a filter that did apply would be a bug, not something to escape.
/// The <c>rules</c> column is not selected: P answers from <c>enabled</c> only
/// <b>[RULED — README §B10 WT13]</b>.
/// </remarks>
/// <param name="database">The write model, which owns the flag configuration.</param>
public sealed class FeatureFlagReader(IAppDbContext database) : IFeatureFlagReader
{
    /// <inheritdoc />
    public async Task<bool?> GetAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return await database.FeatureFlags
            .AsNoTracking()
            .Where(flag => flag.Key == key)
            .Select(flag => (bool?)flag.Enabled)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }
}
