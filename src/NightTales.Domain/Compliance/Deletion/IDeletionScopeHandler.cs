using ErrorOr;

namespace NightTales.Domain.Compliance.Deletion;

/// <summary>
/// The delete logic of one <see cref="DeletionScope"/>, registered by the scope's owner (13 README D4/F3):
/// the engine owns rows, scheduling, audit and the restore re-apply, never the owner's internals.
/// </summary>
/// <remarks>
/// A handler runs inside a DI scope pinned to <see cref="DeletionRequest.UserId"/> and inside the row's
/// transaction. It MUST be idempotent: a crash before completion re-runs it, and a restore re-applies it.
/// </remarks>
public interface IDeletionScopeHandler
{
    /// <summary>The scope this handler executes.</summary>
    DeletionScope Scope { get; }

    /// <summary>Executes <paramref name="request"/>; a failure is an <see cref="Error"/> with a stable code, never a message with data.</summary>
    Task<ErrorOr<DeletionResult>> ExecuteAsync(DeletionRequest request, CancellationToken cancellationToken);
}
