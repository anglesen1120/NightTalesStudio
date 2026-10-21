using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Domain.Publishing;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Publishing;

/// <summary>
/// The single type in the solution that takes a row lock on <c>channel_connections</c> (06.1.2-T2 §Design
/// steps 1 and §"Token access", 06 README D3/D18/F21/YR11): the read the connect write decides
/// insert-versus-reconnect on and the read <see cref="TokenService"/> rotates a token under.
/// </summary>
/// <remarks>
/// <para>
/// <b>The statements.</b> Parameterised <c>FromSql</c> text aimed at the index that decides it —
/// <c>ux_channel_connections_account</c> for the by-account read, the primary key for the by-id read — never a
/// scan, and never a revoked row. Rows come back <em>tracked</em>, so the caller's domain mutation
/// (<c>Reconnect</c>, <c>MarkRefreshed</c>) is written by the caller's own <c>SaveChanges</c>.
/// </para>
/// <para>
/// <b>The lock needs the caller's transaction.</b> <c>FOR UPDATE</c> is held until the enclosing transaction
/// ends, so neither member opens, flushes or commits one: the connect write joins the callback's unit of work
/// and the token path opens its own when a job owns none (<see cref="TokenService"/>).
/// </para>
/// <para>
/// <b>Tenant filter: a declared bypass on both statements.</b> A raw statement carries no query filter, so each
/// one spells out the tenancy it needs and disables the named <c>"tenant"</c> filter of 09 README D10
/// explicitly. For the by-id read the predicate <c>user_id = @scopeUser</c> <em>is</em> the filter, and an
/// anonymous scope answers <see langword="null"/> before a statement runs, so an id of another user, an unknown
/// id and a job that forgot <c>ISystemContext.For</c> all fail closed. The by-account read is owner-agnostic on
/// purpose and cannot be otherwise — its caller is 06.1.2-T1's anonymous callback, where
/// <c>ISystemContext.For</c> cannot pin the scope any more (the claim in unit #1 already created the
/// <c>AppDbContext</c>), and a tenant-filtered read would match no row at all, including the caller's own
/// live one, so a reconnect could never be recognised. This is the same shape as the OAuth state claim of
/// <see cref="EfOAuthStateStore"/>: the row is read across tenancy, and the caller's <c>row.UserId</c> check is
/// what keeps it tenanted — the row is left untouched and answered as <c>channel_already_connected</c> when it
/// belongs to somebody else, and nothing of it is logged.
/// </para>
/// <para>
/// <b>The allow-list.</b> Because this type calls <c>IgnoreQueryFilters</c>, its fully-qualified name must stay
/// in <c>NightTales.Architecture.Tests.AllowLists.IgnoreQueryFiltersTypeNames</c> (09.1.2-T1): the bypass is
/// declared once, reviewed, and cannot spread to a second type by accident.
/// </para>
/// </remarks>
/// <param name="database">
/// The scope's write model: it carries the transaction the lock lives in and the tracking that turns the
/// caller's mutation into an ordinary entity update.
/// </param>
public sealed class ChannelConnectionLocker(AppDbContext database) : IChannelConnectionLocker
{
    /// <inheritdoc />
    public async Task<ChannelConnection?> LockLiveByAccountAsync(
        Platform platform,
        string externalAccountId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalAccountId);

        // The index's own predicate, whoever owns the row: the caller (the connect write) is what compares
        // `row.UserId` with the state's user before anything is written. The status predicate keeps a revoked
        // row out, so a disconnected channel becomes a fresh insert instead of a reconnect.
        return await LockAsync(
            $"""
            SELECT * FROM channel_connections
            WHERE platform = {platform.ToWire()} AND external_account_id = {externalAccountId} AND status <> 'revoked'
            FOR UPDATE
            """,
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ChannelConnection?> LockByIdAsync(Guid connectionId, CancellationToken ct)
    {
        // No pinned user, no statement: an anonymous scope (the callback of 06.1.2-T1, a job that skipped
        // ISystemContext.For) fails closed instead of reading across tenancy.
        if (database.CurrentUserId is not { } userId)
        {
            return null;
        }

        return await LockAsync(
            $"""
            SELECT * FROM channel_connections
            WHERE id = {connectionId} AND user_id = {userId}
            FOR UPDATE
            """,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one locking statement over the unfiltered root and returns its row tracked, carrying the values the
    /// previous lock holder committed rather than the ones this scope read before it waited.
    /// </summary>
    private async Task<ChannelConnection?> LockAsync(FormattableString statement, CancellationToken ct)
    {
        if (database.Database.CurrentTransaction is null)
        {
            // PostgreSQL releases a `FOR UPDATE` lock when the statement's transaction ends, so a caller that
            // forgot one would take no lock at all and two connects (or a connect and a token refresh) would
            // silently interleave. The guard is free: both callers either open a transaction themselves (the
            // token path) or run inside their unit of work (the connect write).
            throw new InvalidOperationException(
                $"{nameof(ChannelConnectionLocker)} takes a row lock and must run inside a transaction: open an IUnitOfWork first.");
        }

        // An instance tracked before the statement comes back from identity resolution with the values of that
        // earlier read, so remember it: the reload below is what puts the locked, committed values into it.
        var alreadyTracked = database.ChangeTracker.Entries<ChannelConnection>()
            .Select(entry => entry.Entity)
            .ToList();

        var row = await database.ChannelConnections
            .FromSql(statement)
            .IgnoreQueryFilters([TenantFilterConvention.FilterName])
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (row is not null && alreadyTracked.Exists(entity => ReferenceEquals(entity, row)))
        {
            // A reload, not a detach-and-re-attach: `EntityEntry.State` and the untyped `DbContext.Attach` are
            // banned solution-wide by 02.2.2 plan §3.2's append-only rule, and a reloaded instance keeps the
            // caller's own reference valid and tracked, so the caller's mutation still reaches `SaveChanges`.
            await database.Entry(row).ReloadAsync(ct).ConfigureAwait(false);
        }

        return row;
    }
}
