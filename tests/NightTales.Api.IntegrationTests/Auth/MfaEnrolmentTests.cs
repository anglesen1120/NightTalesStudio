using System.Net;
using System.Net.Http.Json;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// The MFA lifecycle routes of 09.10.4-T1 (Tests table): the recent-re-authentication rule of WT41, the
/// activation that rotates the stamp and issues the recovery codes, and the two disable rules of BD4.
/// </summary>
public sealed class MfaEnrolmentTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Setup_WhenEnrolled_RequiresReauth()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        _ = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        // A live session that has not re-authenticated since it was opened.
        var familyId = await MfaTestFlow.SeedFamilyAsync(Database, user, clock.UtcNow);
        using var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        var response = await client.PostAsync(MfaTestFlow.SetupPath, content: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.ReauthRequired);
    }

    [Fact]
    public async Task Setup_WithoutReauthAndWithoutEnrolmentToken_ReturnsReauthRequired()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var familyId = await MfaTestFlow.SeedFamilyAsync(Database, user, clock.UtcNow);
        using var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        var response = await client.PostAsync(MfaTestFlow.SetupPath, content: null, Ct);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized,
            "WT41: the re-authentication is required even while two_factor_enabled is false");
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.ReauthRequired);

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM user_tokens WHERE user_id = '{user.Id}'"))
            .ShouldBe("0", "a refused setup writes no secret");
    }

    [Fact]
    public async Task Verify_ActivatesAndRotatesStamp()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var stampBefore = await StampAsync(user.Id);

        var enrolment = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT two_factor_enabled::text FROM users WHERE id = '{user.Id}'"))
            .ShouldBe("true");

        (await StampAsync(user.Id)).ShouldNotBe(
            stampBefore,
            "the enrolment rotates the security stamp through ISecurityEventWriter (09.3.2-T2, D9)");

        enrolment.RecoveryCodes.Count.ShouldBe(10, "the confirmation issues the recovery codes once");

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM audit_logs WHERE action = '{MfaTestFlow.MfaEnrolledAction}' "
                + $"AND subject_user_id = '{user.Id}'"))
            .ShouldBe("1", "one mfa.enrolled row");

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM user_tokens WHERE user_id = '{user.Id}' "
                + "AND name IN ('totp_secret', 'recovery_codes')"))
            .ShouldBe("2", "the pending secret became the active one and the codes exist");
    }

    [Fact]
    public async Task Disable_AdminForbidden()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services, role: PlatformRole.Admin);
        _ = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database,
            user,
            clock.UtcNow,
            reauthenticatedAt: clock.UtcNow);

        using var client = factory.ClientFor(user.Id, PlatformRole.Admin, sessionId: familyId);

        var response = await client.DeleteAsync(MfaTestFlow.MfaPath, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "BD4: an admin cannot disable MFA");
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.Forbidden);

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT two_factor_enabled::text FROM users WHERE id = '{user.Id}'"))
            .ShouldBe("true", "a refused disable changes nothing");
    }

    [Fact]
    public async Task Disable_User_RevokesOtherFamilies()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        _ = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        var callerFamily = await MfaTestFlow.SeedFamilyAsync(
            Database,
            user,
            clock.UtcNow,
            reauthenticatedAt: clock.UtcNow);

        var otherFamily = await MfaTestFlow.SeedFamilyAsync(Database, user, clock.UtcNow);

        using var client = factory.ClientFor(user.Id, user.Role, sessionId: callerFamily);

        var response = await client.DeleteAsync(MfaTestFlow.MfaPath, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT revoked_reason FROM refresh_tokens WHERE family_id = '{otherFamily}' "
                + "AND revoked_at IS NOT NULL LIMIT 1"))
            .ShouldBe("mfa_change", "an MFA change ends the account's other sessions (BD4)");

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM refresh_tokens WHERE family_id = '{callerFamily}' "
                + "AND revoked_at IS NULL"))
            .ShouldBe("1", "the caller's own family survives its own request");

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT two_factor_enabled::text FROM users WHERE id = '{user.Id}'"))
            .ShouldBe("false");

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM user_tokens WHERE user_id = '{user.Id}'"))
            .ShouldBe("0", "both secrets and the recovery codes are deleted");
    }

    [Fact]
    public async Task Verify_RevokesOtherFamilies_OnEnableAndOnReplace()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        // A session opened before MFA existed: enabling must end it, or a stolen cookie keeps rotating
        // into fresh password-only access tokens.
        const string preEnrolmentToken = "pre-enrolment-session-token";
        _ = await AuthTestFlow.SeedFamilyAsync(
            Database, user, preEnrolmentToken, clock.UtcNow, TimeSpan.FromDays(14));

        _ = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        var (blocked, _) = await AuthTestFlow.RefreshAsync(
            factory.AnonymousClient(),
            $"{AuthTestFlow.CookieName}={preEnrolmentToken}",
            AuthTestFlow.Origin,
            Ct);

        blocked.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized,
            "enabling MFA ends the sessions opened before it (BD4/WF12)");
        (await AuthTestFlow.ProblemCodeAsync(blocked)).ShouldBe(ErrorCodes.SessionRevoked);

        // A session opened after the first enrolment: replacing the secret must end it too.
        const string laterToken = "post-enrolment-session-token";
        _ = await AuthTestFlow.SeedFamilyAsync(
            Database, user, laterToken, clock.UtcNow, TimeSpan.FromDays(14));

        var callerFamily = await MfaTestFlow.SeedFamilyAsync(
            Database, user, clock.UtcNow, reauthenticatedAt: clock.UtcNow);
        using var client = factory.ClientFor(user.Id, user.Role, sessionId: callerFamily);

        var setup = await client.PostAsync(MfaTestFlow.SetupPath, content: null, Ct);
        setup.StatusCode.ShouldBe(HttpStatusCode.OK);
        var pending = await MfaTestFlow.SetupSecretAsync(setup, Ct);

        var verify = await client.PostAsJsonAsync(
            MfaTestFlow.VerifyPath,
            new { code = MfaTestFlow.Code(pending, clock.UtcNow) },
            Ct);
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (replaced, _) = await AuthTestFlow.RefreshAsync(
            factory.AnonymousClient(),
            $"{AuthTestFlow.CookieName}={laterToken}",
            AuthTestFlow.Origin,
            Ct);

        replaced.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized,
            "replacing the authenticator ends the sessions the previous secret allowed");
        (await AuthTestFlow.ProblemCodeAsync(replaced)).ShouldBe(ErrorCodes.SessionRevoked);

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM refresh_tokens WHERE family_id = '{callerFamily}' "
                + "AND revoked_at IS NULL"))
            .ShouldBe("1", "the caller's own re-authenticated family survives its own request");
    }

    /// <summary>The user's current <c>security_stamp</c>.</summary>
    private async Task<string> StampAsync(Guid userId) =>
        (await MfaTestFlow.ScalarAsync(
            Database,
            $"SELECT security_stamp FROM users WHERE id = '{userId}'"))!;
}
