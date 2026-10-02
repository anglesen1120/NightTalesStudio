using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Usage;
using NightTales.Domain.Usage;

namespace NightTales.Infrastructure.Persistence.Queries;

/// <summary>
/// The <see cref="IPriceCatalog"/> over <c>price_versions</c> (02 README D14). Rows are immutable, so a
/// version is cached forever by its number; the active-version lookup reads the (tiny) table in its
/// own scope each time, because a new row can appear at any boot of the other host.
/// </summary>
/// <param name="scopeFactory">Creates the scope the context is resolved from.</param>
public sealed class PriceCatalog(IServiceScopeFactory scopeFactory) : IPriceCatalog
{
    private readonly ConcurrentDictionary<int, PriceVersion> _byVersion = new();

    /// <inheritdoc />
    public async Task<PriceVersion> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

        var version = await db.PriceVersions
            .AsNoTracking()
            .Where(row => row.ActiveFrom <= queuedAt)
            .OrderByDescending(row => row.ActiveFrom)
            .ThenByDescending(row => row.Version)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No price_versions row is active at {queuedAt:O}: the OpenAi:Pricing seed has not run before this queue time.");

        return _byVersion.GetOrAdd(version.Version, version);
    }

    /// <inheritdoc />
    public async Task<PriceVersion> GetAsync(int version, CancellationToken ct)
    {
        if (_byVersion.TryGetValue(version, out var cached))
        {
            return cached;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

        var row = await db.PriceVersions.AsNoTracking().SingleOrDefaultAsync(item => item.Version == version, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"price_versions has no version {version}.");

        return _byVersion.GetOrAdd(version, row);
    }
}
