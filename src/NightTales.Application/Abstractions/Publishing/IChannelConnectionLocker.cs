using NightTales.Domain.Publishing;

namespace NightTales.Application.Abstractions.Publishing;

/// <summary>
/// The locked reads of <c>channel_connections</c> (06.1.2-T2 task §Design steps 1 and §"Token access",
/// 06 README D3/D18/F21/YR11): the one place a connection row is read under a database lock, so the
/// connect/reconnect write (<see cref="NightTales.Application.Features.Publishing.Channels.IConnectChannelConnection"/>),
/// the refresh of <see cref="ITokenService"/> and the disconnect 06.4.1-T1 lands cannot interleave.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a port.</b> The Application layer has no relational EF provider and <c>SELECT … FOR UPDATE</c> is not
/// a LINQ expression, so both statements live in Infrastructure (<c>ChannelConnectionLocker</c>, slice C). It is
/// the only type in the codebase that may take a row lock on this table; a second site would be a second
/// locking protocol.
/// </para>
/// <para>
/// <b>Both members join the caller's transaction</b> and neither flushes, commits nor opens one: the lock is
/// held until the caller's unit of work commits or rolls back
/// (<see cref="NightTales.Application.Abstractions.Persistence.IUnitOfWork"/>), which is what makes
/// "read → decide → write" atomic. Outside a transaction the lock would cover one statement and protect
/// nothing, so both members are only meaningful inside one — the connect write joins the callback's unit #2,
/// and <see cref="ITokenService"/> opens its own when a job calls it.
/// </para>
/// <para>
/// <b>Rows come back tracked</b>, so the caller's domain mutation (<c>Reconnect</c>, <c>MarkRefreshed</c>) is
/// written by the caller's single <c>SaveChanges</c> as an ordinary entity update, never by an
/// <c>ExecuteUpdate</c> beside it.
/// </para>
/// </remarks>
public interface IChannelConnectionLocker
{
    /// <summary>
    /// Reads the <em>live</em> row that holds <paramref name="externalAccountId"/> on
    /// <paramref name="platform"/>, <b>whoever owns it</b>, under a row lock, and returns it tracked — the read
    /// the connect write decides "insert or reconnect" on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The statement.</b> One parameterised statement through
    /// <c>ux_channel_connections_account</c>:
    /// <c>SELECT … FROM channel_connections WHERE platform = @platform AND external_account_id = @account AND
    /// status &lt;&gt; 'revoked' FOR UPDATE</c> — the index's own predicate, so no scan and never a revoked row.
    /// </para>
    /// <para>
    /// <b>Owner-agnostic on purpose, and why it must be.</b> The caller is 06.1.2-T1's <em>anonymous</em>
    /// callback: the route is <c>AllowAnonymous</c>, the account is learned from the claimed OAuth state, and
    /// <c>ISystemContext.For</c> cannot pin the scope for unit #2 (it throws once a <c>DbContext</c> exists, and
    /// the claim in unit #1 created one). A tenant-filtered read therefore matches <em>no</em> row — including
    /// the caller's own — so it could never recognise a reconnect; the second connect of one user would insert,
    /// lose to the unique index and be refused as "already connected" against itself.
    /// </para>
    /// <para>
    /// <b>This is the deliberate, allow-listed bypass</b> on this one statement — the same shape as the OAuth
    /// state claim of <c>EfOAuthStateStore</c>, which is the codebase's precedent for "the anonymous callback must
    /// see a row it does not own". The type must stay on
    /// <c>NightTales.Architecture.Tests.AllowLists.IgnoreQueryFiltersTypeNames</c> (09.1.2-T1) so the bypass is
    /// declared, and the hard-coded predicate above is what keeps the read narrow. Tenancy is preserved by the
    /// caller, not by the statement: the row is only ever mutated after
    /// <c>row.UserId == request.UserId</c> is checked, it is left <c>Unchanged</c> — and answered as
    /// <c>channel_already_connected</c> — when it belongs to somebody else, and nothing of it is logged (no
    /// title, no token, no owner id beyond the caller's own).
    /// </para>
    /// </remarks>
    /// <param name="platform">The platform of the connect (06's only member is <see cref="Platform.YouTube"/>).</param>
    /// <param name="externalAccountId">
    /// Google's channel id (<c>UC…</c>), the connection's <c>external_account_id</c>; never empty.
    /// </param>
    /// <param name="ct">Cancels the statement and the lock wait.</param>
    /// <returns>
    /// The live row — tracked, whoever owns it — or <see langword="null"/> when the channel has no live
    /// connection, which is the caller's "insert a new row" case.
    /// </returns>
    Task<ChannelConnection?> LockLiveByAccountAsync(
        Platform platform,
        string externalAccountId,
        CancellationToken ct);

    /// <summary>
    /// Reads the caller's own connection by id under <c>FOR UPDATE</c>, freshly from the database, and returns
    /// it tracked — the read <see cref="ITokenService"/> rotates a token under (06 README F21).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The statement</b> is parameterised and carries the owner predicate itself:
    /// <c>SELECT … FROM channel_connections WHERE id = @id AND user_id = @scopeUser FOR UPDATE</c>. The
    /// predicate is explicit because a raw statement has no query filter to lean on, and the filter cannot be
    /// composed onto it either: once EF has to apply the <c>"tenant"</c> filter it wraps the statement in a
    /// subquery, and PostgreSQL refuses a locking clause there. An anonymous scope
    /// (<c>CurrentUserId == null</c>) answers <see langword="null"/> without a statement, so an id of another
    /// user, an unknown id and a job that forgot <c>ISystemContext.For</c> all fail closed.
    /// </para>
    /// <para>
    /// The answer is <b>re-read after the lock</b>, never taken from tracked state: a caller that waited for the
    /// lock must see the values the previous holder committed (its token pair and <c>TokenExpiresAt</c>), not the
    /// snapshot it read before waiting.
    /// </para>
    /// </remarks>
    /// <param name="connectionId">The <c>channel_connections</c> row to lock.</param>
    /// <param name="ct">Cancels the statement and the lock wait.</param>
    /// <returns>The locked row, or <see langword="null"/> when it is not the scope's own (or does not exist).</returns>
    Task<ChannelConnection?> LockByIdAsync(Guid connectionId, CancellationToken ct);
}
