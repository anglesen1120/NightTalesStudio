using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions.Security;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// The enrolment races of 09.10.4-T1: the pending secret is bound to the one that was verified, so a
/// concurrent setup cannot activate an unverified replacement and two concurrent confirmations cannot
/// both succeed.
/// </summary>
public sealed class MfaConcurrencyTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Verify_Concurrent_OnTheSamePendingSecret_OnlyOneConfirms()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        _ = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        clock.Advance(TimeSpan.FromMinutes(1));

        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database, user, clock.UtcNow, reauthenticatedAt: clock.UtcNow);
        using var first = factory.ClientFor(user.Id, user.Role, sessionId: familyId);
        using var second = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        var setup = await first.PostAsync(MfaTestFlow.SetupPath, content: null, Ct);
        setup.StatusCode.ShouldBe(HttpStatusCode.OK);
        var pending = await MfaTestFlow.SetupSecretAsync(setup, Ct);
        var code = MfaTestFlow.Code(pending, clock.UtcNow);

        var responses = await Task.WhenAll(
            first.PostAsJsonAsync(MfaTestFlow.VerifyPath, new { code }, Ct),
            second.PostAsJsonAsync(MfaTestFlow.VerifyPath, new { code }, Ct));

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(
            1,
            "the per-user lock lets exactly one confirmation of the same pending code win");
        responses.Count(response => response.StatusCode == HttpStatusCode.Unauthorized).ShouldBe(1);

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM user_tokens WHERE user_id = '{user.Id}' "
                + "AND name = 'totp_secret_pending'"))
            .ShouldBe("0", "the confirmed pending row is consumed exactly once");
    }

    [Fact]
    public async Task Setup_DuringVerification_DoesNotActivateTheUnverifiedSecret()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database, user, clock.UtcNow, reauthenticatedAt: clock.UtcNow);
        using var verifier = factory.ClientFor(user.Id, user.Role, sessionId: familyId);
        using var setter = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        var setup = await setter.PostAsync(MfaTestFlow.SetupPath, content: null, Ct);
        setup.StatusCode.ShouldBe(HttpStatusCode.OK);
        var verified = await MfaTestFlow.SetupSecretAsync(setup, Ct);

        // A confirmation of the issued secret races a second setup that replaces the pending payload.
        var confirmation = verifier.PostAsJsonAsync(
            MfaTestFlow.VerifyPath,
            new { code = MfaTestFlow.Code(verified, clock.UtcNow) },
            Ct);

        var replacementSetup = await setter.PostAsync(MfaTestFlow.SetupPath, content: null, Ct);
        replacementSetup.StatusCode.ShouldBe(HttpStatusCode.OK);
        var replacement = await MfaTestFlow.SetupSecretAsync(replacementSetup, Ct);

        var confirmationResponse = await confirmation;

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITotpSecretStore>();
        var active = await store.GetActiveSecretAsync(user.Id, Ct);
        active.IsError.ShouldBeFalse();

        var enabled = await MfaTestFlow.ScalarAsync(
            Database, $"SELECT two_factor_enabled::text FROM users WHERE id = '{user.Id}'");

        if (confirmationResponse.StatusCode == HttpStatusCode.OK)
        {
            active.Value.ShouldBe(verified, "the promoted secret is the one whose code was verified");
        }
        else
        {
            confirmationResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            enabled.ShouldBe("false", "a replacement secret that was never verified must not activate MFA");
            active.Value.ShouldNotBe(replacement, "the unverified replacement is never promoted");
        }
    }
}
