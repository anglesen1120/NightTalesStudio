using ErrorOr;
using NightTales.Domain.Compliance.Deletion;

namespace NightTales.Application.Features.Compliance.Deletion;

/// <summary>
/// The single entry point that starts a legal deletion clock (13 README D4). It joins the caller's ambient
/// <c>IUnitOfWork</c> and never saves: the caller's transaction commits the request together with the change that
/// caused it.
/// </summary>
public interface IDeletionRequests
{
    /// <summary>
    /// Creates the open request for <c>(userId, scope, targetId, reason)</c>, or returns the existing open one with
    /// its <c>DueAt</c> unchanged. A target outside the user's data is <c>not_found</c> and creates nothing.
    /// </summary>
    Task<ErrorOr<DeletionRequest>> CreateAsync(
        Guid userId,
        DeletionScope scope,
        Guid? targetId,
        DeletionReason reason,
        CancellationToken cancellationToken);

    /// <summary>
    /// Says whether a request for <c>(userId, scope, targetId, reason)</c> is already on record <em>in any state</em>
    /// — pending, claimed, failed or completed — so a caller can tell the engine's duty is already scheduled before
    /// it asks for it again.
    /// </summary>
    /// <remarks>
    /// <see cref="CreateAsync"/> dedupes the <em>open</em> row only (the unique index is partial on
    /// <c>completed_at IS NULL</c>), so it cannot answer "has this duty already run": a repeat of the same request
    /// after the job completed it would open a second clock over data that is gone (06.4.1-T1 task §Failure handling
    /// "Connection already revoked and its request already completed_at"). The wide query is what that case needs,
    /// and it is deliberately wide for every state rather than only <c>completed</c>: a caller that already serialises
    /// against the duty — the disconnect holds the connection row's lock while it asks — can rely on the wide answer
    /// instead of racing <see cref="CreateAsync"/>'s open-only dedupe, so a duty that is merely in progress is never
    /// mistaken for a missing one. This read is not a lock of its own: the pair read-then-create is only safe under
    /// the caller's serialisation. It is a read on the caller's tenant-filtered scope and never creates, moves or
    /// reopens anything.
    /// </remarks>
    /// <param name="userId">The owning account.</param>
    /// <param name="scope">The scope of the duty.</param>
    /// <param name="targetId">The target the duty covers, or <see langword="null"/> for the account scope.</param>
    /// <param name="reason">The reason that opened the duty.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="true"/> when any row of that tuple exists, whatever its status.</returns>
    Task<bool> ExistsAsync(
        Guid userId,
        DeletionScope scope,
        Guid? targetId,
        DeletionReason reason,
        CancellationToken cancellationToken);
}
