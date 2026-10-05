namespace NightTales.Application.Features.Account.Legal;

/// <summary>The two document statuses in stable terms/privacy order.</summary>
public sealed record LegalAcceptanceDto(IReadOnlyList<LegalDocumentStatusDto> Documents);

/// <summary>The current version and latest acceptance of a public legal document.</summary>
public sealed record LegalDocumentStatusDto(
    string Document,
    string CurrentVersion,
    string? AcceptedVersion,
    DateTimeOffset? AcceptedAt);
