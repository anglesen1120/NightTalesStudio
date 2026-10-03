using Microsoft.EntityFrameworkCore;
using NightTales.Application.Features.Platform.Retention;
using NightTales.Domain.Assets;

namespace NightTales.Infrastructure.Persistence;

/// <summary>
/// The per-row asset access of <c>RetentionSweepJob</c> (09.7.3-T1). It runs in the row's own scope after
/// <c>ISystemContext.For(userId)</c>, so the tenant filter on <c>assets</c> stays on and resolves to the
/// owner; there is no <c>IgnoreQueryFilters</c> here.
/// </summary>
/// <param name="database">The scope's write context, created after the scope was pinned.</param>
public sealed class AssetRetentionStore(AppDbContext database) : IAssetRetentionStore
{
    /// <inheritdoc />
    public Task<Asset?> FindLiveAsync(Guid assetId, CancellationToken ct) =>
        database.Assets.FirstOrDefaultAsync(asset => asset.Id == assetId && asset.DeletedAt == null, ct);

    /// <inheritdoc />
    public async Task MarkDeletedAsync(Asset asset, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(asset);

        // A tracked change, so AuditableSaveChangesInterceptor stamps updated_at in the same save (09 W3
        // CR-38); a set-based update would bypass it.
        asset.MarkDeleted(now);
        await database.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
