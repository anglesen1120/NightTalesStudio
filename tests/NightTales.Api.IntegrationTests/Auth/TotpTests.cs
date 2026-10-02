using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Infrastructure.Security;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// The TOTP contract of 09.10.4-T1 (Tests table) against a real PostgreSQL 17: the replay-proof
/// compare-and-set, the pending slot of the enrolment, and the per-user wrong-code window of WT35.
/// </summary>
/// <remarks>
/// The algorithm itself is unit-tested (<c>Rfc6238TotpVerifierTests</c>); these tests are about the state
/// around it — <c>users.last_totp_step</c>, which secret a slot resolves to, and the 429 that ten wrong
/// codes produce.
/// </remarks>
public sealed class TotpTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Totp_SameCodeTwice_SecondIsReplay()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var enrolment = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        // The enrolment consumed the step it was confirmed in, so the replay is observed one step later:
        // a fresh code is accepted once and refused the second time.
        clock.Advance(TimeSpan.FromSeconds(MfaTestFlow.StepSeconds));
        var code = MfaTestFlow.Code(enrolment.Secret, clock.UtcNow);
        var step = Rfc6238.StepAt(clock.UtcNow, MfaTestFlow.StepSeconds);

        var first = await VerifyAsync(factory, user.Id, code);
        first.IsError.ShouldBeFalse("the code belongs to the current step");

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT last_totp_step::text FROM users WHERE id = '{user.Id}'"))
            .ShouldBe(step.ToString(CultureInfo.InvariantCulture), "the accepted step is recorded");

        var second = await VerifyAsync(factory, user.Id, code);

        second.IsError.ShouldBeTrue("the same code cannot be presented twice (BD4, W6)");
        second.FirstError.Code.ShouldBe(ErrorCodes.InvalidMfaCode);

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT last_totp_step::text FROM users WHERE id = '{user.Id}'"))
            .ShouldBe(
                step.ToString(CultureInfo.InvariantCulture),
                "the refused replay changes nothing: the recorded step is still the first success");
    }

    [Fact]
    public async Task Totp_VerifyUsesPendingSecret_DuringEnrolment()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var first = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        clock.Advance(TimeSpan.FromSeconds(MfaTestFlow.StepSeconds));

        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database,
            user,
            clock.UtcNow,
            reauthenticatedAt: clock.UtcNow);

        using var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        // A second setup replaces the pending secret; the active one stays where it is.
        var setup = await client.PostAsync(MfaTestFlow.SetupPath, content: null, Ct);
        setup.StatusCode.ShouldBe(HttpStatusCode.OK);

        var pending = await MfaTestFlow.SetupSecretAsync(setup, Ct);
        pending.ShouldNotBe(first.Secret);

        var activeCode = await client.PostAsJsonAsync(
            MfaTestFlow.VerifyPath,
            new { code = MfaTestFlow.Code(first.Secret, clock.UtcNow) },
            Ct);

        activeCode.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized,
            "the enrolment verifies against the pending secret, not the active one (WT31)");
        (await AuthTestFlow.ProblemCodeAsync(activeCode)).ShouldBe(ErrorCodes.InvalidMfaCode);

        var pendingCode = await client.PostAsJsonAsync(
            MfaTestFlow.VerifyPath,
            new { code = MfaTestFlow.Code(pending, clock.UtcNow) },
            Ct);

        pendingCode.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Totp_Guessing_TenWrongCodesAcrossChallenges_Returns429WithRetryAfter900()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var enrolment = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        clock.Advance(TimeSpan.FromMinutes(1));

        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database,
            user,
            clock.UtcNow,
            reauthenticatedAt: clock.UtcNow);

        using var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        // Five wrong codes on the enrolment-confirmation surface (a fresh pending secret, so the codes
        // really are compared).
        var setup = await client.PostAsync(MfaTestFlow.SetupPath, content: null, Ct);
        var pending = await MfaTestFlow.SetupSecretAsync(setup, Ct);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var wrong = await client.PostAsJsonAsync(
                MfaTestFlow.VerifyPath,
                new { code = MfaTestFlow.WrongCode(pending, clock.UtcNow) },
                Ct);

            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await AuthTestFlow.ProblemCodeAsync(wrong)).ShouldBe(ErrorCodes.InvalidMfaCode);
        }

        // Five more on the re-authentication surface: the tenth wrong code trips the window.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var wrong = await client.PostAsJsonAsync(
                MfaTestFlow.ReauthPath,
                new { password = MfaTestFlow.Password, code = MfaTestFlow.WrongCode(enrolment.Secret, clock.UtcNow) },
                Ct);

            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var login = await AuthTestFlow.LoginAsync(
            factory.AnonymousClient(),
            user.Email!,
            MfaTestFlow.Password,
            Ct);

        login.Response.StatusCode.ShouldBe(
            HttpStatusCode.TooManyRequests,
            "ten wrong codes inside fifteen minutes block the sign-in (WT35)");
        (await AuthTestFlow.ProblemCodeAsync(login.Response)).ShouldBe(ErrorCodes.RateLimited);
        MfaTestFlow.RetryAfter(login.Response).ShouldBe("900");
        login.Cookie.ShouldBeNull("a blocked login issues nothing");

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM audit_logs WHERE action = '{MfaTestFlow.GuessingLockedAction}' "
                + $"AND subject_user_id = '{user.Id}'"))
            .ShouldBe("1", "the ruling asks for exactly one account_security row, not one per request");

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM audit_logs WHERE action = '{MfaTestFlow.ReauthFailedAction}' "
                + $"AND subject_user_id = '{user.Id}'"))
            .ShouldBe("5", "each failed re-authentication committed its own row (WT29)");
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
}
