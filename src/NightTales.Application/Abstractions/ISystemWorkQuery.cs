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
/// The wave-1 member is deliberately narrow — one signed method, one batch, no ordering and no lock
/// hints. Later plans extend this interface with their own members (06.4.2-T1, 06.4.3-T1) instead of
/// introducing a second cross-user port.
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
}
