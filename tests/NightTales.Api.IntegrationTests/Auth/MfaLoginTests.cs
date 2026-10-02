using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NightTales.Api.RateLimiting;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Entities;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// The MFA login step of 09.3.2-T3 (Tests table): <c>POST /auth/login</c> answers 401 <c>mfa_required</c>
/// with the single-use <c>mfaToken</c> and the <c>__Secure-nts_mfa</c> pre-auth cookie, and
/// <c>POST /auth/login/mfa</c> exchanges the two secrets plus a TOTP or recovery code for the session.
/// </summary>
/// <remarks>
/// <para>
/// Every refusal asserts the committed row state as well ([RULED — README §B10 WT29]): the attempt
/// counter, the challenge's death and the audit rows are exactly what a rolled-back transaction would
/// silently lose, so a 401 that leaves nothing behind is one of the bugs this class exists to catch.
/// </para>
/// <para>
/// The TOTP verifier is hand-written (09 README D13) where the test is about the challenge's own rules;
/// the tests that are about the code itself (replay, recovery codes, redaction) go through the real
/// verifier or the real recovery-code store.
/// </para>
/// </remarks>
public sealed class MfaLoginTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>BD4's cookie name; the contract the SPA is frozen against (10.3.3 C2).</summary>
    private const string MfaCookieName = "__Secure-nts_mfa";

    /// <summary>The second step of the login.</summary>
    private const string LoginMfaPath = "/api/v1/auth/login/mfa";

    /// <summary>A well-formed six-digit code no log line may ever contain.</summary>
    private const string DistinctiveCode = "135790";

    /// <summary>A well-formed Crockford recovery code no log line may ever contain.</summary>
    private const string DistinctiveRecoveryCode = "ABCDE12345";

    /// <summary>PostgreSQL's text rendering of <c>used_at IS NOT NULL</c> for a spent challenge.</summary>
    private const string Consumed = "true";

    /// <summary>The same for a challenge still live.</summary>
    private const string Live = "false";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Login_OwnerWithMfa_ReturnsMfaRequiredWithPreAuthCookie()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(
            factory.Services, role: PlatformRole.Owner, twoFactorEnabled: true);

        var challenge = await StartLoginAsync(factory.AnonymousClient(), user, userAgent: null, Ct);

        // The token is the 32-byte base64url secret of BD4 and travels in the body alone.
        challenge.MfaToken.Length.ShouldBe(43, "the mfaToken is 32 random bytes base64url (BD4)");

        var raw = await challenge.Response.Content.ReadAsStringAsync(Ct);
        raw.ShouldNotContain("accessToken", Case.Sensitive, "the password step issues no session token");

        // The cookie's flags, as the browser sees them (BD4): the same ones as the refresh cookie, and a
        // Max-Age equal to the challenge's remaining lifetime (Auth:Mfa:ChallengeMinutes = 5).
        var setCookie = challenge.SetCookie;
        var flags = setCookie.ToLowerInvariant();
        flags.ShouldContain("httponly");
        flags.ShouldContain("secure");
        flags.ShouldContain("samesite=strict");
        flags.ShouldContain("path=/api/v1/auth");
        flags.ShouldContain("max-age=300");
        challenge.Cookie.ShouldStartWith(MfaCookieName + "=");

        // No refresh cookie and no family: the login is not complete until the second step.
        AuthTestFlow.SetCookie(challenge.Response).ShouldBeNull("the MFA step sets no refresh cookie");
        (await AuthTestFlow.RowsAsync(Database, user.Id)).ShouldBeEmpty();

        // Only the hashes are stored; the two plaintexts exist in the response and the cookie alone.
        var stored = await MfaTestFlow.ScalarAsync(
            Database,
            $"SELECT token_hash FROM auth_challenges WHERE user_id = '{user.Id}'");
        stored.ShouldBe(AuthTestFlow.Hash(challenge.MfaToken), "only SHA-256 hex of the token is stored");

        var plaintext = await MfaTestFlow.ScalarAsync(
            Database,
            $"SELECT count(*)::text FROM auth_challenges WHERE token_hash = '{challenge.MfaToken}' "
            + $"OR preauth_cookie_hash = '{CookieValue(challenge.Cookie)}'");
        plaintext.ShouldBe("0", "BD16: neither plaintext secret may ever reach the row");
    }

    [Fact]
    public async Task LoginMfa_CorrectTotp_IssuesTokens_AndConsumesChallenge()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services, role: PlatformRole.Admin);
        var enrolment = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        // The confirmation consumed the current step, so the login uses a later one — the real verifier's
        // compare-and-set (`users.last_totp_step`) refuses a code it has already accepted (BD4).
        clock.Advance(TimeSpan.FromSeconds(2 * MfaTestFlow.StepSeconds));
        var challenge = await StartLoginAsync(factory.AnonymousClient(), user, userAgent: null, Ct);

        var code = MfaTestFlow.Code(enrolment.Secret, clock.UtcNow);
        var response = await CompleteAsync(
            factory.AnonymousClient(), challenge, code: code, recoveryCode: null, userAgent: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await JsonAsync(response, Ct);
        body.GetProperty("accessToken").GetString().ShouldNotBeNullOrWhiteSpace();
        body.GetProperty("role").GetString().ShouldBe("admin");
        Amr(body).ShouldBe(["otp"], "the TOTP step is the otp method (BD3)");

        // The session is real: the refresh cookie is set, the pre-auth cookie is cleared, and a family exists.
        var refresh = AuthTestFlow.SetCookie(response);
        refresh.ShouldNotBeNull("a completed login sets the refresh cookie");
        refresh!.ToLowerInvariant().ShouldContain("httponly");
        SetCookieValues(response, MfaCookieName).ShouldHaveSingleItem(
            "the spent challenge's cookie is cleared, so the client cannot resend it");

        // The TOTP step opens a new family and ends none — only the recovery-code path revokes (BD4) — so
        // the enrolment's own session stays live beside the new one.
        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);
        var before = rows.Count;
        rows.Count(row => row.RevokedAt is null).ShouldBe(2, "the new family is live beside the enrolment's");

        (await ChallengeStateAsync(user.Id)).ShouldBe($"0:{Consumed}", "the challenge is consumed");

        // Replay: the same mfaToken cannot open a second session.
        var replay = await CompleteAsync(
            factory.AnonymousClient(), challenge, code: code, recoveryCode: null, userAgent: null, Ct);
        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(replay)).ShouldBe(ErrorCodes.MfaTokenInvalid);
        (await AuthTestFlow.RowsAsync(Database, user.Id)).Count.ShouldBe(before, "the replay opened no family");
    }

    [Fact]
    public async Task LoginMfa_WrongCode_FiveTimes_RevokesChallenge()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var verifier = new FakeTotpVerifier(_ => false);
        factory.WithServices(services => Replace(services, verifier));
        var user = await AuthTestFlow.CreateUserAsync(
            factory.Services, role: PlatformRole.Admin, twoFactorEnabled: true);

        var challenge = await StartLoginAsync(factory.AnonymousClient(), user, userAgent: null, Ct);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var wrong = await CompleteAsync(
                factory.AnonymousClient(), challenge, code: DistinctiveCode, recoveryCode: null, userAgent: null, Ct);

            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await AuthTestFlow.ProblemCodeAsync(wrong)).ShouldBe(
                ErrorCodes.InvalidMfaCode, $"attempt {attempt} is answered invalid_mfa_code (BD4)");
        }

        // The fifth failure killed the challenge in the same statement that counted it (BD4).
        (await ChallengeStateAsync(user.Id)).ShouldBe($"5:{Consumed}");

        // The next call is the one that answers mfa_token_invalid, not invalid_mfa_code (BD4).
        var sixth = await CompleteAsync(
            factory.AnonymousClient(), challenge, code: DistinctiveCode, recoveryCode: null, userAgent: null, Ct);
        sixth.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(sixth)).ShouldBe(ErrorCodes.MfaTokenInvalid);

        // Every refusal left its committed audit row behind, the sixth one alone charging nothing (WT29).
        var failed = await MfaTestFlow.ScalarAsync(
            Database,
            $"SELECT count(*)::text FROM audit_logs WHERE action = '{SecurityAuditActions.MfaFailed}' "
            + $"AND subject_user_id = '{user.Id}'");
        failed.ShouldBe("5", "each charged attempt wrote its account.mfa_failed row");
    }

    [Fact]
    public async Task LoginMfa_IpChanged_IsAuditedNotRejected()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var verifier = new FakeTotpVerifier(_ => true);
        factory.WithServices(services => Replace(services, verifier));
        var user = await AuthTestFlow.CreateUserAsync(
            factory.Services, role: PlatformRole.Admin, twoFactorEnabled: true);

        // BD4: a different address between the two steps is normal (Wi-Fi → mobile) and costs no code.
        using var loginClient = factory.PeerClient("198.51.100.7");
        var challenge = await StartLoginAsync(loginClient, user, userAgent: null, Ct);

        using var completeClient = factory.PeerClient("203.0.113.9");
        var response = await CompleteAsync(
            completeClient, challenge, code: DistinctiveCode, recoveryCode: null, userAgent: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the address change is audited, never rejected (BD4)");

        var count = await MfaTestFlow.ScalarAsync(
            Database,
            $"SELECT count(*)::text FROM audit_logs WHERE action = '{SecurityAuditActions.MfaIpChanged}' "
            + $"AND subject_user_id = '{user.Id}'");
        count.ShouldBe("1", "one internal audit row records the change");

        var data = await MfaTestFlow.ScalarAsync(
            Database,
            $"SELECT data::text FROM audit_logs WHERE action = '{SecurityAuditActions.MfaIpChanged}' "
            + $"AND subject_user_id = '{user.Id}'");
        data.ShouldNotBeNull();
        data!.ShouldContain("previousIpHash");
        data.ShouldContain("currentIpHash");
        data.ShouldNotContain("198.51.100.7", Case.Sensitive, "the raw address never reaches the audit payload (BD16)");
        data.ShouldNotContain("203.0.113.9", Case.Sensitive, "the raw address never reaches the audit payload (BD16)");
    }

    [Fact]
    public async Task LoginMfa_UserAgentChanged_ReturnsMfaTokenInvalid()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var verifier = new FakeTotpVerifier(_ => true);
        factory.WithServices(services => Replace(services, verifier));
        var user = await AuthTestFlow.CreateUserAsync(
            factory.Services, role: PlatformRole.Admin, twoFactorEnabled: true);

        var challenge = await StartLoginAsync(factory.AnonymousClient(), user, "nts-tests/1.0", Ct);

        var response = await CompleteAsync(
            factory.AnonymousClient(), challenge, code: DistinctiveCode, recoveryCode: null,
            userAgent: "nts-tests/2.0", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.MfaTokenInvalid);

        // The binding check runs before the code is even verified, so nothing was charged.
        (await ChallengeStateAsync(user.Id)).ShouldBe($"0:{Live}");
    }

    [Fact]
    public async Task LoginMfa_MissingPreAuthCookie_ReturnsMfaTokenInvalid()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var verifier = new FakeTotpVerifier(_ => true);
        factory.WithServices(services => Replace(services, verifier));
        var user = await AuthTestFlow.CreateUserAsync(
            factory.Services, role: PlatformRole.Admin, twoFactorEnabled: true);

        var challenge = await StartLoginAsync(factory.AnonymousClient(), user, userAgent: null, Ct);

        // The body alone is useless without the browser's cookie (BD4).
        var response = await CompleteAsync(
            factory.AnonymousClient(), challenge, code: DistinctiveCode, recoveryCode: null,
            cookie: null, userAgent: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.MfaTokenInvalid);
        (await ChallengeStateAsync(user.Id)).ShouldBe($"0:{Live}");
    }

    [Fact]
    public async Task LoginMfa_RecoveryCode_RedeemsOnce_AndRevokesOtherFamilies()
    {
        var clock = new FakeClock();
        var alerter = new RecordingAlerter();
        using var factory = new ApiFactory(Database, clock)
            .WithServices(services =>
            {
                services.RemoveAll<ISecurityAlerter>();
                services.AddSingleton<ISecurityAlerter>(alerter);
            });
        var user = await AuthTestFlow.CreateUserAsync(factory.Services, role: PlatformRole.Admin);
        var enrolment = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        // The enrolment's own family is the live session the break-glass sign-in must end (BD4).
        var live = (await AuthTestFlow.RowsAsync(Database, user.Id)).Single();

        var recoveryCode = enrolment.RecoveryCodes[0];
        var challenge = await StartLoginAsync(factory.AnonymousClient(), user, userAgent: null, Ct);
        var response = await CompleteAsync(
            factory.AnonymousClient(), challenge, code: null, recoveryCode: recoveryCode, userAgent: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await JsonAsync(response, Ct);
        Amr(body).ShouldBe(["rc"], "the recovery-code step is the rc method (BD3)");
        AuthTestFlow.SetCookie(response).ShouldNotBeNull("a completed login sets the refresh cookie");

        var rows = await AuthTestFlow.RowsAsync(Database, user.Id);
        rows.Single(row => row.FamilyId == live.FamilyId).RevokedReason.ShouldBe(
            RevocationReason.MfaChange, "every other family is revoked (BD4)");
        rows.Count(row => row.RevokedAt is null).ShouldBe(1, "only the new sign-in's family survives");

        // The code is single-use.
        (await RemainingRecoveryCodesAsync(factory, user.Id)).ShouldBe(9);

        var auditMethod = await MfaTestFlow.ScalarAsync(
            Database,
            $"SELECT data->>'method' FROM audit_logs WHERE action = '{SecurityAuditActions.LoginSucceeded}' "
            + $"AND subject_user_id = '{user.Id}'");
        auditMethod.ShouldBe("rc", "the audit row carries { method }");

        alerter.Events.ShouldContain(
            evt => evt.Kind == SecurityEventKinds.RecoveryCodeUsed && evt.SubjectUserId == user.Id,
            "a recovery-code sign-in of an owner or admin is alerted (D9)");

        // Second use of the same code fails on a fresh challenge.
        var second = await StartLoginAsync(factory.AnonymousClient(), user, userAgent: null, Ct);
        var reuse = await CompleteAsync(
            factory.AnonymousClient(), second, code: null, recoveryCode: recoveryCode, userAgent: null, Ct);

        reuse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(reuse)).ShouldBe(ErrorCodes.InvalidMfaCode);
    }

    [Fact]
    public async Task LoginMfa_ExpiredChallenge_ReturnsMfaTokenInvalid()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var verifier = new FakeTotpVerifier(_ => true);
        factory.WithServices(services => Replace(services, verifier));
        var user = await AuthTestFlow.CreateUserAsync(
            factory.Services, role: PlatformRole.Admin, twoFactorEnabled: true);

        var challenge = await StartLoginAsync(factory.AnonymousClient(), user, userAgent: null, Ct);

        // BD4: the challenge lives five minutes; one minute past it is dead.
        clock.Advance(TimeSpan.FromMinutes(6));

        var response = await CompleteAsync(
            factory.AnonymousClient(), challenge, code: DistinctiveCode, recoveryCode: null, userAgent: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.MfaTokenInvalid);
        (await ChallengeStateAsync(user.Id)).ShouldBe($"0:{Live}", "an expired challenge charges nothing");
    }

    [Fact]
    public async Task LoginMfa_BothCodeAndRecoveryCode_Returns400ValidationFailed()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var verifier = new FakeTotpVerifier(_ => true);
        factory.WithServices(services => Replace(services, verifier));
        var user = await AuthTestFlow.CreateUserAsync(
            factory.Services, role: PlatformRole.Admin, twoFactorEnabled: true);

        var challenge = await StartLoginAsync(factory.AnonymousClient(), user, userAgent: null, Ct);

        // The validator keeps the two fields mutually exclusive, so both surfaces cannot be guessed at once.
        var response = await CompleteAsync(
            factory.AnonymousClient(), challenge, code: DistinctiveCode,
            recoveryCode: DistinctiveRecoveryCode, userAgent: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.ValidationFailed);
        (await ChallengeStateAsync(user.Id)).ShouldBe($"0:{Live}", "a malformed body costs no attempt");
    }
    [Fact]
    public async Task LoginMfa_WrongPreAuthCookie_ReturnsMfaTokenInvalid()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var verifier = new FakeTotpVerifier(_ => true);
        factory.WithServices(services => Replace(services, verifier));
        var user = await AuthTestFlow.CreateUserAsync(
            factory.Services, role: PlatformRole.Admin, twoFactorEnabled: true);

        var challenge = await StartLoginAsync(factory.AnonymousClient(), user, userAgent: null, Ct);

        // The body's mfaToken is right and only the cookie is forged: the fixed-time compare of the two
        // hashes is what must refuse it — the missing-cookie test never reaches that statement (BD4).
        var response = await CompleteAsync(
            factory.AnonymousClient(), challenge, code: DistinctiveCode, recoveryCode: null,
            cookie: MfaCookieName + "=" + new string('x', 43), userAgent: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.MfaTokenInvalid);
        verifier.Codes.ShouldBeEmpty("the code is not even verified when the binding fails");
        (await ChallengeStateAsync(user.Id)).ShouldBe($"0:{Live}", "a forged cookie charges no attempt");
    }

    [Fact]
    public async Task LoginMfa_UnknownToken_AnswersTheSameAsAConsumedOne()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        factory.WithServices(services => Replace(services, new FakeTotpVerifier(_ => true)));
        var user = await AuthTestFlow.CreateUserAsync(
            factory.Services, role: PlatformRole.Admin, twoFactorEnabled: true);

        var challenge = await StartLoginAsync(factory.AnonymousClient(), user, userAgent: null, Ct);

        (await CompleteAsync(
                factory.AnonymousClient(), challenge, code: DistinctiveCode, recoveryCode: null, userAgent: null, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // A token that names no row at all must be indistinguishable from a consumed one (BD4): the same
        // status, the same code and the same sentence — an attacker learns nothing about which half died.
        var replay = await CompleteAsync(
            factory.AnonymousClient(), challenge, code: DistinctiveCode, recoveryCode: null, userAgent: null, Ct);
        var unknown = await CompleteAsync(
            factory.AnonymousClient(),
            challenge with { MfaToken = "z" + challenge.MfaToken[1..] },
            code: DistinctiveCode, recoveryCode: null, userAgent: null, Ct);

        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var replayBody = await JsonAsync(replay, Ct);
        var unknownBody = await JsonAsync(unknown, Ct);
        unknownBody.GetProperty("code").GetString().ShouldBe(
            replayBody.GetProperty("code").GetString());
        unknownBody.GetProperty("code").GetString().ShouldBe(ErrorCodes.MfaTokenInvalid);
        unknownBody.GetProperty("title").GetString().ShouldBe(
            replayBody.GetProperty("title").GetString(),
            "an invented token and a spent token get the same wording");
    }

    [Fact]
    public async Task LoginMfa_NeitherCode_Returns400ValidationFailed()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var verifier = new FakeTotpVerifier(_ => true);
        factory.WithServices(services => Replace(services, verifier));
        var user = await AuthTestFlow.CreateUserAsync(
            factory.Services, role: PlatformRole.Admin, twoFactorEnabled: true);

        var challenge = await StartLoginAsync(factory.AnonymousClient(), user, userAgent: null, Ct);

        // The mutual-exclusion rule runs both ways: an empty body is a malformed request, not a wrong code.
        var response = await CompleteAsync(
            factory.AnonymousClient(), challenge, code: null, recoveryCode: null, userAgent: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.ValidationFailed);
        verifier.Codes.ShouldBeEmpty();
        (await ChallengeStateAsync(user.Id)).ShouldBe($"0:{Live}", "a malformed body costs no attempt");
    }

    [Fact]
    public async Task LoginMfa_AtExactExpiry_ReturnsMfaTokenInvalid()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var verifier = new FakeTotpVerifier(_ => true);
        factory.WithServices(services => Replace(services, verifier));
        var user = await AuthTestFlow.CreateUserAsync(
            factory.Services, role: PlatformRole.Admin, twoFactorEnabled: true);

        var challenge = await StartLoginAsync(factory.AnonymousClient(), user, userAgent: null, Ct);

        // BD4: the challenge dies at expires_at (the compare is <=), not one request later.
        clock.Advance(TimeSpan.FromMinutes(5));

        var response = await CompleteAsync(
            factory.AnonymousClient(), challenge, code: DistinctiveCode, recoveryCode: null, userAgent: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.MfaTokenInvalid);
        verifier.Codes.ShouldBeEmpty();
        (await ChallengeStateAsync(user.Id)).ShouldBe($"0:{Live}", "an expired challenge charges nothing");
    }

    [Fact]
    public async Task LoginMfa_JustInsideExpiry_StillVerifiesTheCode()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var verifier = new FakeTotpVerifier(_ => false);
        factory.WithServices(services => Replace(services, verifier));
        var user = await AuthTestFlow.CreateUserAsync(
            factory.Services, role: PlatformRole.Admin, twoFactorEnabled: true);

        var challenge = await StartLoginAsync(factory.AnonymousClient(), user, userAgent: null, Ct);

        // The other side of the boundary: one second inside the lifetime the challenge is still live, so
        // a wrong code is charged as invalid_mfa_code — the expiry check must not bite early.
        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));

        var response = await CompleteAsync(
            factory.AnonymousClient(), challenge, code: DistinctiveCode, recoveryCode: null, userAgent: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.InvalidMfaCode);
        verifier.Codes.ShouldHaveSingleItem("the live challenge still reaches the verifier");
        (await ChallengeStateAsync(user.Id)).ShouldBe($"1:{Live}");
    }

    [Fact]
    public async Task LoginMfa_SupersededToken_IsInvalid_AndTheLiveOneStillCompletes()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var verifier = new FakeTotpVerifier(_ => true);
        factory.WithServices(services => Replace(services, verifier));
        var user = await AuthTestFlow.CreateUserAsync(
            factory.Services, role: PlatformRole.Admin, twoFactorEnabled: true);

        // A second password step supersedes the first challenge (BD4): its token and cookie are dead even
        // though nothing was ever presented with them.
        var first = await StartLoginAsync(factory.AnonymousClient(), user, userAgent: null, Ct);
        var second = await StartLoginAsync(factory.AnonymousClient(), user, userAgent: null, Ct);
        first.MfaToken.ShouldNotBe(second.MfaToken);

        var dead = await CompleteAsync(
            factory.AnonymousClient(), first, code: DistinctiveCode, recoveryCode: null, userAgent: null, Ct);
        dead.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(dead)).ShouldBe(ErrorCodes.MfaTokenInvalid);

        // The supersede must kill only the older row: the newest challenge still completes.
        var live = await CompleteAsync(
            factory.AnonymousClient(), second, code: DistinctiveCode, recoveryCode: null, userAgent: null, Ct);
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        Amr(await JsonAsync(live, Ct)).ShouldBe(["otp"]);
    }

    [Fact]
    public async Task LoginMfa_WhenTheUserIsGone_ReturnsMfaTokenInvalid()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var verifier = new FakeTotpVerifier(_ => true);
        factory.WithServices(services => Replace(services, verifier));
        var user = await AuthTestFlow.CreateUserAsync(
            factory.Services, role: PlatformRole.Admin, twoFactorEnabled: true);

        var challenge = await StartLoginAsync(factory.AnonymousClient(), user, userAgent: null, Ct);

        // Failure handling: the account deleted between the two steps takes its challenge rows with it
        // (FK ON DELETE CASCADE), and the completion answers the same 401 as any dead token.
        await Database.ExecuteAsync($"DELETE FROM users WHERE id = '{user.Id}'", Ct);
        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM auth_challenges WHERE user_id = '{user.Id}'"))
            .ShouldBe("0", "the cascade removed the challenge with the account");

        var response = await CompleteAsync(
            factory.AnonymousClient(), challenge, code: DistinctiveCode, recoveryCode: null, userAgent: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.MfaTokenInvalid);
        verifier.Codes.ShouldBeEmpty();
    }


    [Fact]
    public async Task LoginMfa_FirstFailureByAdmin_RaisesSecurityAlert()
    {
        var clock = new FakeClock();
        var alerter = new RecordingAlerter();
        using var factory = new ApiFactory(Database, clock)
            .WithServices(services =>
            {
                Replace(services, new FakeTotpVerifier(_ => false));
                services.RemoveAll<ISecurityAlerter>();
                services.AddSingleton<ISecurityAlerter>(alerter);
            });
        var user = await AuthTestFlow.CreateUserAsync(
            factory.Services, role: PlatformRole.Admin, twoFactorEnabled: true);

        var challenge = await StartLoginAsync(factory.AnonymousClient(), user, userAgent: null, Ct);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var wrong = await CompleteAsync(
                factory.AnonymousClient(), challenge, code: DistinctiveCode, recoveryCode: null, userAgent: null, Ct);

            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // D9: only the first wrong second factor of an owner or admin is alerted.
        alerter.Events.Count(evt => evt.Kind == SecurityEventKinds.MfaLoginFailed).ShouldBe(1);
        alerter.Events.ShouldContain(
            evt => evt.Kind == SecurityEventKinds.MfaLoginFailed && evt.SubjectUserId == user.Id);
    }

    [Fact]
    public async Task TotpGuessing_TenWrongCodesAcrossChallenges_Returns429RateLimited()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock)
            .WithSetting(
                $"{RateLimitingOptions.SectionName}:{nameof(RateLimitingOptions.Enabled)}",
                "false")
            .WithServices(services => Replace(services, new FakeTotpVerifier(_ => false)));
        var user = await AuthTestFlow.CreateUserAsync(
            factory.Services, role: PlatformRole.Admin, twoFactorEnabled: true);

        // Five wrong codes kill the first challenge — the window has counted five.
        var first = await StartLoginAsync(factory.AnonymousClient(), user, userAgent: null, Ct);
        await ChargeAsync(factory, first, 5);
        (await ChallengeStateAsync(user.Id)).ShouldBe($"5:{Consumed}");

        // Four more on a fresh challenge, so the window stands at nine (WT35 trips at ten).
        var second = await StartLoginAsync(factory.AnonymousClient(), user, userAgent: null, Ct);
        await ChargeAsync(factory, second, 4);
        (await LiveAttemptsAsync(user.Id)).ShouldBe("4");

        // The tenth wrong code is a re-authentication attempt; it is the one that trips the window.
        var familyId = await MfaTestFlow.SeedFamilyAsync(Database, user, clock.UtcNow, reauthenticatedAt: clock.UtcNow);
        using var session = factory.ClientFor(user.Id, PlatformRole.Admin, sessionId: familyId);
        var reauth = await session.PostAsJsonAsync(
            MfaTestFlow.ReauthPath,
            new { password = AuthTestFlow.Password, code = DistinctiveCode },
            Ct);
        reauth.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(reauth)).ShouldBe(ErrorCodes.InvalidMfaCode);

        // The live challenge now answers 429 before verifying anything, with WT35's Retry-After.
        var blocked = await CompleteAsync(
            factory.AnonymousClient(), second, code: DistinctiveCode, recoveryCode: null, userAgent: null, Ct);
        blocked.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await AuthTestFlow.ProblemCodeAsync(blocked)).ShouldBe(ErrorCodes.RateLimited);
        MfaTestFlow.RetryAfter(blocked).ShouldBe("900", "WT35 fixes Retry-After at 900 seconds");
        (await LiveAttemptsAsync(user.Id)).ShouldBe("4", "a blocked code is never charged");

        // The password step answers the same 429, because the window gates the whole sign-in.
        var (login, _) = await AuthTestFlow.LoginAsync(
            factory.AnonymousClient(), user.Email!, AuthTestFlow.Password, Ct);
        login.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await AuthTestFlow.ProblemCodeAsync(login)).ShouldBe(ErrorCodes.RateLimited);
        MfaTestFlow.RetryAfter(login).ShouldBe("900");

        var locked = await MfaTestFlow.ScalarAsync(
            Database,
            $"SELECT count(*)::text FROM audit_logs WHERE action = '{SecurityAuditActions.MfaCodeGuessingLocked}' "
            + $"AND subject_user_id = '{user.Id}'");
        locked.ShouldBe("1", "the whole tripped window leaves exactly one account_security row");
    }

    [Fact]
    public async Task LoginMfa_LogsCarryNoSecrets()
    {
        var clock = new FakeClock();
        var sink = new CapturingLoggerProvider();
        using var factory = new ApiFactory(Database, clock)
            .WithLoggerProvider(sink)
            .WithServices(services => Replace(services, new FakeTotpVerifier(code => code == DistinctiveCode)));
        var user = await AuthTestFlow.CreateUserAsync(
            factory.Services, role: PlatformRole.Admin, twoFactorEnabled: true);

        // A successful TOTP step and a failed recovery-code step: both secrets travel the full path.
        var success = await StartLoginAsync(factory.AnonymousClient(), user, userAgent: null, Ct);
        (await CompleteAsync(
            factory.AnonymousClient(), success, code: DistinctiveCode, recoveryCode: null, userAgent: null, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var recovery = await StartLoginAsync(factory.AnonymousClient(), user, userAgent: null, Ct);
        (await CompleteAsync(
            factory.AnonymousClient(), recovery, code: null,
            recoveryCode: DistinctiveRecoveryCode, userAgent: null, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var logs = sink.Text;

        // Non-vacuous: the requests really were logged through this sink.
        logs.ShouldContain("mediator.request");
        logs.ShouldNotContain(success.MfaToken, Case.Sensitive, "the mfaToken must never be logged (BD16)");
        logs.ShouldNotContain(recovery.MfaToken, Case.Sensitive, "the mfaToken must never be logged (BD16)");
        logs.ShouldNotContain(
            CookieValue(success.Cookie), Case.Sensitive, "the pre-auth cookie must never be logged (BD16)");
        logs.ShouldNotContain(DistinctiveCode, Case.Sensitive, "a TOTP code must never be logged (BD16)");
        logs.ShouldNotContain(
            DistinctiveRecoveryCode, Case.Sensitive, "a recovery code must never be logged (BD16)");

        // The audit payload is the other sink: no token and no code may reach `audit_logs.data` either.
        var leaked = await MfaTestFlow.ScalarAsync(
            Database,
            $"SELECT count(*)::text FROM audit_logs WHERE data::text LIKE '%{success.MfaToken}%' "
            + $"OR data::text LIKE '%{recovery.MfaToken}%' "
            + $"OR data::text LIKE '%{DistinctiveRecoveryCode}%'");
        leaked.ShouldBe("0", "no challenge token or recovery code reaches an audit payload (BD16)");
    }

    /// <summary>The <c>amr</c> array of a completed login body.</summary>
    private static string[] Amr(JsonElement body) =>
        [.. body.GetProperty("amr").EnumerateArray().Select(element => element.GetString()!)];

    /// <summary>Every <c>Set-Cookie</c> header of <paramref name="name"/>, in order.</summary>
    private static IReadOnlyList<string> SetCookieValues(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? [.. values.Where(value => value.StartsWith(name + "=", StringComparison.Ordinal))]
            : [];

    /// <summary>The value of a <c>name=value</c> cookie pair.</summary>
    private static string CookieValue(string cookiePair) =>
        cookiePair[(cookiePair.IndexOf('=', StringComparison.Ordinal) + 1)..];

    /// <summary>The body of a response as JSON.</summary>
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, CancellationToken ct) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));

    /// <summary>
    /// The account's challenge row rendered as <c>attempts:used</c>. Only for a point where exactly one
    /// row exists (the login supersedes the previous live row, so a second login leaves two).
    /// </summary>
    private async Task<string?> ChallengeStateAsync(Guid userId) =>
        await MfaTestFlow.ScalarAsync(
            Database,
            $"SELECT attempts::text || ':' || (used_at IS NOT NULL)::text FROM auth_challenges "
            + $"WHERE user_id = '{userId}'");

    /// <summary>The attempts of the one challenge still live, or null when none is (BD4: at most one).</summary>
    private async Task<string?> LiveAttemptsAsync(Guid userId) =>
        await MfaTestFlow.ScalarAsync(
            Database,
            $"SELECT attempts::text FROM auth_challenges WHERE user_id = '{userId}' AND used_at IS NULL");

    /// <summary>The recovery codes the user still holds, through the port.</summary>
    private static async Task<int> RemainingRecoveryCodesAsync(ApiFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var codes = scope.ServiceProvider.GetRequiredService<IRecoveryCodeService>();

        return await codes.CountRemainingAsync(userId, Ct);
    }

    /// <summary>Replaces the host's TOTP verifier with the hand-written one (09 README D13).</summary>
    private static void Replace(IServiceCollection services, ITotpVerifier verifier)
    {
        services.RemoveAll<ITotpVerifier>();
        services.AddSingleton(verifier);
    }

    /// <summary>
    /// Posts the password step and reads the challenge document out of the 401: the body's
    /// <c>mfaToken</c> and the <c>__Secure-nts_mfa</c> cookie pair.
    /// </summary>
    private static async Task<Challenge> StartLoginAsync(
        HttpClient client,
        AppUser user,
        string? userAgent,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AuthTestFlow.LoginPath)
        {
            Content = JsonContent.Create(new { email = user.Email!, password = AuthTestFlow.Password }),
        };

        if (userAgent is not null)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        var response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the password step alone issues no session");
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.MfaRequired);

        var body = await JsonAsync(response, ct);
        var mfaToken = body.GetProperty("mfaToken").GetString();
        mfaToken.ShouldNotBeNullOrWhiteSpace();

        var setCookie = SetCookieValues(response, MfaCookieName).ShouldHaveSingleItem();

        // The request header carries only `name=value`; everything after the first ';' is cookie metadata.
        var pair = setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)];

        return new Challenge(
            response,
            mfaToken!,
            pair,
            setCookie);
    }

    /// <summary>Posts the second step with the challenge's token and cookie, and exactly one code.</summary>
    private static async Task<HttpResponseMessage> CompleteAsync(
        HttpClient client,
        Challenge challenge,
        string? code,
        string? recoveryCode,
        string? userAgent,
        CancellationToken ct) =>
        await CompleteAsync(client, challenge, code, recoveryCode, challenge.Cookie, userAgent, ct);

    /// <inheritdoc cref="CompleteAsync(HttpClient, Challenge, string?, string?, string?, CancellationToken)" />
    private static async Task<HttpResponseMessage> CompleteAsync(
        HttpClient client,
        Challenge challenge,
        string? code,
        string? recoveryCode,
        string? cookie,
        string? userAgent,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, LoginMfaPath)
        {
            Content = JsonContent.Create(new { mfaToken = challenge.MfaToken, code, recoveryCode }),
        };

        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        if (userAgent is not null)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        return await client.SendAsync(request, ct);
    }

    /// <summary>Sends <paramref name="attempts"/> wrong codes and asserts each is invalid_mfa_code.</summary>
    private static async Task ChargeAsync(ApiFactory factory, Challenge challenge, int attempts)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var wrong = await CompleteAsync(
                factory.AnonymousClient(), challenge, code: DistinctiveCode, recoveryCode: null, userAgent: null, Ct);

            (await AuthTestFlow.ProblemCodeAsync(wrong)).ShouldBe(ErrorCodes.InvalidMfaCode);
        }
    }

    /// <summary>What the password step handed back: the response and the challenge's two secrets.</summary>
    private sealed record Challenge(
        HttpResponseMessage Response,
        string MfaToken,
        string Cookie,
        string SetCookie);

    /// <summary>A hand-written <see cref="ITotpVerifier"/>; no mocking library (09 README D13).</summary>
    private sealed class FakeTotpVerifier(Func<string, bool> accept) : ITotpVerifier
    {
        public List<string> Codes { get; } = [];

        public Task<ErrorOr<long>> VerifyAsync(Guid userId, string code, TotpSlot slot, CancellationToken ct)
        {
            Codes.Add(code);

            return accept(code)
                ? Task.FromResult<ErrorOr<long>>(1L)
                : Task.FromResult<ErrorOr<long>>(
                    Error.Unauthorized(ErrorCodes.InvalidMfaCode, "The code is not accepted."));
        }
    }

    /// <summary>A hand-written <see cref="ISecurityAlerter"/> that records what it was told.</summary>
    private sealed class RecordingAlerter : ISecurityAlerter
    {
        public List<SecurityEvent> Events { get; } = [];

        public Task AlertAsync(SecurityEvent evt, CancellationToken ct)
        {
            Events.Add(evt);

            return Task.CompletedTask;
        }
    }

    /// <summary>Keeps every rendered line, for the BD16 redaction check.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];
        private readonly Lock _gate = new();

        internal string Text
        {
            get
            {
                lock (_gate)
                {
                    return string.Join('\n', _messages);
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private void Capture(string rendered)
        {
            lock (_gate)
            {
                _messages.Add(rendered);
            }
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                owner.Capture(formatter(state, exception));
            }
        }
    }
}
