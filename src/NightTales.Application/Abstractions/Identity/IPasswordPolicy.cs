using ErrorOr;

namespace NightTales.Application.Abstractions.Identity;

/// <summary>
/// The password rule of a registration or a reset beyond what Identity itself enforces (09 README
/// BD2): the BD2 length rule and the breached-password check.
/// </summary>
/// <remarks>
/// Identity's own validators (composition rules, length) still run inside
/// <c>UserManager.CreateAsync</c>; this port is the pre-check the register flow (09.3.1-T3) and the
/// reset flow (09.3.3-T1) call before touching the account, so a rejected password never creates a row.
/// </remarks>
public interface IPasswordPolicy
{
    /// <summary>
    /// Returns <see cref="Success"/> for an acceptable password; otherwise 400
    /// <c>validation_failed</c> (too short, with Identity's <c>PasswordTooShort</c> description) or 400
    /// <c>breached_password</c> (found in the configured breached-password source). A breached-password
    /// source that cannot answer fails closed with 503 <c>service_unavailable</c> in <c>range</c> mode.
    /// </summary>
    /// <param name="password">The candidate password; never logged.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<Success>> ValidateAsync(string password, CancellationToken ct);
}
