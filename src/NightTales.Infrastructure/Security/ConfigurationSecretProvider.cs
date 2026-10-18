using ErrorOr;
using Microsoft.Extensions.Configuration;
using NightTales.Application.Abstractions.Security;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The configuration-backed <see cref="ISecretProvider"/> (06.1.2-T1, owner decision 2026-10-18): the client
/// secret a deployment stores under the platform's <c>nts/&lt;env&gt;/api/YOUTUBE_CLIENT_SECRET</c> name is read
/// by that name's <em>last</em> path segment, so one reference works as the secret-manager entry, as the
/// environment variable <c>YOUTUBE_CLIENT_SECRET</c> and as an <c>appsettings</c> key in Development.
/// </summary>
/// <remarks>
/// <para>
/// Resolution is lazy and on demand: nothing here runs at start-up, so every host boots without the secret and
/// only a connect resolves one. A missing or blank value answers <c>secret_unavailable</c> with a constant
/// message that carries neither the reference nor any value, so no error body and no log line can reveal one.
/// </para>
/// <para>
/// The provider is a name-to-value lookup, not a vault: a production secret manager (11 README A3/A12) replaces
/// this registration without touching a caller, because every caller depends on
/// <see cref="ISecretProvider"/> only.
/// </para>
/// </remarks>
/// <param name="configuration">The host configuration that carries the secret, by its last path segment.</param>
public sealed class ConfigurationSecretProvider(IConfiguration configuration) : ISecretProvider
{
    /// <summary>The code of a secret no configuration source could answer.</summary>
    internal const string SecretUnavailable = "secret_unavailable";

    /// <inheritdoc />
    public Task<ErrorOr<string>> GetAsync(string secretRef, CancellationToken ct)
    {
        var value = configuration[KeyFor(secretRef)];

        return Task.FromResult(string.IsNullOrWhiteSpace(value)
            ? Error.Unexpected(SecretUnavailable, "The secret is not available from configuration.")
            : (ErrorOr<string>)value);
    }

    /// <summary>
    /// The configuration key a secret reference reads: its last non-empty path segment, so
    /// <c>nts/prod/api/YOUTUBE_CLIENT_SECRET</c> and a bare <c>YOUTUBE_CLIENT_SECRET</c> both read the key
    /// <c>YOUTUBE_CLIENT_SECRET</c>. A reference the resolver could not turn into a key answers with the empty
    /// key, which no configuration source carries, so the call fails closed instead of reading a neighbouring key.
    /// </summary>
    private static string KeyFor(string secretRef)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            return string.Empty;
        }

        var separator = secretRef.LastIndexOf('/');

        return (separator < 0 ? secretRef : secretRef[(separator + 1)..]).Trim();
    }
}
