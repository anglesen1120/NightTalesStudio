using ErrorOr;

namespace NightTales.Application.Abstractions.Publishing;

/// <summary>
/// Resolves one configured OAuth client to its id and secret (06 README D5): the connect pins
/// <see cref="ActiveClientId"/> on the state row, and the callback resolves the <em>state's</em> client
/// so a client rotation between the two calls cannot break an in-flight connect (F19).
/// </summary>
public interface IOAuthClientResolver
{
    /// <summary>The client id new connects use (<c>YouTube:ActiveClientId</c>).</summary>
    string ActiveClientId { get; }

    /// <summary>
    /// Resolves <paramref name="oauthClientId"/> to its client and secret.
    /// </summary>
    /// <param name="oauthClientId">The configured client id to resolve.</param>
    /// <param name="ct">The request cancellation token.</param>
    /// <returns>
    /// The client, 404 <c>oauth_client_unknown</c> when no such client is configured, or 503
    /// <c>oauth_client_secret_unavailable</c> when the configured secret reference cannot be read. The
    /// secret never reaches the description.
    /// </returns>
    Task<ErrorOr<OAuthClient>> ResolveAsync(string oauthClientId, CancellationToken ct);
}
