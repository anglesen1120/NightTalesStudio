using NightTales.Application.Abstractions.Identity;

namespace NightTales.Application.Features.Account.Legal;

/// <summary>Shapes a single latest-consent read into the wire projection.</summary>
public sealed class LegalAcceptanceReader(IConsentReader consents, ICurrentLegalVersions versions)
{
    /// <summary>Reads only the specified user's consent stamps, never IP or profile information.</summary>
    public async Task<LegalAcceptanceDto> ReadAsync(Guid userId, CancellationToken ct, bool includePending = false)
    {
        var accepted = await consents.LatestAsync(userId, ct, includePending).ConfigureAwait(false);
        return new LegalAcceptanceDto([
            Status(LegalDocument.Tos, "tos", accepted),
            Status(LegalDocument.Privacy, "privacy", accepted),
        ]);
    }

    private LegalDocumentStatusDto Status(
        LegalDocument document, string wireName, IReadOnlyList<LegalConsentSnapshot> accepted)
    {
        var consent = accepted.FirstOrDefault(row => row.Document == document);
        return new(wireName, versions.GetCurrentVersion(document), consent?.Version, consent?.AcceptedAt);
    }
}
