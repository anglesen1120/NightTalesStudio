using ErrorOr;

namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// Resolves one deployment secret reference to its value (11 README §226's <c>…SecretRef</c> convention,
/// 09.2.6-T3:109): the configuration names a reference such as
/// <c>nts/&lt;env&gt;/api/YOUTUBE_CLIENT_SECRET</c>, and the composition root decides what that reference
/// means.
/// </summary>
/// <remarks>
/// The value never leaves the caller: an implementation must not log it, cache it outside the secret
/// manager's own cache or put it in an error description. A reference that cannot be read is a 503-shaped
/// failure of the caller's own vocabulary (for the OAuth clients:
/// <c>oauth_client_secret_unavailable</c>), never the exception text.
/// </remarks>
public interface ISecretProvider
{
    /// <summary>Reads the value <paramref name="secretRef"/> names.</summary>
    /// <param name="secretRef">The reference as it appears in configuration; never the secret itself.</param>
    /// <param name="ct">The request cancellation token.</param>
    /// <returns>The secret value, or an error when the reference is unknown or unreadable.</returns>
    Task<ErrorOr<string>> GetAsync(string secretRef, CancellationToken ct);
}
