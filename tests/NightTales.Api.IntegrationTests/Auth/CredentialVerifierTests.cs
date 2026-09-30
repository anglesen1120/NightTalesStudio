using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Common.Errors;
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
/// <c>ICredentialVerifier</c> against a real PostgreSQL 17 (09.3.1-T1 Tests table): an unknown e-mail
/// and a wrong password are indistinguishable — same code, same description, and the unknown path runs
/// the dummy-hash verification instead of returning early (BD4's timing rule).
/// </summary>
public sealed class CredentialVerifierTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Password = "Abcdefghijkl";

    [Fact]
    public async Task UnknownEmail_UsesTheDummyHash_AndReturnsInvalidCredentials()
    {
        var ct = TestContext.Current.CancellationToken;

        using var factory = new ApiFactory(Database, new FakeClock());
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        // The real hasher, wrapped in a counter: what makes the unknown-address path equal-cost is that
        // it verifies against the dummy hash, and that is observable without a timer (T3 C4).
        var hasher = new CountingPasswordHasher(scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>());
        var verifier = new IdentityCredentialVerifier(users, hasher);

        var email = $"verifier-{Guid.CreateVersion7():N}@nighttales.test";
        (await users.CreateAsync(new AppUser { UserName = email, Email = email, DisplayName = "verifier" }, Password))
            .Succeeded.ShouldBeTrue();

        hasher.Hashes.ShouldBe(1, "the placeholder is hashed once, at construction (BD4)");

        var correct = await verifier.VerifyAsync(email, Password, ct);
        correct.IsError.ShouldBeFalse();
        correct.Value.Email.ShouldBe(email);

        var wrongPassword = await verifier.VerifyAsync(email, "not-the-password-1", ct);
        wrongPassword.IsError.ShouldBeTrue();
        wrongPassword.FirstError.Code.ShouldBe(ErrorCodes.InvalidCredentials);

        hasher.Verifications.ShouldBe(0, "a known address verifies through UserManager's own hasher");

        var unknownEmail = await verifier.VerifyAsync("nobody-at-all@nighttales.test", "not-the-password-1", ct);

        unknownEmail.IsError.ShouldBeTrue();
        unknownEmail.FirstError.Code.ShouldBe(ErrorCodes.InvalidCredentials);
        unknownEmail.FirstError.Description.ShouldBe(
            wrongPassword.FirstError.Description,
            "an unknown address must be indistinguishable from a wrong password");
        hasher.Verifications.ShouldBe(
            1,
            "the unknown-email path verifies the submitted password against the dummy hash instead of returning early");
    }

    [Fact]
    public async Task VerifyPasswordAsync_ReturnsFalseWithoutAnError()
    {
        var ct = TestContext.Current.CancellationToken;

        using var factory = new ApiFactory(Database, new FakeClock());
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var verifier = scope.ServiceProvider.GetRequiredService<ICredentialVerifier>();

        var email = $"reauth-{Guid.CreateVersion7():N}@nighttales.test";
        var user = new AppUser { UserName = email, Email = email, DisplayName = "reauth" };
        (await users.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();

        (await verifier.VerifyPasswordAsync(user, Password, ct)).ShouldBeTrue();
        (await verifier.VerifyPasswordAsync(user, "not-the-password-1", ct)).ShouldBeFalse();
    }

    /// <summary>
    /// The real <see cref="IPasswordHasher{TUser}"/> with counters in front of it, so a test can assert
    /// that hashing work happened without timing it (no mocking library, 09 README D13).
    /// </summary>
    private sealed class CountingPasswordHasher(IPasswordHasher<AppUser> inner) : IPasswordHasher<AppUser>
    {
        /// <summary>How many hashes were computed.</summary>
        internal int Hashes { get; private set; }

        /// <summary>How many submitted passwords were verified.</summary>
        internal int Verifications { get; private set; }

        public string HashPassword(AppUser user, string password)
        {
            Hashes++;

            return inner.HashPassword(user, password);
        }

        public PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
        {
            Verifications++;

            return inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }
}
