using System.Net;
using NightTales.Application.Abstractions.Identity;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Identity;

namespace NightTales.Infrastructure.Persistence.Entities;

/// <summary>
/// One accepted legal-document version (09 README D9 <c>user_consents</c>, BD1; 13 FR-13-2). The table
/// is an acceptance <em>history</em>: accepting the same document again inserts another row (no unique
/// index), so a re-acceptance after a version bump is visible.
/// </summary>
/// <remarks>
/// <para>
/// Infrastructure-only, like <see cref="Outbox.OutboxMessage"/>: it is written by
/// <c>ConsentWriter</c> inside the caller's transaction, never by a Domain factory call from a handler.
/// The id is a UUIDv7 from <see cref="Guid.CreateVersion7"/> and the configuration maps it with
/// <c>ValueGeneratedNever</c> (D3/D8), exactly like <c>Entity</c> ids.
/// </para>
/// <para>
/// It implements <see cref="IUserOwned"/>, so the D10 tenant filter restricts reads of
/// <c>user_consents</c> to the scope's user on both contexts. Inserts are unaffected (a query filter
/// only filters reads), which matters because the register flow writes consents while the caller is
/// still anonymous.
/// </para>
/// </remarks>
public sealed class UserConsent : IUserOwned
{
    private UserConsent()
    {
    }

    /// <summary>The row id (UUIDv7, generated here, never by the database).</summary>
    public Guid Id { get; private set; }

    /// <summary>The accepting user; the row references <c>users(id)</c> with <c>ON DELETE RESTRICT</c>.</summary>
    public Guid UserId { get; private set; }

    /// <summary>Which document was accepted.</summary>
    public LegalDocument Document { get; private set; }

    /// <summary>The accepted version as it was presented (1–32 characters).</summary>
    public string Version { get; private set; } = string.Empty;

    /// <summary>When the acceptance happened, from the scope's clock.</summary>
    public DateTimeOffset AcceptedAt { get; private set; }

    /// <summary>The caller's address when it could be resolved; null otherwise.</summary>
    public IPAddress? Ip { get; private set; }

    /// <summary>Creates one acceptance row; the caller adds it to the unit of work.</summary>
    /// <param name="userId">The accepting user.</param>
    /// <param name="document">Which document was accepted.</param>
    /// <param name="version">The accepted version (1–32 characters).</param>
    /// <param name="acceptedAt">The acceptance time.</param>
    /// <param name="ip">The caller's address, or null.</param>
    public static UserConsent Create(
        Guid userId,
        LegalDocument document,
        string version,
        DateTimeOffset acceptedAt,
        IPAddress? ip)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (version.Length > LegalDocumentVersions.VersionMaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(version),
                version.Length,
                $"A legal-document version is at most {LegalDocumentVersions.VersionMaxLength} characters.");
        }

        return new UserConsent
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Document = document,
            Version = version,
            AcceptedAt = acceptedAt,
            Ip = ip,
        };
    }
}
