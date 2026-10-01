using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Api.RateLimiting;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.RateLimiting;

/// <summary>
/// The start-up contracts of 09.3.5-T1's two option types: the shipped numbers are BD14's (and 09 W3
/// CR-7's fourth policy), a limit the limiter could not enforce refuses the boot, and so does a trust
/// list that cannot be parsed.
/// </summary>
/// <remarks>
/// A malformed configuration must fail the boot rather than run with a silently wrong policy — the same
/// rule 09.9.4-T1 applies to <c>App:PublicOrigin</c> (<c>StartupValidationTests</c>).
/// </remarks>
public sealed class RateLimitingOptionsTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The section of the limits; the keys are read as configuration, exactly as a deployment sets them.</summary>
    private const string Section = RateLimitingOptions.SectionName;

    /// <summary>The section of the forwarded-header trust.</summary>
    private const string TrustSection = "Http:ForwardedHeaders";

    [Fact]
    public void ShippedDefaults_MatchBd14()
    {
        using var factory = new ApiFactory(Database, new FakeClock());

        var options = factory.Services.GetRequiredService<IOptions<RateLimitingOptions>>().Value;

        options.Enabled.ShouldBeTrue("the limiter is on unless an incident switch turns it off");
        options.RetryAfterSeconds.ShouldBe(60);

        options.Auth.PermitLimit.ShouldBe(10);
        options.Auth.WindowSeconds.ShouldBe(60);

        options.Paid.PermitLimit.ShouldBe(30);
        options.Paid.WindowSeconds.ShouldBe(60);
        options.Paid.SegmentsPerWindow.ShouldBe(6);

        options.Hub.PermitLimit.ShouldBe(10);
        options.Hub.WindowSeconds.ShouldBe(60);

        // 09 W3 CR-7's fourth policy: the anonymous write routes, 60 requests per minute per address.
        options.Anon.PermitLimit.ShouldBe(60);
        options.Anon.WindowSeconds.ShouldBe(60);

        options.Ipv6PrefixLength.ShouldBe(RateLimitingOptions.DefaultIpv6PrefixLength);

        RateLimitPolicies.All.ShouldBe(
            new[] { RateLimitPolicies.Auth, RateLimitPolicies.Paid, RateLimitPolicies.Hub, RateLimitPolicies.Anon });
    }

    [Fact]
    public void NonPositivePermitLimit_FailsStartup()
    {
        var exception = Should.Throw<OptionsValidationException>(() => StartHost(($"{Section}:Auth:PermitLimit", "0")));

        exception.Message.ShouldBe(RateLimitingOptions.NonPositivePermitLimitFailure);
    }

    [Fact]
    public void RetryAfterShorterThanTheWindow_FailsStartup()
    {
        // The advertised wait is the policy window, never a computed remainder (09.3.5-T1 design), so a
        // Retry-After below a window would tell a refused client to come back while it is still refused.
        var exception = Should.Throw<OptionsValidationException>(
            () => StartHost(($"{Section}:RetryAfterSeconds", "5")));

        exception.Message.ShouldBe(RateLimitingOptions.RetryAfterShorterThanWindowFailure);
    }

    [Fact]
    public void UnparsableTrustNetwork_FailsStartup()
    {
        var exception = Should.Throw<OptionsValidationException>(
            () => StartHost(($"{TrustSection}:KnownNetworks", "not-a-cidr")));

        exception.Message.ShouldContain(
            $"{TrustSection}:KnownNetworks",
            Case.Sensitive,
            "a typo in the proxy list must name the list it is in, never boot with the proxy untrusted");
    }

    [Fact]
    public void Ipv6PrefixLengthOutOfRange_FailsStartup()
    {
        // The upper bound of the prefix: /129 is not a mask, so it must refuse the boot rather than fall
        // back to the whole address (SEC-0935-02 keys an address partition on its IPv6 prefix).
        var exception = Should.Throw<OptionsValidationException>(
            () => StartHost(($"{Section}:Ipv6PrefixLength", "129")));

        exception.Message.ShouldBe(RateLimitingOptions.InvalidIpv6PrefixLengthFailure);
    }

    /// <summary>Starts the real host with <paramref name="settings"/> on top of the shipped configuration.</summary>
    private void StartHost(params (string Key, string Value)[] settings)
    {
        using var factory = new ApiFactory(Database, new FakeClock());

        foreach (var (key, value) in settings)
        {
            factory.WithSetting(key, value);
        }

        factory.CreateClient();
    }
}
