using System.Net;
using ErrorOr;

namespace NightTales.Application.Abstractions.Identity;

/// <summary>
/// Records an accepted legal-document version for a user (09 README D9, BD1; 13 FR-13-2).
/// </summary>
/// <remarks>
/// <para>
/// The port writes inside the caller's transaction (09 README D5): it only <c>Add</c>s rows and never
/// calls <c>SaveChangesAsync</c>, so <c>UnitOfWorkBehavior</c> commits the consent together with the
/// rest of the command — a failed registration leaves no consent row.
/// </para>
/// <para>
/// Repeated acceptance of the same <c>(user, document)</c> is allowed by design: the table is an
/// acceptance <em>history</em> (13 FR-13-2), so there is no unique index and every acceptance inserts
/// a new row.
/// </para>
/// </remarks>
public interface IConsentWriter
{
    /// <summary>Adds one acceptance row to the scope's unit of work.</summary>
    /// <param name="userId">The accepting user; the row references <c>users(id)</c> (<c>ON DELETE RESTRICT</c>).</param>
    /// <param name="document">Which document was accepted.</param>
    /// <param name="version">The accepted version as it was presented (1–32 chars).</param>
    /// <param name="ip">The caller's address, or null when it cannot be resolved.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<Success>> RecordAsync(
        Guid userId,
        LegalDocument document,
        string version,
        IPAddress? ip,
        CancellationToken ct);
}
