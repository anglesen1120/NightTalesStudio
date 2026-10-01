using System.Net;
using System.Net.Http.Json;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Security;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// The QA edge cases of 09.10.4-T1 that the Tests table does not name: the acceptance window's edges,
/// replay inside a step, the no-pending-secret confirmation, re-enrolment inside one step, the dead
/// recovery set after regeneration, sid scoping and window boundaries of the re-authentication guard,
/// and the anonymous-caller answers of every MFA route.
/// </summary>
/// <remarks>
/// These tests assert the documented contract, not the implementation: a case that fails here is a
/// defect of the task, and the report names it.
/// </remarks>
public sealed class MfaEdgeCaseTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task QaTotp_NextStepCode_AcceptedOnce_TPlusTwoRejected()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var enrolment = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        clock.Advance(TimeSpan.FromSeconds(2 * MfaTestFlow.StepSeconds));
        Base32.TryDecode(enrolment.Secret, out var key).ShouldBeTrue();

        var step = Rfc6238.StepAt(clock.UtcNow, MfaTestFlow.StepSeconds);

        var next = await VerifyAsync(factory, user.Id, Rfc6238.Compute(key, step + 1));
        next.IsError.ShouldBeFalse("the ±1 window admits the next step (RFC 6238)");

        var replay = await VerifyAsync(factory, user.Id, Rfc6238.Compute(key, step + 1));
        replay.IsError.ShouldBeTrue("the accepted code cannot be presented again");
        replay.FirstError.Code.ShouldBe(ErrorCodes.InvalidMfaCode);

        var twoAhead = await VerifyAsync(factory, user.Id, Rfc6238.Compute(key, step + 2));
        twoAhead.IsError.ShouldBeTrue("a code two steps ahead is outside the window");
        twoAhead.FirstError.Code.ShouldBe(ErrorCodes.InvalidMfaCode);
    }

    [Fact]
    public async Task QaTotp_PreviousStepCode_AcceptedOnce()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var enrolment = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        clock.Advance(TimeSpan.FromSeconds(2 * MfaTestFlow.StepSeconds));
        Base32.TryDecode(enrolment.Secret, out var key).ShouldBeTrue();

        var step = Rfc6238.StepAt(clock.UtcNow, MfaTestFlow.StepSeconds);

        var previous = await VerifyAsync(factory, user.Id, Rfc6238.Compute(key, step - 1));
        previous.IsError.ShouldBeFalse("the ±1 window admits the previous step (RFC 6238)");

        var replay = await VerifyAsync(factory, user.Id, Rfc6238.Compute(key, step - 1));
        replay.IsError.ShouldBeTrue("the accepted code cannot be presented again");
        replay.FirstError.Code.ShouldBe(ErrorCodes.InvalidMfaCode);
    }

    [Fact]
    public async Task QaVerify_MalformedCode_Is400_AndChargesNothing()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database, user, clock.UtcNow, reauthenticatedAt: clock.UtcNow);
        using var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        var setup = await client.PostAsync(MfaTestFlow.SetupPath, content: null, Ct);
        setup.StatusCode.ShouldBe(HttpStatusCode.OK);

        var malformed = await client.PostAsJsonAsync(
            MfaTestFlow.VerifyPath, new { code = "12345" }, Ct);

        malformed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AuthTestFlow.ProblemCodeAsync(malformed)).ShouldBe(ErrorCodes.ValidationFailed);

        var nonDigit = await client.PostAsJsonAsync(
            MfaTestFlow.VerifyPath, new { code = "abcdef" }, Ct);
        nonDigit.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM audit_logs WHERE subject_user_id = '{user.Id}'"))
            .ShouldBe("1", "only mfa.setup_started exists: a malformed code reaches no flow");
    }

    [Fact]
    public async Task QaVerify_WithoutPendingSecret_Is401_InvalidMfaCode()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database, user, clock.UtcNow, reauthenticatedAt: clock.UtcNow);
        using var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        var verify = await client.PostAsJsonAsync(
            MfaTestFlow.VerifyPath,
            new { code = MfaTestFlow.WrongCode(Base32.NewSecret(), clock.UtcNow) },
            Ct);

        verify.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(verify)).ShouldBe(ErrorCodes.InvalidMfaCode);

        (await MfaTestFlow.ScalarAsync(
                Database, $"SELECT two_factor_enabled::text FROM users WHERE id = '{user.Id}'"))
            .ShouldBe("false");
        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM audit_logs WHERE action = '{MfaTestFlow.MfaEnrolledAction}' "
                + $"AND subject_user_id = '{user.Id}'"))
            .ShouldBe("0", "no enrolment row is written when nothing was confirmed");
    }

    [Fact]
    public async Task QaReenrolment_SameStepPending_CannotConfirm_NextStepCan()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var first = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);
        _ = first;

        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database, user, clock.UtcNow, reauthenticatedAt: clock.UtcNow);
        using var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        var setup = await client.PostAsync(MfaTestFlow.SetupPath, content: null, Ct);
        setup.StatusCode.ShouldBe(HttpStatusCode.OK);
        var pending = await MfaTestFlow.SetupSecretAsync(setup, Ct);

        // The first confirmation recorded users.last_totp_step for this step; a confirmation of a
        // brand-new pending secret inside the same step is refused as a replay — a real defect a QA
        // run must surface: the user sees a freshly generated secret answer invalid_mfa_code.
        var sameStep = await client.PostAsJsonAsync(
            MfaTestFlow.VerifyPath, new { code = MfaTestFlow.Code(pending, clock.UtcNow) }, Ct);

        sameStep.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            "a fresh pending secret must confirm even inside the step the last confirmation used — "
            + "the CAS is shared across slots");
        clock.Advance(TimeSpan.FromSeconds(2 * MfaTestFlow.StepSeconds));

        // Whatever the same-step answer was, a fresh enrolment a step later must confirm.
        var laterSetup = await client.PostAsync(MfaTestFlow.SetupPath, content: null, Ct);
        laterSetup.StatusCode.ShouldBe(HttpStatusCode.OK);
        var laterPending = await MfaTestFlow.SetupSecretAsync(laterSetup, Ct);

        var later = await client.PostAsJsonAsync(
            MfaTestFlow.VerifyPath, new { code = MfaTestFlow.Code(laterPending, clock.UtcNow) }, Ct);
        later.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task QaRecoveryCodes_RegenerateInvalidatesOldSet_AndAudits()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var enrolment = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database, user, clock.UtcNow, reauthenticatedAt: clock.UtcNow);
        using var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        var regenerated = await client.PostAsync(MfaTestFlow.RecoveryCodesPath, content: null, Ct);
        regenerated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var fresh = await MfaTestFlow.RecoveryCodesAsync(regenerated, Ct);

        var old = await RedeemAsync(factory, user.Id, enrolment.RecoveryCodes[0]);
        old.IsError.ShouldBeTrue("a code of the replaced set must be dead");
        old.FirstError.Code.ShouldBe(ErrorCodes.InvalidMfaCode);

        var alive = await RedeemAsync(factory, user.Id, fresh[0]);
        alive.IsError.ShouldBeFalse("the new set's codes redeem");

        (await CountAsync(factory, user.Id)).ShouldBe(9);

        (await MfaTestFlow.ScalarAsync(
                Database,
                "SELECT count(*)::text FROM audit_logs "
                + "WHERE action = 'mfa.recovery_codes_regenerated' "
                + $"AND subject_user_id = '{user.Id}'"))
            .ShouldBe("1", "the regeneration wrote its audit row");

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM audit_logs WHERE action = 'security.mfa_recovery_codes_regenerated' "
                + $"AND subject_user_id = '{user.Id}'"))
            .ShouldBe("1", "the regeneration raised its security alert row");
    }

    [Fact]
    public async Task QaRecoveryCodes_NoReauth_401_NotEnrolled_404()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var staleFamily = await MfaTestFlow.SeedFamilyAsync(Database, user, clock.UtcNow);
        using var stale = factory.ClientFor(user.Id, user.Role, sessionId: staleFamily);

        var refused = await stale.PostAsync(MfaTestFlow.RecoveryCodesPath, content: null, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(refused)).ShouldBe(ErrorCodes.ReauthRequired);

        var freshFamily = await MfaTestFlow.SeedFamilyAsync(
            Database, user, clock.UtcNow, reauthenticatedAt: clock.UtcNow);
        using var fresh = factory.ClientFor(user.Id, user.Role, sessionId: freshFamily);

        var notEnrolled = await fresh.PostAsync(MfaTestFlow.RecoveryCodesPath, content: null, Ct);
        notEnrolled.StatusCode.ShouldBe(
            HttpStatusCode.NotFound,
            "without an enrolled authenticator there is no set to replace (D10)");
        (await AuthTestFlow.ProblemCodeAsync(notEnrolled)).ShouldBe(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task QaDisable_WhenNotEnrolled_DoesNotRevokeFamilies()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var caller = await MfaTestFlow.SeedFamilyAsync(
            Database, user, clock.UtcNow, reauthenticatedAt: clock.UtcNow);
        var other = await MfaTestFlow.SeedFamilyAsync(Database, user, clock.UtcNow);

        using var client = factory.ClientFor(user.Id, user.Role, sessionId: caller);

        var response = await client.DeleteAsync(MfaTestFlow.MfaPath, Ct);

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM refresh_tokens WHERE family_id = '{other}' "
                + "AND revoked_at IS NULL"))
            .ShouldBe("1", "a no-op disable must not end the account's other sessions");

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM audit_logs WHERE action = 'mfa.disabled' "
                + $"AND subject_user_id = '{user.Id}'"))
            .ShouldBe("0", "nothing was disabled, so no mfa.disabled row may exist");

        (response.StatusCode == HttpStatusCode.NotFound).ShouldBeTrue(
            $"there is no MFA to disable: the same not_found the recovery-codes route gives (D10), "
            + $"observed {(int)response.StatusCode}");
    }

    [Fact]
    public async Task QaReauthGuard_StaleStamp_Blocks_FreshReauth_Passes()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock).MapEndpoints(MfaProbeEndpoints.Map);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var window = MfaProbeEndpoints.ReauthWindow;

        // Exactly at the window edge is stale (the guard's <=).
        var edgeFamily = await MfaTestFlow.SeedFamilyAsync(
            Database, user, clock.UtcNow, reauthenticatedAt: clock.UtcNow - window);
        using var edge = factory.ClientFor(user.Id, user.Role, sessionId: edgeFamily);

        var edgeResponse = await edge.GetAsync(MfaProbeEndpoints.ReauthGuardedPath, Ct);
        edgeResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(edgeResponse)).ShouldBe(ErrorCodes.ReauthRequired);

        // One second inside the window passes.
        var insideFamily = await MfaTestFlow.SeedFamilyAsync(
            Database,
            user,
            clock.UtcNow,
            reauthenticatedAt: clock.UtcNow - window + TimeSpan.FromSeconds(1));
        using var inside = factory.ClientFor(user.Id, user.Role, sessionId: insideFamily);

        (await inside.GetAsync(MfaProbeEndpoints.ReauthGuardedPath, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.OK, "a stamp inside the window satisfies the guard");

        // A fresh re-authentication re-arms the stale family.
        var reauth = await edge.PostAsJsonAsync(
            MfaTestFlow.ReauthPath, new { password = MfaTestFlow.Password }, Ct);
        reauth.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await edge.GetAsync(MfaProbeEndpoints.ReauthGuardedPath, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task QaReauth_IsScopedToTheFamily_OtherSession_NotReauthenticated()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock).MapEndpoints(MfaProbeEndpoints.Map);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var familyA = await MfaTestFlow.SeedFamilyAsync(Database, user, clock.UtcNow);
        var familyB = await MfaTestFlow.SeedFamilyAsync(Database, user, clock.UtcNow);

        using var clientA = factory.ClientFor(user.Id, user.Role, sessionId: familyA);
        using var clientB = factory.ClientFor(user.Id, user.Role, sessionId: familyB);

        var reauth = await clientA.PostAsJsonAsync(
            MfaTestFlow.ReauthPath, new { password = MfaTestFlow.Password }, Ct);
        reauth.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await clientA.GetAsync(MfaProbeEndpoints.ReauthGuardedPath, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.OK, "the re-authenticated family passes");

        var guardedB = await clientB.GetAsync(MfaProbeEndpoints.ReauthGuardedPath, Ct);
        guardedB.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(guardedB)).ShouldBe(
            ErrorCodes.ReauthRequired,
            "another session's re-authentication does not carry over (sid scoping)");
    }

    [Fact]
    public async Task QaMfaRoutes_Anonymous_Answer401_Unauthorized()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock).MapEndpoints(MfaProbeEndpoints.Map);
        using var anonymous = factory.AnonymousClient();

        // Collect every answer first, so a failure still reports what the rest of the surface did.
        var answers = new List<(string Route, HttpStatusCode Status, string? Code)>();

        var setup = await anonymous.PostAsync(MfaTestFlow.SetupPath, content: null, Ct);
        answers.Add(("POST /me/mfa/setup", setup.StatusCode, await AuthTestFlow.ProblemCodeAsync(setup)));

        var verify = await anonymous.PostAsJsonAsync(
            MfaTestFlow.VerifyPath, new { code = "123456" }, Ct);
        answers.Add(("POST /me/mfa/verify", verify.StatusCode, await AuthTestFlow.ProblemCodeAsync(verify)));

        var codes = await anonymous.PostAsync(MfaTestFlow.RecoveryCodesPath, content: null, Ct);
        answers.Add((
            "POST /me/mfa/recovery-codes",
            codes.StatusCode,
            await AuthTestFlow.ProblemCodeAsync(codes)));

        var disable = await anonymous.DeleteAsync(MfaTestFlow.MfaPath, Ct);
        answers.Add(("DELETE /me/mfa", disable.StatusCode, await AuthTestFlow.ProblemCodeAsync(disable)));

        var reauth = await anonymous.PostAsJsonAsync(
            MfaTestFlow.ReauthPath, new { password = MfaTestFlow.Password }, Ct);
        answers.Add(("POST /auth/reauth", reauth.StatusCode, await AuthTestFlow.ProblemCodeAsync(reauth)));

        var guarded = await anonymous.GetAsync(MfaProbeEndpoints.ReauthGuardedPath, Ct);
        answers.Add((
            "GET /__probe/mfa/reauth-guarded",
            guarded.StatusCode,
            await AuthTestFlow.ProblemCodeAsync(guarded)));

        // Report every route's answer, not just the first mismatch.
        var mismatches = answers
            .Where(answer => answer.Status != HttpStatusCode.Unauthorized
                || answer.Code != ErrorCodes.Unauthorized)
            .Select(answer => $"{answer.Route}: {(int)answer.Status} {answer.Code}")
            .ToList();

        mismatches.ShouldBeEmpty(
            "an anonymous caller is 401 unauthorized on every MFA route, never reauth_required or "
            + "session_revoked (09 README D4/D10); observed: "
            + string.Join(", ", answers.Select(a => $"{a.Route}={(int)a.Status}/{a.Code}")));
    }
    [Fact]
    public async Task QaReauth_NoSession_TokenWithoutSid_IsSessionRevoked()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        using var noSid = factory.ClientFor(user.Id, user.Role);

        var response = await noSid.PostAsJsonAsync(
            MfaTestFlow.ReauthPath, new { password = MfaTestFlow.Password }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.SessionRevoked);

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM audit_logs WHERE action = '{MfaTestFlow.ReauthFailedAction}' "
                + $"AND subject_user_id = '{user.Id}'"))
            .ShouldBe("0", "a caller with no family is charged nothing and writes no row");
    }

    [Fact]
    public async Task QaReauth_WrongCodes_BelowTen_LeaveNoGuessingRow()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var enrolment = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        clock.Advance(TimeSpan.FromMinutes(1));

        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database, user, clock.UtcNow, reauthenticatedAt: clock.UtcNow);
        using var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var wrong = await client.PostAsJsonAsync(
                MfaTestFlow.ReauthPath,
                new
                {
                    password = MfaTestFlow.Password,
                    code = MfaTestFlow.WrongCode(enrolment.Secret, clock.UtcNow),
                },
                Ct);

            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await AuthTestFlow.ProblemCodeAsync(wrong)).ShouldBe(ErrorCodes.InvalidMfaCode);
        }

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT reauth_attempts::text FROM refresh_tokens WHERE family_id = '{familyId}'"))
            .ShouldBe("4", "every wrong code charged the family counter");

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM audit_logs WHERE action = '{MfaTestFlow.GuessingLockedAction}' "
                + $"AND subject_user_id = '{user.Id}'"))
            .ShouldBe("0", "the WT35 row marks a tripped window — four wrong codes is not a trip");

        var login = await AuthTestFlow.LoginAsync(
            factory.AnonymousClient(), user.Email!, MfaTestFlow.Password, Ct);

        login.Response.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized,
            "four wrong codes must not lock the account (WT35 trips at ten)");
        (await AuthTestFlow.ProblemCodeAsync(login.Response)).ShouldBe(ErrorCodes.MfaRequired);
    }

    [Fact]
    public async Task QaReauth_SuccessfulReauth_DoesNotClearDurableCounter()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var enrolment = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database, user, clock.UtcNow, reauthenticatedAt: clock.UtcNow);
        using var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        var wrong = await client.PostAsJsonAsync(
            MfaTestFlow.ReauthPath,
            new { password = "wrong-password-9" },
            Ct);
        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The enrolment step was consumed by the confirmation itself, so the good attempt needs a
        // code of a later step.
        clock.Advance(TimeSpan.FromSeconds(MfaTestFlow.StepSeconds));

        var right = await client.PostAsJsonAsync(
            MfaTestFlow.ReauthPath,
            new { password = MfaTestFlow.Password, code = MfaTestFlow.Code(enrolment.Secret, clock.UtcNow) },
            Ct);
        right.StatusCode.ShouldBe(HttpStatusCode.OK);

        var attempts = await MfaTestFlow.ScalarAsync(
            Database,
            $"SELECT reauth_attempts::text FROM refresh_tokens WHERE family_id = '{familyId}'");

        attempts.ShouldBe(
            "1",
            "OBSERVED: the durable counter survives a success — whether that is intended is a ruling "
            + "the design text does not give");
    }

    [Fact]
    public async Task QaSetup_ResponseContract_AndNoStoreHeader()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services, role: PlatformRole.Owner);

        using var client = factory.ClientFor(
            user.Id, PlatformRole.Owner, scope: TestAuthHandler.EnrolmentScope);

        var setup = await client.PostAsync(MfaTestFlow.SetupPath, content: null, Ct);
        setup.StatusCode.ShouldBe(HttpStatusCode.OK);

        var secret = await MfaTestFlow.SetupSecretAsync(setup, Ct);
        secret.Length.ShouldBe(32, "the 20-byte secret encodes to exactly 32 RFC 4648 base32 characters");
        Base32.TryDecode(secret, out _).ShouldBeTrue(
            "the secret must be the RFC 4648 alphabet an authenticator application decodes");

        var uri = await MfaTestFlow.OtpauthUriAsync(setup, Ct);
        uri.ShouldContain("otpauth://totp/NightTales:");
        uri.ShouldContain("issuer=NightTales");
        uri.ShouldContain("algorithm=SHA1");
        uri.ShouldContain("digits=6");
        uri.ShouldContain("period=30");
        setup.Headers.CacheControl.ShouldNotBeNull(
            "the response carries a secret; no proxy may keep it (BD16)");
        setup.Headers.CacheControl!.ToString()
            .Contains("no-store", StringComparison.Ordinal)
            .ShouldBeTrue("the response carries a secret; no proxy may keep it (BD16)");

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM audit_logs WHERE action = 'mfa.setup_started' "
                + $"AND subject_user_id = '{user.Id}'"))
            .ShouldBe("1", "the enrolment start wrote its audit row");

        // No audit row may carry the secret: scan every row of this user and of the system actor.
        var leaked = await MfaTestFlow.ScalarAsync(
            Database,
            $"SELECT count(*)::text FROM audit_logs WHERE data::text LIKE '%{secret}%'");
        leaked.ShouldBe("0", "a secret never reaches the audit trail (BD16)");
    }

    [Fact]
    public async Task QaVerify_TenWrongCodes_ThenLogin_WritesExactlyOneGuessingRow()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        _ = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        clock.Advance(TimeSpan.FromMinutes(1));

        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database, user, clock.UtcNow, reauthenticatedAt: clock.UtcNow);
        using var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        var setup = await client.PostAsync(MfaTestFlow.SetupPath, content: null, Ct);
        setup.StatusCode.ShouldBe(HttpStatusCode.OK);
        var pending = await MfaTestFlow.SetupSecretAsync(setup, Ct);

        for (var attempt = 0; attempt < InMemoryMfaGuessWindow.Threshold; attempt++)
        {
            var wrong = await client.PostAsJsonAsync(
                MfaTestFlow.VerifyPath,
                new { code = MfaTestFlow.WrongCode(pending, clock.UtcNow) },
                Ct);

            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await AuthTestFlow.ProblemCodeAsync(wrong)).ShouldBe(ErrorCodes.InvalidMfaCode);
        }

        // The confirmation path answers an error, so its transaction rolls back: it must not consume the
        // trip report, or the committing login gate can never write the one WT35 row for the window.
        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM audit_logs WHERE action = '{MfaTestFlow.GuessingLockedAction}' "
                + $"AND subject_user_id = '{user.Id}'"))
            .ShouldBe("0", "a rolled-back verification writes no audit row and claims nothing");

        var login = await AuthTestFlow.LoginAsync(
            factory.AnonymousClient(), user.Email!, MfaTestFlow.Password, Ct);

        login.Response.StatusCode.ShouldBe(
            HttpStatusCode.TooManyRequests,
            "ten wrong confirmation codes block the sign-in (WT35)");
        (await AuthTestFlow.ProblemCodeAsync(login.Response)).ShouldBe(ErrorCodes.RateLimited);

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM audit_logs WHERE action = '{MfaTestFlow.GuessingLockedAction}' "
                + $"AND subject_user_id = '{user.Id}'"))
            .ShouldBe("1", "the whole tripped window leaves exactly one account_security row");
    }

    /// <summary>One verification through the real verifier, inside its own transaction.</summary>
    private static async Task<ErrorOr<long>> VerifyAsync(ApiFactory factory, Guid userId, string code)
    {
        using var scope = factory.Services.CreateScope();
        var verifier = scope.ServiceProvider.GetRequiredService<ITotpVerifier>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginAsync(Ct);

        var result = await verifier.VerifyAsync(userId, code, TotpSlot.Active, Ct);

        var committed = await unitOfWork.CommitAsync(Ct);
        committed.IsError.ShouldBeFalse();

        return result;
    }

    /// <summary>One recovery-code redemption, in its own transaction, exactly as a handler would run it.</summary>
    private static async Task<ErrorOr<Success>> RedeemAsync(ApiFactory factory, Guid userId, string code)
    {
        using var scope = factory.Services.CreateScope();
        var codes = scope.ServiceProvider.GetRequiredService<IRecoveryCodeService>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginAsync(Ct);

        var result = await codes.RedeemAsync(userId, code, Ct);

        var committed = await unitOfWork.CommitAsync(Ct);
        committed.IsError.ShouldBeFalse();

        return result;
    }

    /// <summary>How many codes the user still holds, through the port.</summary>
    private static async Task<int> CountAsync(ApiFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var codes = scope.ServiceProvider.GetRequiredService<IRecoveryCodeService>();

        return await codes.CountRemainingAsync(userId, Ct);
    }
}
