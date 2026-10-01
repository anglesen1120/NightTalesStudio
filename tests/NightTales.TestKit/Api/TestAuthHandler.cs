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
/// <remarks>
/// The header grammar is closed and single-valued (SEC 09.2.1-T2): the user id must be a
/// <see cref="Guid"/>, the role must be one of the <see cref="PlatformRole"/> names
/// (case-insensitive, see <see cref="TryParseRole"/>), and each header must be present at most once —
/// so a numeric role, a comma-joined value or a duplicated header fails authentication instead of
/// quietly authenticating as something else.
/// </remarks>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name; <see cref="ApiFactory"/> also makes it the default scheme.</summary>
    public const string SchemeName = "Test";

    /// <summary>Header holding the user id; must parse as a <see cref="Guid"/>.</summary>
    public const string UserHeader = "X-Test-User";

    /// <summary>Header holding the role; absent means <see cref="PlatformRole.User"/>.</summary>
    public const string RoleHeader = "X-Test-Role";

    /// <summary>
    /// Header holding the token's scope (09 README BD4); absent means "a full access token". The only
    /// accepted value is <c>mfa_enrol</c>, so a test can drive the enrolment-only branch of the routes.
    /// </summary>
    public const string ScopeHeader = "X-Test-Scope";

    /// <summary>
    /// Header holding the refresh family (<c>sid</c>) of the fake token (09 README BD3); absent means the
    /// token belongs to no family, exactly like the enrolment-only token.
    /// </summary>
    public const string SessionHeader = "X-Test-Session";

    /// <summary>The only value <see cref="ScopeHeader"/> accepts: BD4's enrolment-only scope.</summary>
    public const string EnrolmentScope = "mfa_enrol";

    /// <summary>The claim type carrying the user id.</summary>
    public const string SubjectClaimType = "sub";

    /// <summary>The claim type carrying the role.</summary>
    public const string RoleClaimType = "role";

    /// <summary>The claim type carrying the token's scope.</summary>
    public const string ScopeClaimType = "scope";

    /// <summary>The claim type carrying the refresh family (09 README BD3).</summary>
    public const string SessionClaimType = "sid";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <summary>The claim value of <paramref name="role"/>, snake_case like the real JWT (README D16).</summary>
    public static string RoleClaimValue(PlatformRole role) => role.ToString().ToLowerInvariant();

    /// <summary>
    /// Parses a role header value against the closed set of <see cref="PlatformRole"/> names,
    /// case-insensitively. <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> is deliberately
    /// not used: it also accepts numeric values (<c>2</c> is <see cref="PlatformRole.Owner"/>) and
    /// comma-joined flag combinations, neither of which a test header may smuggle in
    /// (SEC 09.2.1-T2).
    /// </summary>
    public static bool TryParseRole(string? raw, out PlatformRole role)
    {
        foreach (var name in Enum.GetNames<PlatformRole>())
        {
            if (string.Equals(name, raw, StringComparison.OrdinalIgnoreCase))
            {
                role = Enum.Parse<PlatformRole>(name);
                return true;
            }
        }

        role = default;
        return false;
    }

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userValues = Request.Headers[UserHeader];
        if (userValues.Count == 0 || string.IsNullOrWhiteSpace(userValues.ToString()))
        {
            // No header: anonymous, exactly like a request without a bearer token.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (userValues.Count > 1)
        {
            // Two X-Test-User headers are ambiguous: never let the first one silently win.
            return Task.FromResult(AuthenticateResult.Fail($"Header '{UserHeader}' must be sent at most once."));
        }

        if (!Guid.TryParse(userValues.ToString(), out var userId))
        {
            return Task.FromResult(AuthenticateResult.Fail($"Header '{UserHeader}' must hold a Guid."));
        }

        var roleValues = Request.Headers[RoleHeader];
        if (roleValues.Count > 1)
        {
            return Task.FromResult(AuthenticateResult.Fail($"Header '{RoleHeader}' must be sent at most once."));
        }

        var rawRole = roleValues.ToString();
        var role = PlatformRole.User;
        if (!string.IsNullOrWhiteSpace(rawRole) && !TryParseRole(rawRole, out role))
        {
            return Task.FromResult(AuthenticateResult.Fail($"Header '{RoleHeader}' must name a {nameof(PlatformRole)}."));
        }

        var scopeValues = Request.Headers[ScopeHeader];
        if (scopeValues.Count > 1)
        {
            return Task.FromResult(AuthenticateResult.Fail($"Header '{ScopeHeader}' must be sent at most once."));
        }

        var scope = scopeValues.ToString();
        if (!string.IsNullOrWhiteSpace(scope)
            && !string.Equals(scope, EnrolmentScope, StringComparison.Ordinal))
        {
            // The platform issues exactly one scope (BD4): anything else is a malformed test header.
            return Task.FromResult(AuthenticateResult.Fail($"Header '{ScopeHeader}' must be '{EnrolmentScope}'."));
        }

        var sessionValues = Request.Headers[SessionHeader];
        if (sessionValues.Count > 1)
        {
            return Task.FromResult(AuthenticateResult.Fail($"Header '{SessionHeader}' must be sent at most once."));
        }

        Guid? sessionId = null;
        var rawSession = sessionValues.ToString();
        if (!string.IsNullOrWhiteSpace(rawSession))
        {
            if (!Guid.TryParse(rawSession, out var parsed))
            {
                return Task.FromResult(AuthenticateResult.Fail($"Header '{SessionHeader}' must hold a Guid."));
            }

            sessionId = parsed;
        }

        var claims = new List<Claim>
        {
            new(SubjectClaimType, userId.ToString()),
            new(RoleClaimType, RoleClaimValue(role)),
        };

        if (!string.IsNullOrWhiteSpace(scope))
        {
            claims.Add(new Claim(ScopeClaimType, scope));
        }

        if (sessionId is { } familyId)
        {
            claims.Add(new Claim(SessionClaimType, familyId.ToString()));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
