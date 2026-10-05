using NightTales.Application.Abstractions.Identity;

namespace NightTales.Infrastructure.Identity;

/// <summary>
/// <c>LegalDocuments:Current</c> (09.3.1-T1): the ToS and Privacy versions a registration records as
/// accepted (13 FR-13-2; 13 owns <c>legal_documents</c> and the version catalogue).
/// </summary>
/// <remarks>
/// Both values are required and validated at start-up (1–32 characters, the width of
/// <c>user_consents.version</c>): a host that cannot name the current versions must not accept a
/// registration. 09.3.1-T3 reads them through this options type when it records the consent rows.
/// </remarks>
public sealed class LegalDocumentVersions : ICurrentLegalVersions
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string SectionName = "LegalDocuments:Current";

    /// <summary>The longest version string; matches <c>user_consents.version</c>.</summary>
    public const int VersionMaxLength = 32;

    /// <summary>The current terms-of-service version.</summary>
    public string Tos { get; set; } = string.Empty;

    /// <summary>The current privacy-policy version.</summary>
    public string Privacy { get; set; } = string.Empty;

    /// <summary>The current version of <paramref name="document"/>.</summary>
    /// <param name="document">Which document to name.</param>
    public string GetCurrentVersion(LegalDocument document) => document switch
    {
        LegalDocument.Tos => Tos,
        LegalDocument.Privacy => Privacy,
        _ => throw new ArgumentOutOfRangeException(nameof(document), document, "Unknown legal document."),
    };
}
