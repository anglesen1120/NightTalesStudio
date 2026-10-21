using NightTales.Domain.Publishing;

namespace NightTales.Application.Abstractions.Publishing;

/// <summary>
/// The one locked read of a publication on the <em>anonymous</em> cancel-by-link route (06.2.2-T2 §Design
/// "Cancel-by-link", 06 README D7/F17/YR8): the row is loaded under the same <c>FOR NO KEY UPDATE</c> lock
/// 06.2.6-T1's <c>DELETE /publications/{id}</c> takes, and returned tracked for the caller's own
/// <c>Cancel</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a port, and why not the tenant-filtered read.</b> The route carries no principal: its caller proves
/// itself with the single-use token, and the token's owner is only known <em>after</em> the consume. The
/// scope's tenant filter is fixed when its <c>AppDbContext</c> is constructed — which the unit of work does
/// before the handler runs — and <c>ISystemContext.For</c> refuses a scope that already created one, so a
/// tenant-filtered read would match no row at all, including the token owner's own. This is the same shape as
/// 06.1.2-T1's anonymous OAuth callback (<see cref="IChannelConnectionLocker"/>), and the statement therefore
/// carries the tenancy itself: the caller passes the id the consume returned and the row's <c>user_id</c> is a
/// predicate of the statement, so another user's id and an unknown id are both <see langword="null"/>.
/// </para>
/// <para>
/// <b>The lock is the one the sibling cancel and the upload job take.</b> <c>FOR NO KEY UPDATE</c> on the
/// publication row excludes 06.2.6-T1's <c>DELETE</c> and the upload job's own locked read, so whichever
/// takes the lock first decides: if the upload job wins, this read sees <c>uploading</c> and the route answers
/// its generic 400; if this route wins, the job reads <c>cancelled</c> and refuses in
/// <see cref="Publication.StartUpload"/>. The caller's transaction holds the lock, so the statement is only
/// meaningful inside one (a unit of work) and the port refuses without one.
/// </para>
/// <para>
/// <b>Tracked, never a second write path.</b> The row comes back tracked by the caller's own
/// <c>IAppDbContext</c>, so the domain mutation (<see cref="Publication.Cancel"/>) is written by the caller's
/// single <c>SaveChanges</c> and the outbox interceptor raises <c>publication_updated.v1</c> beside it —
/// exactly as on the authenticated route.
/// </para>
/// </remarks>
public interface IPublicationCancelLock
{
    /// <summary>
    /// Locks and reads one publication of <paramref name="userId"/>, freshly from the database.
    /// </summary>
    /// <param name="userId">
    /// The owner the token's consume returned — never a value the request body named.
    /// </param>
    /// <param name="publicationId">The publication the token's subject was compared with.</param>
    /// <param name="cancellationToken">Cancels the statement and the lock wait.</param>
    /// <returns>
    /// The locked row, tracked — or <see langword="null"/> when it does not exist or belongs to somebody else,
    /// which the caller answers with the route's single generic refusal.
    /// </returns>
    Task<Publication?> LockOwnedAsync(
        Guid userId,
        Guid publicationId,
        CancellationToken cancellationToken);
}
