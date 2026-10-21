using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Domain.Publishing;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Publishing;

/// <summary>
/// The <see cref="IPublicationCancelLock"/> over <c>publications</c> (06.2.2-T2 §Design "Cancel-by-link"): the one
/// statement of the anonymous route, which locks and reads the token owner's row in a single round trip.
/// </summary>
/// <remarks>
/// <para>
/// <b>The statement.</b> Parameterised <c>FromSql</c> text aimed at the primary key, with the owner as a
/// predicate of the statement itself: <c>SELECT * FROM publications WHERE id = @id AND user_id = @userId FOR
/// NO KEY UPDATE</c>. <c>FOR NO KEY UPDATE</c> is the keying 06.2.6-T1's cancel and the upload job's locked read
/// use, so an unrelated foreign-key <c>KEY SHARE</c> check may still proceed.
/// </para>
/// <para>
/// <b>Tenant filter: a declared bypass.</b> A raw statement carries no query filter, and the composed one cannot
/// be used either: once EF applies the <c>"tenant"</c> filter it wraps the statement in a subquery, and
/// PostgreSQL refuses a locking clause there. The bypass is therefore explicit and narrow — the owner predicate
/// above <em>is</em> the tenancy — and because this type calls <c>IgnoreQueryFilters</c> its fully-qualified name
/// must stay in <c>NightTales.Architecture.Tests.AllowLists.IgnoreQueryFiltersTypeNames</c> (09.1.2-T1). The
/// route is anonymous, so the id it names comes from the consume of the single-use token and never from the
/// caller's claims.
/// </para>
/// <para>
/// <b>Tracked, and re-read after the lock.</b> The row comes back tracked, so the caller's mutation is an
/// ordinary entity update written by its own <c>SaveChanges</c> (never an <c>ExecuteUpdate</c> beside it). A row
/// this scope already tracked is reloaded, so a caller that waited for the lock sees the values the previous
/// holder committed rather than the snapshot it read before waiting — the same rule as
/// <c>ChannelConnectionLocker</c>. <c>xmin</c> is named explicitly because
/// <see cref="NightTales.Domain.Common.AggregateRoot.Version"/> is mapped to that PostgreSQL system column as the
/// concurrency token (<c>AggregateRootConvention</c>) and <c>SELECT *</c> excludes system columns: a row without it
/// would carry <c>Version = 0</c> and the caller's <c>UPDATE</c> would answer a spurious concurrency conflict. The
/// channel-connection lock needs none of this, because that entity ignores its version.
/// </para>
/// </remarks>
/// <param name="database">The scope's write model: it carries the transaction the lock lives in and the tracking.</param>
public sealed class PublicationCancelLock(AppDbContext database) : IPublicationCancelLock
{
    /// <inheritdoc />
    public async Task<Publication?> LockOwnedAsync(
        Guid userId,
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            // An empty owner is a defect at the call site — the consume never answers one — and the equivalent
            // of an unpinned scope: fail loudly instead of reading across tenancy.
            throw new ArgumentException("The lock is taken for one owner; the id is never empty.", nameof(userId));
        }

        if (database.Database.CurrentTransaction is null)
        {
            // PostgreSQL releases a `FOR NO KEY UPDATE` lock when the statement's transaction ends, so a caller
            // that forgot its unit of work would take no lock at all and the upload job could interleave.
            throw new InvalidOperationException(
                $"{nameof(PublicationCancelLock)} takes a row lock and must run inside a transaction: open an IUnitOfWork first.");
        }

        // A row tracked before the statement comes back from identity resolution with the values of that earlier
        // read, so remember it: the reload below is what puts the locked, committed values into it.
        var alreadyTracked = database.ChangeTracker.Entries<Publication>()
            .Select(entry => entry.Entity)
            .ToList();

        var row = await database.Publications
            .FromSql(
                $"""
                SELECT *, xmin FROM publications
                WHERE id = {publicationId} AND user_id = {userId}
                FOR NO KEY UPDATE
                """)
            .IgnoreQueryFilters([TenantFilterConvention.FilterName])
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row is not null && alreadyTracked.Exists(entity => ReferenceEquals(entity, row)))
        {
            // A reload, not a detach-and-re-attach: `EntityEntry.State` and the untyped `DbContext.Attach` are
            // banned solution-wide, and a reloaded instance keeps the caller's own reference valid and tracked.
            await database.Entry(row).ReloadAsync(cancellationToken).ConfigureAwait(false);
        }

        return row;
    }
}
