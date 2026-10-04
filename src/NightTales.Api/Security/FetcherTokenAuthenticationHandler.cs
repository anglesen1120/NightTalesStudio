using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NightTales.Api.Internal.Fetcher;

namespace NightTales.Api.Security;

/// <summary>
/// Compares a presented bearer token with <c>FETCHER_API_TOKEN</c> in constant time (02.5.2-T2, plan §5.2). The
/// configured token is read once at construction and kept only as its SHA-256; the presented value is hashed too, so the
/// comparison is over two 32-byte digests (<see cref="CryptographicOperations.FixedTimeEquals"/>) and its duration does
/// not depend on the length of either token.
/// </summary>
public sealed class FetcherTokenVerifier
{
    private readonly byte[]? _digest;

    /// <summary>Reads the token from <paramref name="configuration"/>.</summary>
    /// <param name="configuration">The host configuration; the secret store feeds <c>FETCHER_API_TOKEN</c> into it.</param>
    public FetcherTokenVerifier(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var token = configuration[FetcherInternalOptions.TokenKey];
        _digest = string.IsNullOrWhiteSpace(token) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    /// <summary>Whether a token is configured; without one every presented value is rejected.</summary>
    public bool IsConfigured => _digest is not null;

    /// <summary>Whether <paramref name="presented"/> is the configured token.</summary>
    /// <param name="presented">The bearer value from the request.</param>
    public bool Verify(string presented)
    {
        ArgumentNullException.ThrowIfNull(presented);

        var candidate = SHA256.HashData(Encoding.UTF8.GetBytes(presented));

        // Hash the candidate even when nothing is configured, so the two paths cost the same.
        return _digest is not null && CryptographicOperations.FixedTimeEquals(candidate, _digest);
    }
}

/// <summary>
/// The <c>FetcherToken</c> scheme of the internal listener (02.5.2-T2): a bearer token that is the shared secret of
/// the fetcher host. The only principal it produces is the anonymous-of-users <c>fetcher</c> machine identity — it has no
/// <c>sub</c> claim, so a request it authenticates owns no tenant row — and a user JWT is never accepted.
/// </summary>
/// <remarks>
/// A missing, malformed or wrong token is a bare <c>401</c> with no body and no detail; the header value is never
/// logged or echoed (D19).
/// </remarks>
public sealed class FetcherTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    FetcherTokenVerifier verifier)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>The scheme name.</summary>
    public const string SchemeName = "FetcherToken";

    /// <summary>The principal's name: the fetcher host as a machine identity.</summary>
    public const string PrincipalName = "fetcher";

    private const string BearerPrefix = "Bearer ";

    /// <summary>A token longer than this cannot be the secret; it is rejected before hashing.</summary>
    private const int MaxPresentedLength = 512;

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header) || header.Count != 1)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var value = header[0];
        if (value is null || !value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presented = value[BearerPrefix.Length..].Trim();
        if (presented.Length is 0 or > MaxPresentedLength || !verifier.Verify(presented))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, PrincipalName), new Claim(ClaimTypes.Role, PrincipalName)],
            SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    /// <inheritdoc />
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;

        return Task.CompletedTask;
    }
}
