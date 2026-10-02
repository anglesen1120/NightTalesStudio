using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Security;
using NightTales.Domain.Common;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// The session limits of 09.3.2-T1 (Tests table): the BD3 defaults a role gets, and the start-up refusal of
/// a configuration that would widen the owner/admin policy.
/// </summary>
/// <remarks>
/// The two default assertions are pure, and the start-up ones boot the production composition root with a
/// refused value, so the guard is the host's real <c>ValidateOnStart</c> rather than a copy of it —
/// 09.3.1-T1's <c>IdentityOptionsStartupTests</c> is the precedent for mixing both levels here.
/// </remarks>
public sealed class SessionLimitsTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public void Owner_IsTwelveHoursAndThirtyMinutesIdle()
    {
        var owner = SessionLimits.For(PlatformRole.Owner);

        owner.Absolute.ShouldBe(TimeSpan.FromHours(12), "BD3: an owner family's ceiling is 12 hours");
        owner.Idle.ShouldBe(TimeSpan.FromMinutes(30), "BD3: the idle window is 30 minutes");

        SessionLimits.For(PlatformRole.Admin).ShouldBe(owner, "owner and admin share one session policy (BD3)");
    }

    [Fact]
    public void User_IsFourteenDaysNoIdle()
    {
        var user = SessionLimits.For(PlatformRole.User);

        user.Absolute.ShouldBe(TimeSpan.FromDays(14), "BD3: a user family lives 14 days");
        user.Idle.ShouldBeNull("a user family has no idle timeout, only its absolute ceiling");
    }

    [Fact]
    public void AdminIdleAboveThirtyMinutes_StartupFails()
    {
        // BD3 caps the idle window at 30 minutes; a host configured wider must not start at all.
        ShouldRefuseToStart(($"{SessionOptionsSection}:Session:OwnerIdleMinutes", "31"));
    }

    [Fact]
    public void OwnerAbsoluteAboveTwentyFourHours_StartupFails()
    {
        ShouldRefuseToStart(($"{SessionOptionsSection}:Session:OwnerAbsoluteHours", "24.5"));
    }

    /// <summary>
    /// The same rule read as 09.10.4-T1's acceptance check reads it: an owner/admin absolute value of 48
    /// hours cannot be configured. The key is <c>Session:OwnerAbsoluteHours</c> because BD3 gives the two
    /// roles one session policy — there is no separate admin key to widen.
    /// </summary>
    [Fact]
    public void AdminAbsoluteAboveTwentyFourHours_StartupFails()
    {
        ShouldRefuseToStart(($"{SessionOptionsSection}:Session:OwnerAbsoluteHours", "48"));
    }

    [Fact]
    public void CookieNameWithoutSecurePrefix_StartupFails()
    {
        // The prefix is what makes the browser itself enforce `Secure`; a deployment cannot quietly rename
        // the refresh cookie into something weaker (BD3).
        ShouldRefuseToStart(($"{SessionOptionsSection}:Refresh:CookieName", "nts_rt"));
    }

    /// <summary>The configuration section every auth option binds to (BD3's <c>Auth:*</c> keys).</summary>
    private const string SessionOptionsSection = "Auth";

    private void ShouldRefuseToStart(params (string Key, string Value)[] settings)
    {
        using var host = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder =>
            {
                foreach (var (key, value) in settings)
                {
                    builder.UseSetting(key, value);
                }
            });

        Should.Throw<OptionsValidationException>(() => host.CreateClient());
    }
}
