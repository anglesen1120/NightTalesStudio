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
}
