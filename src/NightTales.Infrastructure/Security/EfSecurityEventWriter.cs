using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The P implementation of <see cref="ISecurityEventWriter"/> (09.10.4-T1 Design, 09 W3 CR-20): it
/// rotates <c>users.security_stamp</c> so every token issued with the old one stops being accepted once
/// 09.3.2-T2 validates the claim.
/// </summary>
/// <remarks>
/// <para>
/// 09.3.2-T2 owns the full contract — the per-request stamp/session check, the
/// <c>UserSecurityChanged</c> outbox event and the socket abort — and this task calls the port instead of
/// assigning the stamp inline, exactly as that task's file asks. The rotation itself is real here: the
/// new stamp commits with the caller's transaction (09 README D5).
/// </para>
/// <para>
/// <b>P limitation.</b> The <c>user_security_changed.v1</c> row is not written, because the current
/// <c>OutboxSaveChangesInterceptor</c> only stores events raised by an <c>AggregateRoot</c>: the raising
/// path is 09.3.2-T2's, together with the handler that evicts the stamp cache. Nothing in P reads the
/// stamp claim, so no caller observes the difference; <paramref name="reason"/> is carried for that
/// event and validated here.
/// </para>
/// </remarks>
/// <param name="database">The scope's write model; the tracked user row commits with the caller.</param>
public sealed class EfSecurityEventWriter(AppDbContext database) : ISecurityEventWriter
{
    /// <inheritdoc />
    public async Task<ErrorOr<Success>> RotateStampAsync(Guid userId, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var user = await database.Users
            .SingleOrDefaultAsync(candidate => candidate.Id == userId, ct)
            .ConfigureAwait(false);

        if (user is null)
        {
            return Errors.NotFound();
        }

        // A fresh opaque value; Identity's own store uses a random string of the same shape. The tracked
        // entity is saved by the unit of work that wraps the calling command.
        user.SecurityStamp = Guid.CreateVersion7().ToString("N");

        return Result.Success;
    }
}
