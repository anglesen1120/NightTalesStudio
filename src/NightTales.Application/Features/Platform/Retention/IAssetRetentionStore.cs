using NightTales.Domain.Assets;

namespace NightTales.Application.Features.Platform.Retention;

/// <summary>
/// The per-row asset access of <c>RetentionSweepJob</c> (09.7.3-T1). It is resolved inside the row's own
/// DI scope, after <c>ISystemContext.For(userId)</c>, so the tenant filter of every call resolves to the
/// asset's owner (09 README D10): the pinned <c>ISystemWorkQuery</c> pair carries no storage key or kind,
/// and this is where the row is actually loaded.
/// </summary>
public interface IAssetRetentionStore
{
    /// <summary>
    /// Loads the asset of the scope's user, tracked, only while it is still due: live and
    /// <c>retention_until</c> before <paramref name="now"/>. The id query that produced the pair ran on
    /// another context, so this re-check guards the irreversible object delete against a row that was
    /// re-stamped or soft-deleted in between.
    /// </summary>
    /// <param name="assetId">The id from the <c>ISystemWorkQuery</c> pair.</param>
    /// <param name="now">The sweep's clock reading.</param>
    /// <param name="ct">Cancels the statement.</param>
    /// <returns>The asset, or <see langword="null"/> when it is gone, not due or not visible to the scope's user.</returns>
    Task<Asset?> FindDueAsync(Guid assetId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Marks <paramref name="asset"/> deleted and commits it, after its object is gone.</summary>
    /// <param name="asset">The asset <see cref="FindDueAsync"/> returned.</param>
    /// <param name="now">The sweep's clock reading.</param>
    /// <param name="ct">Cancels the commit.</param>
    Task MarkDeletedAsync(Asset asset, DateTimeOffset now, CancellationToken ct);
}
