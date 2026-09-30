using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Infrastructure.Identity;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// The BD2 Identity options of 09.3.1-T1 as the real host registers them (Tests table): the password
/// policy, the lockout setting and the unique-email rule, the last against a real PostgreSQL 17 so
/// Identity's validator and the unique <c>normalized_email</c> index are both exercised.
/// </summary>
public sealed class IdentityOptionsTests(TestDatabase database) : IntegrationTest(database)
{
    private const string ValidPassword = "Abcdefghijkl";

    [Fact]
    public async Task PasswordPolicy_TwelveChars_NoCompositionRules()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var tooShort = await users.CreateAsync(NewUser("short"), "Abcdefghijk");
        tooShort.Succeeded.ShouldBeFalse();
        tooShort.Errors.Select(error => error.Code).ShouldContain("PasswordTooShort");

        var accepted = await users.CreateAsync(NewUser("twelve"), ValidPassword);
        accepted.Succeeded.ShouldBeTrue(
            $"BD2 turns every composition rule off: {string.Join("; ", accepted.Errors.Select(error => $"{error.Code}:{error.Description}"))}");
    }

    [Fact]
    public void Lockout_AllowedForNewUsers_IsFalse()
    {
        using var factory = new ApiFactory(Database, new FakeClock());

        var options = factory.Services.GetRequiredService<IOptions<IdentityOptions>>().Value;

        options.Lockout.AllowedForNewUsers.ShouldBeFalse("BD2: lockout is 09.3.3's counters, not Identity's");
        options.User.RequireUniqueEmail.ShouldBeTrue();
        options.Password.RequiredLength.ShouldBe(12);
        options.Password.RequiredUniqueChars.ShouldBe(1);
        options.Password.RequireDigit.ShouldBeFalse();
        options.Password.RequireLowercase.ShouldBeFalse();
        options.Password.RequireUppercase.ShouldBeFalse();
        options.Password.RequireNonAlphanumeric.ShouldBeFalse();
        options.SignIn.RequireConfirmedEmail.ShouldBeFalse("09.3.2-T1 answers email_not_verified itself");
        options.Stores.ProtectPersonalData.ShouldBeFalse("09.10.4-T1 protects the TOTP secret directly");
    }

    [Fact]
    public async Task RequireUniqueEmail_RejectsSecondUserWithSameNormalizedEmail()
    {
        using var factory = new ApiFactory(Database, new FakeClock());
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var email = $"unique-{Guid.CreateVersion7():N}@nighttales.test";

        var first = await users.CreateAsync(
            new AppUser { UserName = email, Email = email, DisplayName = "first" },
            ValidPassword);
        first.Succeeded.ShouldBeTrue(string.Join("; ", first.Errors.Select(error => $"{error.Code}:{error.Description}")));

        // A different user name, the same address: only RequireUniqueEmail and the normalized_email
        // index can refuse this one.
        var second = await users.CreateAsync(
            new AppUser { UserName = $"other-{Guid.CreateVersion7():N}@nighttales.test", Email = email, DisplayName = "second" },
            ValidPassword);

        second.Succeeded.ShouldBeFalse();
        second.Errors.Select(error => error.Code).ShouldContain("DuplicateEmail");
    }

    private static AppUser NewUser(string tag)
    {
        var address = $"{tag}-{Guid.CreateVersion7():N}@nighttales.test";

        return new AppUser { UserName = address, Email = address, DisplayName = tag };
    }
}
