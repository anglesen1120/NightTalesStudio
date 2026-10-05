namespace NightTales.Application.Abstractions.Identity;

/// <summary>The startup-validated current versions, supplied by the runtime configuration.</summary>
public interface ICurrentLegalVersions
{
    /// <summary>Returns the configured current version of a document.</summary>
    string GetCurrentVersion(LegalDocument document);
}
