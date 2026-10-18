using NightTales.Domain.Publishing;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Publishing;

// The feature-flag namespace `NightTales.Domain.Platform` encloses this one, so the simple name `Platform`
// binds to it; an alias declared in this namespace body wins over that member (one declared in the
// compilation unit does not) and names the publishing enum the wire mapping belongs to.
using Platform = NightTales.Domain.Publishing.Platform;

/// <summary>
/// <see cref="PlatformExtensions"/> (06 README D3, 06.1.2-T1): the enum's lower-case wire spelling is the value
/// the API path segment, the <c>oauth_states.platform</c> column and its <c>CHECK</c> all share, so the three
/// cannot drift.
/// </summary>
public sealed class PlatformWireMappingTests
{
    [Fact]
    public void ToWire_IsTheLowerCaseValueTheColumnAndTheCheckUse()
    {
        Platform.YouTube.ToWire().ShouldBe("youtube");
    }

    [Fact]
    public void ToWire_RefusesAnUndeclaredValue()
    {
        // A cast the enum does not declare has no wire value: storing it would fail the CHECK anyway.
        Should.Throw<ArgumentOutOfRangeException>(() => ((Platform)99).ToWire());
    }

    [Theory]
    [InlineData("youtube")]
    [InlineData("YouTube")]
    [InlineData("YOUTUBE")]
    public void TryParse_AcceptsTheWireValueInAnyCase(string wire)
    {
        PlatformExtensions.TryParse(wire, out var platform).ShouldBeTrue();

        platform.ShouldBe(Platform.YouTube);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tiktok")] // 07's platform: the column's CHECK admits it, the enum does not declare it yet.
    [InlineData("you tube")]
    [InlineData("youtube ")]
    [InlineData("yt")]
    public void TryParse_RefusesAnythingElse(string? wire)
    {
        PlatformExtensions.TryParse(wire, out var platform).ShouldBeFalse();

        platform.ShouldBe(default);
    }

    [Fact]
    public void RoundTrip_KeepsEveryDeclaredPlatform()
    {
        foreach (var platform in Enum.GetValues<Platform>())
        {
            PlatformExtensions.TryParse(platform.ToWire(), out var parsed).ShouldBeTrue();
            parsed.ShouldBe(platform);
        }
    }
}
