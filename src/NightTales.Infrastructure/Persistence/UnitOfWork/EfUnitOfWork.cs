using System.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;

namespace NightTales.Infrastructure.Persistence.UnitOfWork;

/// <summary>
/// The <see cref="IUnitOfWork"/> of a command (09 README D5): one explicit <c>ReadCommitted</c>
/// transaction on the scope's <see cref="AppDbContext"/>, saved and committed once.
/// </summary>
/// <remarks>
/// <para>
/// Registered scoped, exactly like the context it wraps, so a handler's direct
/// <c>SaveChangesAsync</c> — which only the UnitOfWork and the seeders call — and the raw locks of
/// 02 (<c>pg_advisory_xact_lock</c>, <c>SELECT … FOR UPDATE</c>) run inside the same transaction.
/// </para>
/// <para>
/// A commit the database refuses is mapped to an error instead of an exception: a lost update becomes
/// <c>concurrency_conflict</c> (409) and a transient failure — including the
/// <see cref="DbUpdateException"/> that wraps one, which is how <c>SaveChangesAsync</c> reports it —
/// becomes <c>service_unavailable</c> (503). Both roll the transaction back first.
/// </para>
/// </remarks>
public sealed class EfUnitOfWork(AppDbContext database) : IUnitOfWork
{
    private const string ConcurrencyDescription =
        "The record changed while the command was running; retry with the current version.";

    private const string TransientDescription = "The database is temporarily unavailable; retry.";

    /// <summary>
    /// The stable description of a refused foreign key; it names the client's input, never the SQL or
    /// the constraint (09 README D17).
    /// </summary>
    private const string ForeignKeyDescription =
        "A record this request refers to no longer exists; reload and try again.";

    /// <summary>PostgreSQL's SQLSTATE for a foreign key violation.</summary>
    private const string ForeignKeyViolation = "23503";

    private IDbContextTransaction? _transaction;

    /// <inheritdoc />
    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        if (_transaction is null)
        {
            _transaction = await database.Database
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<ErrorOr<Success>> CommitAsync(CancellationToken cancellationToken)
    {
        var transaction = _transaction
            ?? throw new InvalidOperationException($"'{nameof(CommitAsync)}' requires '{nameof(BeginAsync)}' first.");

        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _transaction = null;

            return Result.Success;
        }
        catch (DbUpdateConcurrencyException)
        {
            // The row changed between the load and the save (the xmin concurrency token of D8).
            await TryRollbackAsync(cancellationToken).ConfigureAwait(false);

            return Error.Conflict(code: ErrorCodes.ConcurrencyConflict, description: ConcurrencyDescription);
        }
        catch (DbUpdateException exception) when (IsForeignKeyViolation(exception))
        {
            // 09.3.1-T1 failure handling: a row the command referenced no longer exists (the user of a
            // `user_consents` row was deleted between steps) is the client's input being unusable, not a
            // server fault — 400 validation_failed instead of a raw 500 from the provider exception.
            await TryRollbackAsync(cancellationToken).ConfigureAwait(false);

            return Error.Validation(code: ErrorCodes.ValidationFailed, description: ForeignKeyDescription);
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            await TryRollbackAsync(cancellationToken).ConfigureAwait(false);

            return Error.Failure(code: ErrorCodes.ServiceUnavailable, description: TransientDescription);
        }
    }

    /// <inheritdoc />
    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        var transaction = _transaction;
        if (transaction is null)
        {
            return;
        }

        _transaction = null;
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the database reported a transient failure. <c>SaveChangesAsync</c> wraps the provider
    /// exception in a <see cref="DbUpdateException"/>, the transaction commit does not.
    /// </summary>
    private static bool IsTransient(Exception exception) =>
        exception is NpgsqlException { IsTransient: true }
        || exception.InnerException is NpgsqlException { IsTransient: true };

    /// <summary>
    /// Whether the save was refused by a foreign key (SQLSTATE 23503, <c>foreign_key_violation</c>).
    /// </summary>
    private static bool IsForeignKeyViolation(Exception exception) =>
        exception is DbUpdateException { InnerException: PostgresException { SqlState: ForeignKeyViolation } }
        || exception.InnerException is PostgresException { SqlState: ForeignKeyViolation };

    /// <summary>
    /// Rolls back without letting a secondary failure replace the mapped error the caller is about to
    /// receive; the transient failure that caused it would break the rollback too.
    /// </summary>
    private async Task TryRollbackAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Deliberate: the mapped error is what the caller needs, and the context disposal rolls back
            // an abandoned transaction anyway.
        }
    }
}
