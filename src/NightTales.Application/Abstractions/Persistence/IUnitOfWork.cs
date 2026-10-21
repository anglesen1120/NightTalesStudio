using ErrorOr;

namespace NightTales.Application.Abstractions.Persistence;

/// <summary>
/// The explicit transaction of one command (09 README D5). <c>UnitOfWorkBehavior</c> opens it before
/// the handler runs, so the handler's effects, the outbox rows of the same <c>SaveChangesAsync</c> and
/// the idempotency claim of the same scope commit or roll back together.
/// </summary>
/// <remarks>
/// Handlers never call <c>SaveChangesAsync</c> themselves, and a raw lock a handler takes
/// (<c>pg_advisory_xact_lock</c>, <c>SELECT … FOR UPDATE</c>) joins this transaction, so it is held
/// until the commit.
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>
    /// Opens the transaction at <c>ReadCommitted</c> on the scope's connection. A second call in the
    /// same scope joins the open transaction instead of nesting a new one.
    /// </summary>
    Task BeginAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Saves the scope's changes and commits. The transaction is rolled back and the failure mapped
    /// when the database reports a lost update (<c>concurrency_conflict</c>, 409, retryable) or a
    /// transient error (<c>service_unavailable</c>, 503, retryable).
    /// </summary>
    Task<ErrorOr<Success>> CommitAsync(CancellationToken cancellationToken);

    /// <summary>Rolls back the open transaction; a scope that opened none is a no-op.</summary>
    Task RollbackAsync(CancellationToken cancellationToken);
}
