using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Identity;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Identity;

/// <summary>Reads latest stamps in one statement, with transaction-local read-your-writes.</summary>
public sealed class ConsentReader(AppDbContext context) : IConsentReader
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<LegalConsentSnapshot>> LatestAsync(Guid userId, CancellationToken ct, bool includePending = false)
    {
        var rows = await context.Database.SqlQuery<ConsentRow>($"""
            SELECT DISTINCT ON (document) document, version, accepted_at
            FROM user_consents WHERE user_id = {userId}
            ORDER BY document, accepted_at DESC, id DESC
            """).ToListAsync(ct).ConfigureAwait(false);
        var latest = rows.Select(row => new LegalConsentSnapshot(
            row.Document == "tos" ? LegalDocument.Tos : LegalDocument.Privacy, row.Version, row.AcceptedAt)).ToList();
        if (!includePending)
        {
            return latest;
        }

        // RecordAsync only Adds. Preserve its exact timestamps without flushing before AuditBehavior.
        // GET and the gate return above: only the successful accept response reads pending rows.
        foreach (var entry in context.ChangeTracker.Entries<Persistence.Entities.UserConsent>())
        {
            var consent = entry.Entity;
            if (entry.State != EntityState.Added || consent.UserId != userId)
            {
                continue;
            }
            var index = latest.FindIndex(row => row.Document == consent.Document);
            var pending = new LegalConsentSnapshot(consent.Document, consent.Version, consent.AcceptedAt);
            if (index < 0)
            {
                latest.Add(pending);
            }
            else if (consent.AcceptedAt >= latest[index].AcceptedAt)
            {
                latest[index] = pending;
            }
        }
        return latest;
    }

    private sealed record ConsentRow(string Document, string Version, DateTimeOffset AcceptedAt);
}
