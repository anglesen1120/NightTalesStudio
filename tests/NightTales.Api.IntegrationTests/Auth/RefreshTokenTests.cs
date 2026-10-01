using System.Net;
using System.Text.Json;
using Npgsql;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence.Entities;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Security;
using NightTales.Infrastructure.Security;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// Login, refresh rotation and the family rules of 09.3.2-T1 (Tests table) against a real PostgreSQL 17:
/// the status codes the wire contract fixes, and the committed rows behind each of them.
/// </summary>
/// <remarks>
/// <para>
/// Every 401 asserts database state as well ([RULED — README §B10 WT29]): a refusal that leaves nothing
/// behind is the bug these tests exist to catch, because the revocation, the counter and (in 09.3.6-T1)
/// the audit row are exactly what a rolled-back transaction would silently lose.
/// </para>
/// <para>
/// Two side effects the task's Design names cannot be asserted here: the <c>account_security</c> audit
/// rows (<c>login.succeeded</c>, <c>refresh.reuse_detected</c>) and the <c>refresh_token_reuse</c> e-mail
/// belong to <c>IAuditWriter</c> (09.3.6-T1) and <c>IEmailSender</c> (09.2.6-T3), whose tables and ports
/// do not exist in this wave. The revocation they will accompany is asserted instead.
/// </para>
/// </remarks>
public sealed class RefreshTokenTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly TimeSpan AdminIdleWindow = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan AdminAbsoluteWindow = TimeSpan.FromHours(12);

    /// <summary>The <c>amr</c> array a password login answers with (09.3-auth plan §5).</summary>
    private static readonly string[] PwdAmr = ["pwd"];

    [Fact]
    public async Task Login_UnknownEmail_AndWrongPassword_ReturnInvalidCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var client = factory.AnonymousClient();

        var (unknown, unknownCookie) = await AuthTestFlow.LoginAsync(
            client,
            $"nobody-{Guid.CreateVersion7():N}@nighttales.test",
            AuthTestFlow.Password,
            ct);

        unknown.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(unknown)).ShouldBe(ErrorCodes.InvalidCredentials);
        unknownCookie.ShouldBeNull();

        var (wrong, wrongCookie) = await AuthTestFlow.LoginAsync(client, user.Email!, "not-the-password-1", ct);

        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(wrong)).ShouldBe(
            ErrorCodes.InvalidCredentials,
            "an unknown address and a wrong password are the same answer (09.3.1-T1's timing rule)");
        wrongCookie.ShouldBeNull();

        (await AuthTestFlow.RowsAsync(Database, user.Id)).ShouldBeEmpty("a refused login opens no family");
    }

    [Fact]
    public async Task Login_UnconfirmedEmail_Returns403EmailNotVerified()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestFlow.CreateUserAsync(factory.Services, emailConfirmed: false);

        var (response, cookie) = await AuthTestFlow.LoginAsync(
            factory.AnonymousClient(),
            user.Email!,
            AuthTestFlow.Password,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.EmailNotVerified);
        cookie.ShouldBeNull();
        (await AuthTestFlow.RowsAsync(Database, user.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Login_SuspendedUser_ReturnsSessionRevoked()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestFlow.CreateUserAsync(factory.Services, status: UserStatus.Suspended);

        var (response, cookie) = await AuthTestFlow.LoginAsync(
            factory.AnonymousClient(),
            user.Email!,
            AuthTestFlow.Password,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.SessionRevoked);
        cookie.ShouldBeNull();
        (await AuthTestFlow.RowsAsync(Database, user.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Login_OwnerWithoutMfa_ReturnsMfaEnrolmentRequired_WithoutTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestFlow.CreateUserAsync(factory.Services, role: PlatformRole.Owner, twoFactorEnabled: false);

        var (response, cookie) = await AuthTestFlow.LoginAsync(
            factory.AnonymousClient(),
            user.Email!,
            AuthTestFlow.Password,
            ct);

        // BD4: an owner or admin always enrols before signing in, and this task answers without a challenge
        // document — the enrolment-only token stays off the wire until 09.3.2-T3 adds the pre-auth cookie.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.MfaEnrolmentRequired);

        var body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldNotContain("accessToken", Case.Sensitive, "an enrolment answer carries no session token");
        body.ShouldNotContain("token", Case.Sensitive, "no challenge or enrolment token is returned yet (WT22, WT39)");
        cookie.ShouldBeNull();
        (await AuthTestFlow.RowsAsync(Database, user.Id)).ShouldBeEmpty("the MFA answers set no refresh cookie");
    }

    [Fact]
    public async Task Login_AdminWithMfa_ReturnsMfaRequired_WithoutTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestFlow.CreateUserAsync(factory.Services, role: PlatformRole.Admin, twoFactorEnabled: true);

        var (response, cookie) = await AuthTestFlow.LoginAsync(
            factory.AnonymousClient(),
            user.Email!,
            AuthTestFlow.Password,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.MfaRequired);
        cookie.ShouldBeNull();
        (await AuthTestFlow.RowsAsync(Database, user.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Login_CreatesFamily_WithHashedToken_AndNoPlaintextInDb()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var (response, cookie) = await AuthTestFlow.LoginAsync(
            factory.AnonymousClient(),
            user.Email!,
            AuthTestFlow.Password,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var setCookie = AuthTestFlow.SetCookie(response);
        setCookie.ShouldNotBeNull("a completed login sets the refresh cookie");

        // BD3's flags, as the browser sees them: HttpOnly keeps the token away from script, Secure off plain
        // HTTP, SameSite=Strict off a cross-site form, and the path off every route that does not use it.
        var flags = setCookie!.ToLowerInvariant();
        flags.ShouldContain("httponly");
        flags.ShouldContain("secure");
        flags.ShouldContain("samesite=strict");
        flags.ShouldContain($"path={AuthTestFlow.RefreshCookiePath}");
        flags.ShouldContain("max-age=");

        var value = cookie![(AuthTestFlow.CookieName.Length + 1)..];
        value.Length.ShouldBe(43, "32 random bytes base64url (BD3)");

        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));
        body.GetProperty("accessToken").GetString().ShouldNotBeNullOrWhiteSpace();
        body.GetProperty("expiresAt").GetString().ShouldNotBeNullOrWhiteSpace();
        body.GetProperty("role").GetString().ShouldBe("user");
        body.GetProperty("amr").EnumerateArray().Select(element => element.GetString()).ShouldBe(PwdAmr);
        body.ToString().ShouldNotContain(value, Case.Sensitive, "the refresh token only ever travels in the cookie");

        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);
        rows.ShouldHaveSingleItem();
        rows[0].TokenHash.ShouldBe(AuthTestFlow.Hash(value), "only SHA-256 hex is stored");
        rows[0].TokenHash.ShouldNotBe(value);
        rows[0].RevokedAt.ShouldBeNull();
        rows[0].RevokedReason.ShouldBeNull();
        rows[0].ExpiresAt.ShouldBe(clock.UtcNow.AddDays(14), "a user family lives 14 days (BD3)");
    }

    [Fact]
    public async Task Login_RootRow_LastUsedAtEqualsCreatedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var (response, _) = await AuthTestFlow.LoginAsync(factory.AnonymousClient(), user.Email!, AuthTestFlow.Password, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);

        // [RULED — README §B10 WT33]: the idle clock of an owner/admin family starts at creation, so the very
        // first refresh is not already twenty-nine minutes into a thirty-minute window.
        rows[0].LastUsedAt.ShouldBe(rows[0].CreatedAt);
    }

    [Fact]
    public async Task Refresh_RotatesToken_AndLinksReplacedById()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var client = factory.AnonymousClient();

        var (_, first) = await AuthTestFlow.LoginAsync(client, user.Email!, AuthTestFlow.Password, ct);
        var before = (await AuthTestFlow.RowsAsync(Database, user.Id)).Single();

        clock.Advance(TimeSpan.FromMinutes(5));

        var (response, second) = await AuthTestFlow.RefreshAsync(client, first!, AuthTestFlow.Origin, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.ShouldNotBeNull();
        second.ShouldNotBe(first, "every refresh rotates the token (BD3)");

        var successor = second![(AuthTestFlow.CookieName.Length + 1)..];
        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);

        rows.Count.ShouldBe(2);
        var consumed = rows[0];
        var issued = rows[1];

        consumed.Id.ShouldBe(before.Id);
        consumed.RevokedReason.ShouldBe(RevocationReason.Rotated);
        consumed.RevokedAt.ShouldBe(clock.UtcNow);
        consumed.LastUsedAt.ShouldBe(clock.UtcNow);
        consumed.ReplacedById.ShouldBe(issued.Id, "the consumed row points at its successor");
        issued.FamilyId.ShouldBe(consumed.FamilyId, "a rotation never leaves the family");
        issued.TokenHash.ShouldBe(AuthTestFlow.Hash(successor));
        issued.RevokedAt.ShouldBeNull();
        issued.LastUsedAt.ShouldBeNull("only a family's first row starts its idle clock (WT33)");
        issued.ExpiresAt.ShouldBe(
            consumed.ExpiresAt,
            "a user family's ceiling is its start plus 14 days, which a rotation cannot extend");
        issued.ReauthenticatedAt.ShouldBe(consumed.ReauthenticatedAt, "WT32: the re-auth state is copied");
        issued.ReauthAttempts.ShouldBe(consumed.ReauthAttempts);
    }

    [Fact]
    public async Task Refresh_ReusedToken_RevokesFamily_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var client = factory.AnonymousClient();

        var (_, first) = await AuthTestFlow.LoginAsync(client, user.Email!, AuthTestFlow.Password, ct);
        (await AuthTestFlow.RefreshAsync(client, first!, AuthTestFlow.Origin, ct)).Response.StatusCode
            .ShouldBe(HttpStatusCode.OK);

        clock.Advance(TimeSpan.FromMinutes(1));

        var (response, _) = await AuthTestFlow.RefreshAsync(client, first!, AuthTestFlow.Origin, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.RefreshTokenReused);

        // The consumed cookie came back: the whole family is dead, the successor included. The audit row and
        // the reuse e-mail this answer also produces belong to 09.3.6-T1 and 09.2.6-T3 (see the class note).
        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(row => row.RevokedAt != null);
        rows[1].RevokedReason.ShouldBe(
            RevocationReason.ReuseDetected,
            "the live successor is revoked by the reuse detection, not by a rotation");
    }

    [Fact]
    public async Task Refresh_StaleRevokedCookie_IsSessionRevokedWithoutEmail()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var (_, cookie) = await AuthTestFlow.LoginAsync(factory.AnonymousClient(), user.Email!, AuthTestFlow.Password, ct);

        var logout = await AuthTestFlow.LogoutAsync(
            factory.ClientFor(user.Id),
            cookie!,
            AuthTestFlow.Origin,
            ct);
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var (response, _) = await AuthTestFlow.RefreshAsync(factory.AnonymousClient(), cookie!, AuthTestFlow.Origin, ct);

        // WT36: only a `rotated` row is theft. A cookie revoked by logout is stale, so the answer is
        // session_revoked, the family is not revoked again and no alert is queued.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.SessionRevoked);

        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);
        rows.ShouldHaveSingleItem("a stale cookie revokes nothing (no new row, no second revocation)");
        rows[0].RevokedReason.ShouldBe(RevocationReason.Logout, "the reason of the first revocation is untouched");
    }

    [Fact]
    public async Task Refresh_AdminIdleExpired_RevokesFamily()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services, role: PlatformRole.Admin);
        var client = factory.AnonymousClient();

        // An admin cannot sign in until 09.3.2-T3 completes the MFA login, so the family the login would
        // have opened is seeded directly, already idle past the window.
        var token = "seeded-admin-token-idle";
        await AuthTestFlow.SeedFamilyAsync(
            Database,
            user,
            token,
            clock.UtcNow - AdminIdleWindow - TimeSpan.FromMinutes(1),
            AdminAbsoluteWindow);
        var cookie = $"{AuthTestFlow.CookieName}={token}";

        var (response, _) = await AuthTestFlow.RefreshAsync(client, cookie!, AuthTestFlow.Origin, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.SessionRevoked);

        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);
        rows[0].RevokedReason.ShouldBe(RevocationReason.SessionIdle, "the 30-minute idle window is BD3's");
    }

    [Fact]
    public async Task Refresh_AdminAbsoluteTwelveHours_CannotBeExtended()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services, role: PlatformRole.Admin);
        var client = factory.AnonymousClient();
        var familyStart = clock.UtcNow;

        // Seeded for the same reason as the idle test: no admin can complete a login in this wave.
        const string seeded = "seeded-admin-token-ceiling";
        await AuthTestFlow.SeedFamilyAsync(Database, user, seeded, clock.UtcNow, AdminAbsoluteWindow);
        var cookie = $"{AuthTestFlow.CookieName}={seeded}";

        // An admin family has a 30-minute idle window, so the only way to reach the 12-hour ceiling is to keep
        // using the session; every round trip still has to stay inside the family's absolute lifetime.
        var step = TimeSpan.FromMinutes(25);
        while (clock.UtcNow - familyStart + step < AdminAbsoluteWindow)
        {
            clock.Advance(step);

            var (alive, next) = await AuthTestFlow.RefreshAsync(client, cookie!, AuthTestFlow.Origin, ct);

            alive.StatusCode.ShouldBe(
                HttpStatusCode.OK,
                $"the family is still inside its 12-hour ceiling at {clock.UtcNow - familyStart}");
            cookie = next;
        }

        clock.Advance(step + TimeSpan.FromMinutes(1));

        var (response, _) = await AuthTestFlow.RefreshAsync(client, cookie!, AuthTestFlow.Origin, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.SessionRevoked);

        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);
        // A family past its ceiling is revoked, which is the only outcome a rotation can have.
        rows.ShouldAllBe(row => row.RevokedAt != null);
        rows[^1].RevokedReason.ShouldBe(
            RevocationReason.SessionExpired,
            "the ceiling was reached: expires_at = family start + 12 hours (BD3)");
    }

    [Fact]
    public async Task Refresh_UserAbsoluteFourteenDays_Expires()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var client = factory.AnonymousClient();

        var (_, cookie) = await AuthTestFlow.LoginAsync(client, user.Email!, AuthTestFlow.Password, ct);

        clock.Advance(TimeSpan.FromDays(13));

        var (alive, next) = await AuthTestFlow.RefreshAsync(client, cookie!, AuthTestFlow.Origin, ct);

        alive.StatusCode.ShouldBe(HttpStatusCode.OK, "a user family has no idle timeout, only its 14 days");

        clock.Advance(TimeSpan.FromDays(2));

        var (expired, _) = await AuthTestFlow.RefreshAsync(client, next!, AuthTestFlow.Origin, ct);

        expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "day 15 is past the 14-day ceiling (BD3)");
        (await AuthTestFlow.ProblemCodeAsync(expired)).ShouldBe(ErrorCodes.SessionRevoked);

        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);
        rows[^1].RevokedReason.ShouldBe(RevocationReason.SessionExpired);
    }

    [Fact]
    public async Task Refresh_SuspendedUser_RevokesFamily_ReturnsSessionRevoked()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var client = factory.AnonymousClient();

        var (_, cookie) = await AuthTestFlow.LoginAsync(client, user.Email!, AuthTestFlow.Password, ct);

        await AuthTestFlow.UpdateUserAsync(factory.Services, user.Id, suspended => suspended.Status = UserStatus.Suspended);

        var (response, newCookie) = await AuthTestFlow.RefreshAsync(client, cookie!, AuthTestFlow.Origin, ct);

        // In P this is the only place a suspension is enforced: without it the account would keep renewing
        // for the family's whole lifetime (WF12).
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.SessionRevoked);
        newCookie.ShouldNotBeNull()!.ShouldStartWith($"{AuthTestFlow.CookieName}=", Case.Sensitive);

        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);
        // The rotation itself succeeded before the re-check: the consumed row is `rotated`, and the successor
        // the re-check refused is revoked with the reason the suspension earned.
        rows.ShouldAllBe(row => row.RevokedAt != null);
        rows[^1].RevokedReason.ShouldBe(RevocationReason.AccountNotActive);
    }

    [Fact]
    public async Task Refresh_UnconfirmedEmail_ReturnsEmailNotVerifiedWithoutRevoking()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var client = factory.AnonymousClient();

        var (_, cookie) = await AuthTestFlow.LoginAsync(client, user.Email!, AuthTestFlow.Password, ct);

        await AuthTestFlow.UpdateUserAsync(factory.Services, user.Id, unconfirmed => unconfirmed.EmailConfirmed = false);

        var (response, newCookie) = await AuthTestFlow.RefreshAsync(client, cookie!, AuthTestFlow.Origin, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.EmailNotVerified);
        newCookie.ShouldBeNull();

        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);
        rows.ShouldHaveSingleItem("the rotation rolled back: no successor was written");
        rows[0].RevokedAt.ShouldBeNull("the family stays live, so the user can verify the address and keep the session");
        rows[0].LastUsedAt.ShouldBe(rows[0].CreatedAt, "the rolled-back rotation consumed nothing");

        // The promise the rollback exists for: verifying the address restores the same session.
        await AuthTestFlow.UpdateUserAsync(factory.Services, user.Id, confirmed => confirmed.EmailConfirmed = true);

        var (recovered, recoveredCookie) = await AuthTestFlow.RefreshAsync(client, cookie!, AuthTestFlow.Origin, ct);

        recovered.StatusCode.ShouldBe(HttpStatusCode.OK, "the very same cookie still refreshes once the address is verified");
        recoveredCookie.ShouldNotBeNull();
    }

    [Fact]
    public async Task Refresh_TwoConcurrentRequests_LoserSeesReuse()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var (_, cookie) = await AuthTestFlow.LoginAsync(factory.AnonymousClient(), user.Email!, AuthTestFlow.Password, ct);

        // Two tabs, one cookie: the row lock serialises them, and the loser sees the `rotated` row and counts
        // as theft — the documented behaviour the SPA avoids with a single-flight refresh (10's client note).
        var first = factory.AnonymousClient();
        var second = factory.AnonymousClient();

        var responses = await Task.WhenAll(
            AuthTestFlow.RefreshAsync(first, cookie!, AuthTestFlow.Origin, ct),
            AuthTestFlow.RefreshAsync(second, cookie!, AuthTestFlow.Origin, ct));

        responses.Count(result => result.Response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        var loser = responses.Single(result => result.Response.StatusCode != HttpStatusCode.OK);

        loser.Response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(loser.Response)).ShouldBe(ErrorCodes.RefreshTokenReused);

        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);
        // Reuse revokes the family the winner rotated into as well.
        rows.ShouldAllBe(row => row.RevokedAt != null);
    }

    [Fact]
    public async Task Refresh_ForeignOrigin_ReturnsForbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var client = factory.AnonymousClient();

        var (_, cookie) = await AuthTestFlow.LoginAsync(client, user.Email!, AuthTestFlow.Password, ct);

        var foreign = await AuthTestFlow.RefreshAsync(client, cookie!, "https://evil.test", ct);
        foreign.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AuthTestFlow.ProblemCodeAsync(foreign.Response)).ShouldBe(ErrorCodes.Forbidden);

        var missing = await AuthTestFlow.RefreshAsync(client, cookie!, origin: null, ct);
        missing.Response.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "a cookie-authenticated POST without an Origin is refused, never assumed same-origin");

        var same = await AuthTestFlow.RefreshAsync(client, cookie!, AuthTestFlow.Origin, ct);
        same.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_UnknownCookie_ReturnsSessionRevoked()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var (response, cookie) = await AuthTestFlow.RefreshAsync(
            factory.AnonymousClient(),
            $"{AuthTestFlow.CookieName}=never-issued",
            AuthTestFlow.Origin,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.SessionRevoked);
        AuthTestFlow.SetCookie(response).ShouldNotBeNull()!.ShouldStartWith(
            $"{AuthTestFlow.CookieName}=",
            Case.Sensitive,
            "an unknown cookie is cleared, so the client stops presenting it");
        (await AuthTestFlow.RowsAsync(Database, user.Id)).ShouldBeEmpty("an unknown cookie revokes nothing");
    }

    [Fact]
    public async Task Refresh_WithoutCookie_ReturnsSessionRevoked()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());

        using var request = AuthTestFlow.CookieRequest(HttpMethod.Post, AuthTestFlow.RefreshPath, cookie: null, AuthTestFlow.Origin);
        var response = await factory.AnonymousClient().SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "WT21: a missing refresh cookie is session_revoked");
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.SessionRevoked);
    }

    [Fact]
    public async Task RefreshAndLogoutInterleaved_LeaveNoLiveSuccessor()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        const string presented = "interleaved-family-token";
        await AuthTestFlow.SeedFamilyAsync(Database, user, presented, clock.UtcNow, TimeSpan.FromDays(14));
        var familyId = (await AuthTestFlow.RowsAsync(Database, user.Id)).Single().FamilyId;

        // The rotation runs in its own scope and holds the family lock inside an open transaction.
        await using var refreshScope = factory.Services.CreateAsyncScope();
        var refreshUnitOfWork = refreshScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var refreshStore = refreshScope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        await refreshUnitOfWork.BeginAsync(ct);

        var rotated = await refreshStore.RotateAsync(presented, new RequestInfo(null, null), ct);
        rotated.IsError.ShouldBeFalse();

        // The revocation starts while the rotation is still open: it must wait for the family lock, and its
        // statement must then see the successor the rotation inserted (09.3.2-T1 CR-1).
        await using var logoutScope = factory.Services.CreateAsyncScope();
        var logoutUnitOfWork = logoutScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var logoutStore = logoutScope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        await logoutUnitOfWork.BeginAsync(ct);

        var revoke = logoutStore.RevokeFamilyAsync(familyId, "logout", ct);

        await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        revoke.IsCompleted.ShouldBeFalse("the revocation waits for the family lock the rotation holds");

        (await refreshUnitOfWork.CommitAsync(ct)).IsError.ShouldBeFalse();
        (await revoke).IsError.ShouldBeFalse();
        (await logoutUnitOfWork.CommitAsync(ct)).IsError.ShouldBeFalse();

        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);
        rows.Count.ShouldBe(2, "the rotation wrote its successor");
        rows.ShouldAllBe(row => row.RevokedAt != null, "the revocation must reach the successor it could not see");
    }

    [Fact]
    public async Task Refresh_FailureCommitsDbStateBefore401()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services, role: PlatformRole.Admin);

        const string seeded = "seeded-admin-token-committed";
        await AuthTestFlow.SeedFamilyAsync(
            Database,
            user,
            seeded,
            clock.UtcNow - AdminIdleWindow - TimeSpan.FromMinutes(1),
            AdminAbsoluteWindow);
        var cookie = $"{AuthTestFlow.CookieName}={seeded}";

        var (response, _) = await AuthTestFlow.RefreshAsync(factory.AnonymousClient(), cookie!, AuthTestFlow.Origin, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // WT29: the outcome is a value, so the revocation committed before the endpoint mapped the 401. The
        // proof is a context that knows nothing about the request's transaction.
        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);
        rows[0].RevokedAt.ShouldNotBeNull();
        rows[0].RevokedReason.ShouldBe(RevocationReason.SessionIdle);
        (await AuthTestFlow.ScalarAsync(
            Database,
            "SELECT count(*)::text FROM refresh_tokens "
            + $"WHERE user_id = '{user.Id}' AND revoked_at IS NOT NULL")).ShouldBe("1");
    }

    [Fact]
    public async Task FamilyRevocation_PublishesSecurityNotification()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var (_, cookie) = await AuthTestFlow.LoginAsync(factory.AnonymousClient(), user.Email!, AuthTestFlow.Password, ct);

        var notifications = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listener = new NpgsqlConnection(Database.ConnectionString);
        await listener.OpenAsync(ct);
        listener.Notification += (_, args) => notifications.TrySetResult(args.Payload);

        await using (var listen = new NpgsqlCommand("LISTEN security", listener))
        {
            await listen.ExecuteNonQueryAsync(ct);
        }

        // Npgsql only delivers notifications while the connection is waiting for one.
        var awaiting = listener.WaitAsync(ct);

        await AuthTestFlow.LogoutAsync(factory.ClientFor(user.Id), cookie!, AuthTestFlow.Origin, ct);

        var payload = await notifications.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        payload.ShouldBe(user.Id.ToString(), "WT37: the security notification names the user whose sockets must abort");

        await awaiting.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }

    [Fact]
    public async Task Refresh_TamperedCookieValue_ReturnsSessionRevoked_AndKeepsFamily()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var client = factory.AnonymousClient();

        var (_, cookie) = await AuthTestFlow.LoginAsync(client, user.Email!, AuthTestFlow.Password, ct);

        // A cookie whose value was flipped mid-way: it hashes to a row nobody owns, which must be the
        // unknown-cookie answer — and the genuine family it was cut from must stay live.
        var value = AuthTestFlow.CookieValue(cookie!);
        var tampered = $"{AuthTestFlow.CookieName}={value[..^4]}{(value[^4] == 'a' ? 'b' : 'a')}{value[^3..]}";

        var (response, _) = await AuthTestFlow.RefreshAsync(client, tampered, AuthTestFlow.Origin, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.SessionRevoked);

        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);
        rows.ShouldHaveSingleItem();
        rows[0].RevokedAt.ShouldBeNull("tampering with a presented value must not touch the real session");

        var (alive, next) = await AuthTestFlow.RefreshAsync(client, cookie!, AuthTestFlow.Origin, ct);
        alive.StatusCode.ShouldBe(HttpStatusCode.OK);
        next.ShouldNotBeNull();
    }

    [Fact]
    public async Task Refresh_EmptyCookieValue_ReturnsSessionRevoked_AndClearsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());

        // `__Secure-nts_rt=` with nothing after the equals sign is the same "no usable token" case as a
        // missing cookie (WT21), and the dead cookie is cleared so the client stops sending it.
        var (response, _) = await AuthTestFlow.RefreshAsync(
            factory.AnonymousClient(),
            $"{AuthTestFlow.CookieName}=",
            AuthTestFlow.Origin,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.SessionRevoked);
        AuthTestFlow.SetCookie(response).ShouldNotBeNull()!.ShouldStartWith(
            $"{AuthTestFlow.CookieName}=",
            Case.Sensitive,
            "an empty cookie value is cleared exactly like an unknown one");
    }

    [Fact]
    public async Task Refresh_DuplicatedOriginHeader_ReturnsForbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var (_, cookie) = await AuthTestFlow.LoginAsync(
            factory.AnonymousClient(),
            user.Email!,
            AuthTestFlow.Password,
            ct);

        // Two Origin values is malformed input, not "the first one wins": a CSRF gate refuses it even when
        // one of the two names the real origin.
        using var request = AuthTestFlow.CookieRequest(
            HttpMethod.Post,
            AuthTestFlow.RefreshPath,
            cookie,
            AuthTestFlow.Origin);
        request.Headers.Add(AuthTestFlow.OriginHeaderName, "https://evil.test");

        var response = await factory.AnonymousClient().SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.Forbidden);

        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);
        rows[0].RevokedAt.ShouldBeNull("the refused request never reached the rotation");
    }
}
