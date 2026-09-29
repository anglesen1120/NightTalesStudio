using NightTales.Domain.Common;
using NightTales.TestKit.Api;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Harness;

/// <summary>
/// The role-name parse of the test scheme is a pure function, so this is a unit test: no server, no
/// <c>Category=Integration</c> trait (SEC 09.2.1-T2). The header plumbing is covered by
/// <see cref="ApiFactoryAuthTests"/>.
/// </summary>
public sealed class TestAuthHandlerGuardTests
{
    [Theory]
    [InlineData("user", PlatformRole.User)]
    [InlineData("USER", PlatformRole.User)]
    [InlineData("Admin", PlatformRole.Admin)]
    [InlineData("admin", PlatformRole.Admin)]
    [InlineData("owner", PlatformRole.Owner)]
    [InlineData("OWNER", PlatformRole.Owner)]
    public void TestAuthHandler_RoleHeader_AcceptsTheClosedNameSet(string raw, PlatformRole expected)
    {
        TestAuthHandler.TryParseRole(raw, out var role).ShouldBeTrue();
        role.ShouldBe(expected);
    }

    [Theory]
    [InlineData("2")] // Enum.TryParse reads this as PlatformRole.Owner.
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("user,owner")] // The shape a duplicated header takes once StringValues joins it.
    [InlineData("User, Owner")]
    [InlineData("supreme_overlord")]
    [InlineData("")]
    [InlineData(" user ")]
    [InlineData(null)]
    public void TestAuthHandler_RoleHeader_RejectsAnythingElse(string? raw)
    {
        TestAuthHandler.TryParseRole(raw, out var role).ShouldBeFalse();
        role.ShouldBe(default(PlatformRole));
    }

    [Fact]
    public void TestAuthHandler_RoleClaimValue_IsSnakeCase()
    {
        // The claim the handler emits stays lower-case, so a client can round-trip it (README D16).
        TestAuthHandler.RoleClaimValue(PlatformRole.Owner).ShouldBe("owner");
        TestAuthHandler.TryParseRole(
            TestAuthHandler.RoleClaimValue(PlatformRole.Owner),
            out var role).ShouldBeTrue();
        role.ShouldBe(PlatformRole.Owner);
    }
}
