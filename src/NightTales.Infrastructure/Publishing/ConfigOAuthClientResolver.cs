using ErrorOr;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Publishing;

namespace NightTales.Infrastructure.Publishing;

/// <summary>
/// The configuration-backed <see cref="IOAuthClientResolver"/> of the P deployment (06.1.2-T1, 06 README D5): the
/// active client id and the <c>YouTube:Clients:&lt;id&gt;:SecretRef</c> map come from <see cref="YouTubeOptions"/>,
/// and the secret itself is resolved on demand through <see cref="ISecretProvider"/>, so no secret is read while
/// a host starts and no secret ever reaches an option.
/// </summary>
/// <remarks>
/// The resolution answers two constant failures and never a provider detail: an id the map does not carry is
/// <c>oauth_client_unknown</c> (404), a secret configuration cannot answer is <c>oauth_client_secret_unavailable</c>
/// (mapped to 503). Neither message carries the reference or a value, so a client id that exists cannot be
/// distinguished from one that does not except by the status the Api maps.
/// </remarks>
/// <param name="options">The bound <c>YouTube</c> section (the client map and the active id).</param>
/// <param name="secrets">The secret-resolution port, asked only for the entry's reference.</param>
public sealed class ConfigOAuthClientResolver(
    IOptions<YouTubeOptions> options,
    ISecretProvider secrets) : IOAuthClientResolver
{
    /// <summary>The code of a client id the configured map does not carry.</summary>
    internal const string ClientUnknown = "oauth_client_unknown";

    /// <summary>The code of a configured client whose secret configuration could not answer.</summary>
    internal const string ClientSecretUnavailable = "oauth_client_secret_unavailable";

    /// <inheritdoc />
    public string ActiveClientId => options.Value.ActiveClientId;

    /// <inheritdoc />
    public async Task<ErrorOr<OAuthClient>> ResolveAsync(string oauthClientId, CancellationToken ct)
    {
        var clients = options.Value.Clients;

        // An unknown id and a blank reference both fail here, before the secret port is asked: a client id that is
        // not in the map must not be able to probe the secret sources with a reference of its own.
        if (string.IsNullOrWhiteSpace(oauthClientId)
            || clients is null
            || !clients.TryGetValue(oauthClientId, out var client)
            || string.IsNullOrWhiteSpace(client?.SecretRef))
        {
            return Error.NotFound(ClientUnknown, "The OAuth client is not configured.");
        }

        var secret = await secrets.GetAsync(client.SecretRef, ct);

        return secret.IsError || string.IsNullOrWhiteSpace(secret.Value)
            ? Error.Unexpected(ClientSecretUnavailable, "The OAuth client secret is unavailable.")
            : new OAuthClient(oauthClientId, secret.Value);
    }
}
