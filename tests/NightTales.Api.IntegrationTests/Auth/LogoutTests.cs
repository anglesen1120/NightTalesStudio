using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence.Entities;
using NightTales.Infrastructure.Security;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// <c>POST /auth/logout</c> (09.3.2-T1 Tests table, WF12) against the <em>production</em> composition root,
/// with the real ES256 token the login answered: the bearer scheme, the rotation and the family rules are
/// exercised exactly as a deployment has them.
/// </summary>
/// <remarks>
/// The host is <see cref="ApiFactory.ProductionHost"/>, not the test-scheme factory, on purpose: logout is
/// the one route of this task that requires an authenticated caller, so this is where the ES256 access
/// token has to survive real validation (issuer, audience, lifetime, ES256 pinning) and reach
/// <c>HttpCurrentUser</c> through the raw <c>sub</c> claim.
/// </remarks>
public sealed class LogoutTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task Logout_RevokesFamily_AndClearsCookie()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = ApiFactory.ProductionHost(Database.ConnectionString);
        var client = host.CreateClient();
        var user = await AuthTestFlow.CreateUserAsync(host.Services);

        var (login, cookie) = await AuthTestFlow.LoginAsync(client, user.Email!, AuthTestFlow.Password, ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var request = AuthTestFlow.CookieRequest(
            HttpMethod.Post,
            AuthTestFlow.LogoutPath,
            cookie,
            AuthTestFlow.Origin);

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            await AuthTestFlow.AccessTokenAsync(login, ct));

        var response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The cookie is expired, not merely unused: the client cannot keep presenting a token the API has
        // revoked.
        var cleared = AuthTestFlow.SetCookie(response);
        cleared.ShouldNotBeNull();
        cleared!.ShouldContain($"{AuthTestFlow.CookieName}=;", Case.Sensitive);
        cleared.ToLowerInvariant().ShouldContain("expires=");

        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);
        rows.ShouldHaveSingleItem();
        rows[0].RevokedAt.ShouldNotBeNull();
        rows[0].RevokedReason.ShouldBe(RevocationReason.Logout, "WF12: logout revokes the family");

        // And the session really is over: the very cookie that worked a moment ago is refused.
        var (refresh, _) = await AuthTestFlow.RefreshAsync(client, cookie!, AuthTestFlow.Origin, ct);

        refresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(refresh)).ShouldBe(ErrorCodes.SessionRevoked);
    }

    [Fact]
    public async Task Logout_DoesNotRevokeOtherFamilies()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        // Two sign-ins are two families — a phone and a laptop of the same account. The test-scheme host is
        // used here on purpose: what it asserts is the family rule, and the real ES256 bearer round trip is
        // already proven by Logout_RevokesFamily_AndClearsCookie.
        var (_, firstCookie) = await AuthTestFlow.LoginAsync(factory.AnonymousClient(), user.Email!, AuthTestFlow.Password, ct);
        var (_, secondCookie) = await AuthTestFlow.LoginAsync(factory.AnonymousClient(), user.Email!, AuthTestFlow.Password, ct);

        secondCookie.ShouldNotBe(firstCookie, "each login opens its own family with its own token");
        secondCookie.ShouldNotBeNull();

        var logout = await AuthTestFlow.LogoutAsync(
            factory.ClientFor(user.Id),
            secondCookie!,
            AuthTestFlow.Origin,
            ct);

        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);
        rows.Count.ShouldBe(2);
        rows.Count(row => row.RevokedAt is null).ShouldBe(
            1,
            "logging out of one family must leave the other session alive");

        var live = rows.Single(row => row.RevokedAt is null);
        live.TokenHash.ShouldBe(
            AuthTestFlow.Hash(AuthTestFlow.CookieValue(firstCookie!)),
            "the surviving row is the first family");

        var (refresh, rotated) = await AuthTestFlow.RefreshAsync(
            factory.AnonymousClient(),
            firstCookie!,
            AuthTestFlow.Origin,
            ct);

        refresh.StatusCode.ShouldBe(HttpStatusCode.OK, "the other device keeps its session");
        rotated.ShouldNotBeNull();
    }

    [Fact]
    public async Task Logout_ForeignOrigin_ReturnsForbidden_AndKeepsFamily()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var (_, cookie) = await AuthTestFlow.LoginAsync(
            factory.AnonymousClient(),
            user.Email!,
            AuthTestFlow.Password,
            ct);

        // The Origin filter runs before authentication and before the handler: a cross-site POST must
        // neither revoke the family nor learn that the cookie is genuine.
        var foreign = await AuthTestFlow.LogoutAsync(
            factory.ClientFor(user.Id),
            cookie!,
            "https://evil.test",
            ct);

        foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AuthTestFlow.ProblemCodeAsync(foreign)).ShouldBe(ErrorCodes.Forbidden);

        var (refresh, rotated) = await AuthTestFlow.RefreshAsync(
            factory.AnonymousClient(),
            cookie!,
            AuthTestFlow.Origin,
            ct);

        refresh.StatusCode.ShouldBe(HttpStatusCode.OK, "the refused logout touched nothing");
        rotated.ShouldNotBeNull();
    }

    [Fact]
    public async Task Logout_Twice_LeavesTheFamilyRevoked()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var (_, cookie) = await AuthTestFlow.LoginAsync(
            factory.AnonymousClient(),
            user.Email!,
            AuthTestFlow.Password,
            ct);

        var first = await AuthTestFlow.LogoutAsync(factory.ClientFor(user.Id), cookie!, AuthTestFlow.Origin, ct);
        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var revokedAt = (await AuthTestFlow.RowsAsync(Database, user.Id))[0].RevokedAt;

        // The spec is silent on a second logout. The observed contract is idempotent: the lookup resolves
        // the revoked row to its family, the revocation statement matches no live row, and the answer is
        // still 204 — a retried logout does not resurrect, error, or re-revoke the family.
        var second = await AuthTestFlow.LogoutAsync(factory.ClientFor(user.Id), cookie!, AuthTestFlow.Origin, ct);

        second.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);
        rows.ShouldHaveSingleItem();
        rows[0].RevokedReason.ShouldBe(RevocationReason.Logout);
        rows[0].RevokedAt.ShouldBe(revokedAt, "the second logout rewrote nothing");

        var (refresh, _) = await AuthTestFlow.RefreshAsync(
            factory.AnonymousClient(),
            cookie!,
            AuthTestFlow.Origin,
            ct);
        refresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(refresh)).ShouldBe(ErrorCodes.SessionRevoked);
    }

    [Fact]
    public async Task Logout_SuspendedUser_StillRevokesOwnFamily()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var (_, cookie) = await AuthTestFlow.LoginAsync(
            factory.AnonymousClient(),
            user.Email!,
            AuthTestFlow.Password,
            ct);

        await AuthTestFlow.UpdateUserAsync(factory.Services, user.Id, suspended => suspended.Status = UserStatus.Suspended);

        // The access token already issued stays valid until it dies, so a suspended account can still end
        // its own session: suspension stops refresh (proved in RefreshTokenTests), never logout.
        var logout = await AuthTestFlow.LogoutAsync(factory.ClientFor(user.Id), cookie!, AuthTestFlow.Origin, ct);

        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);
        rows[0].RevokedReason.ShouldBe(RevocationReason.Logout);
    }

    [Fact]
    public async Task Logout_WithoutAccessToken_ReturnsUnauthorized_AndKeepsFamily()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = ApiFactory.ProductionHost(Database.ConnectionString);
        var client = host.CreateClient();
        var user = await AuthTestFlow.CreateUserAsync(host.Services);

        var (login, cookie) = await AuthTestFlow.LoginAsync(client, user.Email!, AuthTestFlow.Password, ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);

        var logout = await AuthTestFlow.LogoutAsync(client, cookie!, AuthTestFlow.Origin, ct);

        // LogoutCommand carries no AllowAnonymousRequest (WT12): the cookie alone is not enough — the
        // bearer token must authenticate the caller first. The session survives server-side; only the
        // caller's copy of the cookie is cleared, so the client cannot keep presenting it.
        logout.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(logout)).ShouldBe(ErrorCodes.Unauthorized);
        AuthTestFlow.SetCookie(logout).ShouldNotBeNull()!
            .ShouldContain($"{AuthTestFlow.CookieName}=;", Case.Sensitive);

        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);
        rows[0].RevokedAt.ShouldBeNull("an unauthenticated request revokes nothing");
    }

    [Fact]
    public async Task Logout_WithTamperedAccessToken_ReturnsUnauthorized_AndKeepsFamily()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = ApiFactory.ProductionHost(Database.ConnectionString);
        var client = host.CreateClient();
        var user = await AuthTestFlow.CreateUserAsync(host.Services);

        var (login, cookie) = await AuthTestFlow.LoginAsync(client, user.Email!, AuthTestFlow.Password, ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Forge an elevated caller: the payload now claims owner, under a signature that no longer covers
        // it. Validation must refuse the token before any claim is believed.
        var tampered = TamperedToken(await AuthTestFlow.AccessTokenAsync(login, ct));

        var logout = await LogoutWithBearerAsync(client, cookie!, tampered, ct);

        logout.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(logout)).ShouldBe(ErrorCodes.Unauthorized);

        var (refresh, _) = await AuthTestFlow.RefreshAsync(client, cookie!, AuthTestFlow.Origin, ct);
        refresh.StatusCode.ShouldBe(HttpStatusCode.OK, "the forged token did not end the real session");
    }

    [Fact]
    public async Task Logout_WithAlgNoneToken_ReturnsUnauthorized()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = ApiFactory.ProductionHost(Database.ConnectionString);
        var client = host.CreateClient();
        var user = await AuthTestFlow.CreateUserAsync(host.Services);

        var (login, cookie) = await AuthTestFlow.LoginAsync(client, user.Email!, AuthTestFlow.Password, ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The classic downgrade: an unsigned JWT claiming the user. ValidAlgorithms pins ES256 and
        // RequireSignedTokens is set, so the token is refused without touching the family.
        var algNone = $"{ToBase64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}"u8.ToArray())}"
            + $".{ToBase64Url(Encoding.UTF8.GetBytes(
                $"{{\"sub\":\"{user.Id}\",\"role\":\"user\",\"aud\":\"nts-api\"}}"))}."
            ;

        var logout = await LogoutWithBearerAsync(client, cookie!, algNone, ct);

        logout.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var (refresh, _) = await AuthTestFlow.RefreshAsync(client, cookie!, AuthTestFlow.Origin, ct);
        refresh.StatusCode.ShouldBe(HttpStatusCode.OK, "the unsigned token did not end the real session");
    }

    [Fact]
    public async Task Logout_WithExpiredAccessToken_ReturnsUnauthorized_AndKeepsFamily()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = ApiFactory.ProductionHost(Database.ConnectionString);
        var client = host.CreateClient();
        var user = await AuthTestFlow.CreateUserAsync(host.Services);

        var (login, cookie) = await AuthTestFlow.LoginAsync(client, user.Email!, AuthTestFlow.Password, ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);

        var familyId = (await AuthTestFlow.RowsAsync(Database, user.Id))[0].FamilyId;

        // A correctly-signed token that simply died: issued 20 minutes ago, so even the 30-second skew of
        // BD3 cannot save it. The client must refresh first; the session itself is untouched.
        var expired = host.Services
            .GetRequiredService<ITokenIssuer>()
            .IssueAccessToken(user, familyId, AuthMethod.Pwd, DateTimeOffset.UtcNow.AddMinutes(-20));

        var logout = await LogoutWithBearerAsync(client, cookie!, expired.Token, ct);

        logout.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(logout)).ShouldBe(ErrorCodes.Unauthorized);

        var (refresh, _) = await AuthTestFlow.RefreshAsync(client, cookie!, AuthTestFlow.Origin, ct);
        refresh.StatusCode.ShouldBe(HttpStatusCode.OK, "an expired access token ends nothing; refresh is the answer");
    }

    /// <summary>A logout request carrying <paramref name="bearer"/> as its access token.</summary>
    private static Task<HttpResponseMessage> LogoutWithBearerAsync(
        HttpClient client,
        string cookie,
        string bearer,
        CancellationToken ct)
    {
        var request = AuthTestFlow.CookieRequest(
            HttpMethod.Post,
            AuthTestFlow.LogoutPath,
            cookie,
            AuthTestFlow.Origin);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        return client.SendAsync(request, ct);
    }

    /// <summary>The same JWT with its payload re-encoded as <c>role: owner</c> — the signature no longer covers it.</summary>
    private static string TamperedToken(string token)
    {
        var parts = token.Split('.');
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(FromBase64Url(parts[1]))!;
        payload["role"] = JsonSerializer.SerializeToElement("owner");

        return $"{parts[0]}.{ToBase64Url(JsonSerializer.SerializeToUtf8Bytes(payload))}.{parts[2]}";
    }

    private static byte[] FromBase64Url(string value) => Convert.FromBase64String(
        value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
