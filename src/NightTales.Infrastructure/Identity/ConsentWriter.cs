using System.Net;
using ErrorOr;
using NightTales.Application.Abstractions.Identity;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Entities;

namespace NightTales.Infrastructure.Identity;

/// <summary>
/// Records accepted legal-document versions in <c>user_consents</c> (09 README D9, BD1; 13 FR-13-2)
/// inside the caller's transaction.
/// </summary>
/// <remarks>
/// <para>
/// The writer only <c>Add</c>s rows: it never calls <c>SaveChangesAsync</c>, so
/// <c>UnitOfWorkBehavior</c> (09 README D5) commits the consents together with the rest of the
/// command. A handler that fails after the call leaves no consent row, which
/// <c>ConsentWriterTests.RecordAsync_IsCommittedWithTheCallersTransaction</c> proves.
/// </para>
/// <para>
/// The timestamp comes from <see cref="IClock"/> so the tests' <c>FakeClock</c> controls it, exactly
/// like the audit stamps of 09 W3 CR-38.
/// </para>
/// </remarks>
/// <param name="context">The scope's write model (the caller's unit of work).</param>
/// <param name="clock">The clock that stamps <c>accepted_at</c>.</param>
public sealed class ConsentWriter(AppDbContext context, IClock clock) : IConsentWriter
{
    /// <inheritdoc />
    public Task<ErrorOr<Success>> RecordAsync(
        Guid userId,
        LegalDocument document,
        string version,
        IPAddress? ip,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        context.UserConsents.Add(UserConsent.Create(userId, document, version, clock.UtcNow, ip));

        return Task.FromResult<ErrorOr<Success>>(Result.Success);
    }
}
