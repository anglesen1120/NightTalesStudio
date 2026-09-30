using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;

namespace NightTales.Application.Behaviors;

/// <summary>
/// Opens the explicit <c>ReadCommitted</c> transaction before the handler, saves and commits on
/// success, and rolls back when the handler returns an error — so a command's effects, its outbox rows
/// and its idempotency claim commit or roll back together (09 README D4/D5).
/// </summary>
/// <remarks>
/// Only commands run it: the constraint is <c>IBaseCommand</c>, so a query never opens a transaction.
/// A commit that the database refuses (a lost update, a transient failure) is mapped to
/// <c>concurrency_conflict</c> / <c>service_unavailable</c> by <see cref="IUnitOfWork"/>, and an
/// exception thrown by the handler rolls back and rethrows unchanged.
/// </remarks>
public sealed class UnitOfWorkBehavior<TMessage, TResponse>(IUnitOfWork unitOfWork)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IBaseCommand
    where TResponse : IErrorOr
{
    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        TResponse response;
        try
        {
            response = await next(message, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await RollbackIgnoringFailureAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (response.IsError)
        {
            // UC3: an error result leaves nothing behind — no row, no outbox row, no idempotency row.
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return response;
        }

        ErrorOr<Success> committed;
        try
        {
            committed = await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await RollbackIgnoringFailureAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        return committed.IsError
            ? ErrorOrFactory<TResponse>.From([.. committed.Errors])
            : response;
    }

    /// <summary>
    /// Rolls back without letting a secondary failure replace the failure the caller must see: the
    /// handler's own exception, or the error the commit mapping produced.
    /// </summary>
    private async Task RollbackIgnoringFailureAsync(CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Deliberate: the scope's context disposal rolls back an abandoned transaction too, and the
            // original failure is the one that must surface.
        }
    }
}
