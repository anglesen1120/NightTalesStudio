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
