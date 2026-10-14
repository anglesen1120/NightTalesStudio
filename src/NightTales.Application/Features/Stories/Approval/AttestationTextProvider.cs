using NightTales.Application.Abstractions.Prompts;
using NightTales.Domain.Common;

namespace NightTales.Application.Features.Stories.Approval;

/// <summary>
/// The <see cref="IAttestationTextProvider"/> over the D5 prompt catalog (02.3.3-T1, 02 README T29):
/// the clause text of <c>prompts/shared/attestation.ta.v1.{vi|en}.md</c> with the version the release
/// manifest declares for that file.
/// </summary>
/// <remarks>
/// The catalog has already resolved and hashed every tracked file at start-up, so this is a lookup with
/// no I/O and no file system. A key the manifest does not track throws
/// <see cref="PromptCatalogException"/>; the read model treats that as a missing text (never a failed
/// read), because the text is displayed, not enforced.
/// </remarks>
/// <param name="catalog">The validated prompt catalog of the host.</param>
public sealed class AttestationTextProvider(IPromptCatalog catalog) : IAttestationTextProvider
{
    /// <inheritdoc />
    public ValueTask<(string Version, string Text)> GetAsync(Language language, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var attestation = catalog.GetAttestation(language);

        return ValueTask.FromResult((attestation.Version, attestation.Text));
    }
}
