using ErrorOr;

namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// The single-use recovery codes of one user (09.10.4-T1 Design, 09 README BD4).
/// </summary>
/// <remarks>
/// <para>
/// Ten codes of ten Crockford base32 characters (50 bits each) are generated together, returned to the
/// caller exactly once and stored as their SHA-256 hashes in the one <c>recovery_codes</c> row of
/// Identity's <c>user_tokens</c>, joined by <c>;</c> — the shape Identity's own store uses, so the whole
/// set can be replaced atomically. Nothing is ever stored in plaintext (BD16).
/// </para>
/// <para>
/// Redemption locks the row (<c>SELECT … FOR UPDATE</c>), compares every stored hash with
/// <c>CryptographicOperations.FixedTimeEquals</c>, removes the matched hash and saves, so one code is
/// used at most once even when two requests redeem it at the same instant.
/// </para>
/// <para>
/// Every method is called inside the caller's transaction (09 README D5).
/// </para>
/// </remarks>
public interface IRecoveryCodeService
{
    /// <summary>
    /// Replaces every stored code with ten fresh ones and returns them, in plaintext, once.
    /// </summary>
    /// <param name="userId">The user whose codes are replaced.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<IReadOnlyList<string>>> RegenerateAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Redeems one code: the matched hash is removed and cannot be used again.
    /// </summary>
    /// <param name="userId">The user redeeming a code.</param>
    /// <param name="code">The submitted code; never logged.</param>
    /// <param name="ct">The request cancellation token.</param>
    /// <returns>Success, or 401 <c>invalid_mfa_code</c> when no stored code matches.</returns>
    Task<ErrorOr<Success>> RedeemAsync(Guid userId, string code, CancellationToken ct);

    /// <summary>How many codes the user still holds (0 when the row is absent).</summary>
    /// <param name="userId">The user to read.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<int> CountRemainingAsync(Guid userId, CancellationToken ct);
}
