using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Queries;

/// <summary>
/// The implementation of the single allow-listed cross-user read of 09 README D10. This namespace and
/// type are on the <c>IgnoreQueryFilters</c> allow-list of 09.1.2-T1: nothing else outside
/// <c>Application/Features/Admin</c> may bypass the tenant filter.
/// </summary>
/// <remarks>
/// It runs on the read model and returns ids only — never another user's row data. The caller then
/// processes each pair in its own DI scope under <see cref="ISystemContext.For"/>, where the filter is
/// on again.
/// </remarks>
public sealed class SystemWorkQuery(IReadDb readDb) : ISystemWorkQuery
{
    /// <inheritdoc />
    public IAsyncEnumerable<(Guid Id, Guid UserId)> PendingAsync<T>(
        Expression<Func<T, bool>> where,
        int batch,
        CancellationToken ct)
        where T : Entity, IUserOwned
    {
        ArgumentNullException.ThrowIfNull(where);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batch);

        return Rows(where, batch, ct);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<(Guid Id, Guid UserId)> PendingAsync<T, TKey>(
        Expression<Func<T, bool>> where,
        Expression<Func<T, TKey>> orderBy,
        int batch,
        CancellationToken ct)
        where T : Entity, IUserOwned
    {
        ArgumentNullException.ThrowIfNull(where);
        ArgumentNullException.ThrowIfNull(orderBy);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batch);

        return OrderedRows(where, orderBy, batch, ct);
    }

    /// <summary>
    /// Streams the pairs, so a sweep can start on the first one instead of buffering a batch. The
    /// enumeration owns the query: the caller must consume it inside the lifetime of the
    /// <see cref="IReadDb"/> scope that created this instance.
    /// </summary>
    private async IAsyncEnumerable<(Guid Id, Guid UserId)> Rows<T>(
        Expression<Func<T, bool>> where,
        int batch,
        [EnumeratorCancellation] CancellationToken ct)
        where T : Entity, IUserOwned
    {
        var rows = readDb
            .Query<T>()
            .IgnoreQueryFilters([TenantFilterConvention.FilterName])
            .Where(where)
            .Select(entity => new { entity.Id, entity.UserId })
            .Take(batch);

        await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(ct))
        {
            yield return (row.Id, row.UserId);
        }
    }

    /// <summary>
    /// Streams the pairs of the ordered overload: <paramref name="orderBy"/> first, then the id as the total-order
    /// tiebreak. The primary key is the last sort key, so the statement is stable and a sweep that re-stamps what it
    /// served walks its queue instead of re-reading its head.
    /// </summary>
    private async IAsyncEnumerable<(Guid Id, Guid UserId)> OrderedRows<T, TKey>(
        Expression<Func<T, bool>> where,
        Expression<Func<T, TKey>> orderBy,
        int batch,
        [EnumeratorCancellation] CancellationToken ct)
        where T : Entity, IUserOwned
    {
        var rows = readDb
            .Query<T>()
            .IgnoreQueryFilters([TenantFilterConvention.FilterName])
            .Where(where)
            .OrderBy(orderBy)
            .ThenBy(entity => entity.Id)
            .Select(entity => new { entity.Id, entity.UserId })
            .Take(batch);

        await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(ct))
        {
            yield return (row.Id, row.UserId);
        }
    }
}
