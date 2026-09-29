using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Domain.Common;

namespace NightTales.TestKit.Api;

/// <summary>
/// Test-only authentication scheme (09 README D13, CR-10): it trusts the <c>X-Test-User</c> /
/// <c>X-Test-Role</c> headers instead of a token, so integration tests can call the Api as any user
/// without a real login. It is registered **only** by <see cref="ApiFactory"/>: nothing under
/// <c>src/</c> references this type, and <c>ProductionHost_DoesNotRegisterTestScheme</c> proves the
/// host built from <c>Program</c> never authenticates through it.
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name; <see cref="ApiFactory"/> also makes it the default scheme.</summary>
    public const string SchemeName = "Test";

    /// <summary>Header holding the user id; must parse as a <see cref="Guid"/>.</summary>
    public const string UserHeader = "X-Test-User";

    /// <summary>Header holding the role; absent means <see cref="PlatformRole.User"/>.</summary>
    public const string RoleHeader = "X-Test-Role";

    /// <summary>The claim type carrying the user id.</summary>
    public const string SubjectClaimType = "sub";

    /// <summary>The claim type carrying the role.</summary>
    public const string RoleClaimType = "role";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <summary>The claim value of <paramref name="role"/>, snake_case like the real JWT (README D16).</summary>
    public static string RoleClaimValue(PlatformRole role) => role.ToString().ToLowerInvariant();

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var rawUser = Request.Headers[UserHeader].ToString();
        if (string.IsNullOrWhiteSpace(rawUser))
        {
            // No header: anonymous, exactly like a request without a bearer token.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!Guid.TryParse(rawUser, out var userId))
        {
            return Task.FromResult(AuthenticateResult.Fail($"Header '{UserHeader}' must hold a Guid."));
        }

        var rawRole = Request.Headers[RoleHeader].ToString();
        var role = PlatformRole.User;
        if (!string.IsNullOrWhiteSpace(rawRole) && !Enum.TryParse(rawRole, ignoreCase: true, out role))
        {
            return Task.FromResult(AuthenticateResult.Fail($"Header '{RoleHeader}' must name a {nameof(PlatformRole)}."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(SubjectClaimType, userId.ToString()),
                new Claim(RoleClaimType, RoleClaimValue(role)),
            ],
            SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
