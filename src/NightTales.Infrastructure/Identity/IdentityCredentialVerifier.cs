using ErrorOr;
using Microsoft.AspNetCore.Identity;
using NightTales.Application.Common.Errors;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Identity;

/// <summary>
/// Verifies credentials through <see cref="UserManager{TUser}"/> (09 README BD2): e-mail lookup plus
/// <c>CheckPasswordAsync</c>, with every failure answered as 401 <c>invalid_credentials</c>.
/// </summary>
/// <remarks>
/// <para>
/// BD4's timing rule: an unknown e-mail must cost what a wrong password costs. The constructor hashes a
/// fixed, non-secret placeholder once, and the unknown-address path verifies the submitted password
/// against that dummy hash and discards the result. No hash literal is stored, so a hasher upgrade (or
/// a different iteration count) cannot invalidate the placeholder.
/// </para>
/// <para>
/// <c>FindByEmailAsync</c> looks up Identity's normalized e-mail (<c>RequireUniqueEmail</c> +
/// the unique <c>normalized_email</c> index, no <c>citext</c>), so the lookup is the same one
/// registration's uniqueness check uses.
/// </para>
/// <para>
/// Scoped, because <see cref="UserManager{TUser}"/> and <see cref="IPasswordHasher{TUser}"/> are. The
/// dummy hash is therefore computed once per scope; that is the price of using the registered hasher
/// instead of a hard-coded literal.
/// </para>
/// </remarks>
/// <param name="users">The Identity user manager of the scope.</param>
/// <param name="passwordHasher">The registered hasher, used for both the dummy hash and nothing else here.</param>
public sealed class IdentityCredentialVerifier(
    UserManager<AppUser> users,
    IPasswordHasher<AppUser> passwordHasher) : ICredentialVerifier
{
    /// <summary>
    /// The placeholder hashed once at construction. It is not a secret and never a valid credential:
    /// the hasher only needs <em>some</em> input so the unknown-address path does the same work as a
    /// real verification.
    /// </summary>
    private const string DummyPassword = "nts-dummy-password-for-constant-time-lookup";

    private static readonly AppUser DummyUser = new()
    {
        Id = Guid.Empty,
        UserName = "dummy",
        DisplayName = "dummy",
    };

    private readonly string _dummyHash = passwordHasher.HashPassword(DummyUser, DummyPassword);

    /// <inheritdoc />
    public async Task<ErrorOr<AppUser>> VerifyAsync(string email, string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(password);

        var user = await users.FindByEmailAsync(email).ConfigureAwait(false);
        if (user is null)
        {
            // Same work as the known-user path: BD4 requires the two to be indistinguishable.
            _ = passwordHasher.VerifyHashedPassword(DummyUser, _dummyHash, password);
            return InvalidCredentials();
        }

        return await users.CheckPasswordAsync(user, password).ConfigureAwait(false)
            ? user
            : InvalidCredentials();
    }

    /// <inheritdoc />
    public Task<bool> VerifyPasswordAsync(AppUser user, string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(password);

        return users.CheckPasswordAsync(user, password);
    }

    private static Error InvalidCredentials() =>
        Error.Unauthorized(ErrorCodes.InvalidCredentials, "invalid_credentials");
}
