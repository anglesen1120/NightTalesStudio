using System.Linq.Expressions;
using NightTales.Domain.Common;

namespace NightTales.Application.Abstractions;

/// <summary>
/// The one allow-listed cross-user read outside <c>Features/Admin</c> (09 README D10). A recurring
/// sweep that must find work for every user resolves the <c>(id, user_id)</c> pairs through this
/// port and then processes each pair in its own scope under <see cref="ISystemContext.For"/>, so the
/// tenant filter stays on for every actual row read: only ids cross the tenant boundary.
/// </summary>
/// <remarks>
/// The wave-1 member is deliberately narrow — one signed method, one batch, no lock hints. Later plans
/// extend this interface with their own members (06.4.2-T1, 06.4.3-T1) instead of introducing a second
/// cross-user port. The ordered overload is 02.8.3-T1's extension: a sweep that takes a batch out of a
/// queue of due rows must decide <i>which</i> rows it takes, or the rows the provider happens to return
/// first starve every other owner (review findings PF-1, SEC-2).
/// </remarks>
public interface ISystemWorkQuery
{
    /// <summary>
    /// Yields at most <paramref name="batch"/> <c>(Id, UserId)</c> pairs of the rows matching
    /// <paramref name="where"/>, whoever owns them.
    /// </summary>
    /// <typeparam name="T">A user-owned entity type; the filter bypass only exists for those.</typeparam>
    /// <param name="where">The predicate the owner's own query would use.</param>
    /// <param name="batch">The maximum number of pairs to yield; must be positive.</param>
    /// <param name="ct">Cancels the enumeration and the underlying statement.</param>
    IAsyncEnumerable<(Guid Id, Guid UserId)> PendingAsync<T>(
        Expression<Func<T, bool>> where,
        int batch,
        CancellationToken ct)
        where T : Entity, IUserOwned;

    /// <summary>
    /// Yields at most <paramref name="batch"/> <c>(Id, UserId)</c> pairs of the rows matching
    /// <paramref name="where"/>, whoever owns them, in the order <paramref name="orderBy"/> then row id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The id tiebreak makes the order total, so two calls with the same batch size pick the same rows in the same
    /// order and a sweep that re-stamps what it served (<c>UpdatedAt</c>) rotates through its queue instead of
    /// re-reading its head. A sweep that cannot re-stamp must not rely on it for fairness: without a change between
    /// calls, the same rows come back.
    /// </para>
    /// <para>
    /// Neither the order key nor the tiebreak crosses the tenant boundary: the rows are the caller's own work, and
    /// only their ids are returned.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">A user-owned entity type; the filter bypass only exists for those.</typeparam>
    /// <typeparam name="TKey">The type of the order key, a mapped column of <typeparamref name="T"/>.</typeparam>
    /// <param name="where">The predicate the owner's own query would use.</param>
    /// <param name="orderBy">The column to order by first; ascending, and never null.</param>
    /// <param name="batch">The maximum number of pairs to yield; must be positive.</param>
    /// <param name="ct">Cancels the enumeration and the underlying statement.</param>
    IAsyncEnumerable<(Guid Id, Guid UserId)> PendingAsync<T, TKey>(
        Expression<Func<T, bool>> where,
        Expression<Func<T, TKey>> orderBy,
        int batch,
        CancellationToken ct)
        where T : Entity, IUserOwned;
}
