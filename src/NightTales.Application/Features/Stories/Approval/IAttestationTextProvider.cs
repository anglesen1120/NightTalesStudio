using NightTales.Domain.Common;

namespace NightTales.Application.Features.Stories.Approval;

/// <summary>
/// The true-crime attestation text of one content language (02.3.3-T1, 02 README T29).
/// </summary>
/// <remarks>
/// The text ships as an embedded resource tracked by the prompt catalog, so this port is a thin read
/// over <c>IPromptCatalog</c>: the catalog has already hashed the file at start-up, and the version it
/// declares is what a stored tick is compared to. The gate and the read model take the provider
/// through DI; nothing here reads the file system.
/// </remarks>
public interface IAttestationTextProvider
{
    /// <summary>Reads the attestation clause text of one language and the version the manifest declares.</summary>
    /// <param name="language">The content language of the episode's series.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The manifest version (for example <c>ta.v1</c>) and the clause text.</returns>
    ValueTask<(string Version, string Text)> GetAsync(Language language, CancellationToken ct);
}
