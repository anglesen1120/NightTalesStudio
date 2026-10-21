using ErrorOr;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Common.Errors;

namespace NightTales.Infrastructure.Identity;

/// <summary>
/// The BD2 password rule (09.3.1-T1): the length check of the registered
/// <see cref="IdentityOptions"/> and the breached-password check. It adds nothing else — composition
/// rules stay off, exactly as <c>AddIdentityCore</c> is configured.
/// </summary>
/// <remarks>
/// <para>
/// The length comes from <see cref="IdentityOptions.Password"/> rather than a constant, so Identity's
/// own validators (which run inside <c>UserManager.CreateAsync</c>) and this pre-check cannot drift.
/// The description is Identity's own error code, <c>PasswordTooShort</c>, so the client sees the same
/// wording whether the pre-check or Identity rejected the password.
/// </para>
/// <para>
/// Identity's own composition rules are deliberately not re-implemented: with
/// <c>RequireDigit</c>/<c>RequireUppercase</c>/… all false, the only rule to mirror is the length.
/// </para>
/// <para>Registered as a singleton: it holds only options and the stateless checker.</para>
/// </remarks>
/// <param name="identityOptions">The registered Identity options (the BD2 password policy).</param>
/// <param name="breachedPasswords">The configured breached-password source.</param>
public sealed class IdentityPasswordPolicy(
    IOptions<IdentityOptions> identityOptions,
    IBreachedPasswordChecker breachedPasswords) : IPasswordPolicy
{
    /// <summary>
    /// The client-safe reason of a breached password; the code (<c>breached_password</c>) is what the
    /// body carries and what the SPA maps, this is the human-readable title.
    /// </summary>
    public const string BreachedDescription = "This password appears in a breached-password list; choose another one.";

    /// <inheritdoc />
    public async Task<ErrorOr<Success>> ValidateAsync(string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(password);

        var requiredLength = identityOptions.Value.Password.RequiredLength;
        if (password.Length < requiredLength)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "PasswordTooShort");
        }

        var breached = await breachedPasswords.IsBreachedAsync(password, ct).ConfigureAwait(false);
        if (breached.IsError)
        {
            // Range mode fails closed (503 service_unavailable): a possibly-breached password is never
            // accepted because the provider could not answer (09.3.1-T1 failure handling).
            return breached.Errors;
        }

        return breached.Value
            ? Error.Validation(ErrorCodes.BreachedPassword, BreachedDescription)
            : Result.Success;
    }
}
