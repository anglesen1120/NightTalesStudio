using ErrorOr;

namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// The authenticator secrets of one user (09.10.4-T1 Design, 09 README BD4): the active secret, the
/// unverified enrolment secret and their lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// The secrets live in Identity's <c>user_tokens</c> (<c>LoginProvider = "[AspNetUserStore]"</c>, names
/// <c>totp_secret</c> / <c>totp_secret_pending</c>) — the shape Identity's own store uses, so no new
/// table and no migration is needed — and every value is Data-Protection-protected under the purpose
/// <c>identity.totp.v1</c> ([RULED — README §B10 WT43]).
/// </para>
/// <para>
/// Every method is called inside the caller's transaction (09 README D5), so a promoted secret and the
/// <c>two_factor_enabled</c> flag it belongs to commit or roll back together.
/// </para>
/// </remarks>
public interface ITotpSecretStore
{
    /// <summary>The active secret as base32, decrypted, or <c>null</c> when the user has none.</summary>
    /// <param name="userId">The user to read.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<string?>> GetActiveSecretAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// The unverified enrolment secret as base32, decrypted, or <c>null</c> when there is none.
    /// </summary>
    /// <remarks>
    /// The Design's <c>ITotpSecretStore</c> sketch lists only the active read, but
    /// <see cref="ITotpVerifier.VerifyAsync"/> takes a <see cref="TotpSlot"/>: resolving the pending
    /// slot is this store's job, because it is the only place that knows how the value is protected.
    /// </remarks>
    /// <param name="userId">The user to read.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<string?>> GetPendingSecretAsync(Guid userId, CancellationToken ct);

    /// <summary>Stores <paramref name="base32Secret"/> as the user's unverified enrolment secret.</summary>
    /// <param name="userId">The user enrolling.</param>
    /// <param name="base32Secret">The freshly generated base32 secret; never logged.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<Success>> SetPendingSecretAsync(Guid userId, string base32Secret, CancellationToken ct);

    /// <summary>
    /// Promotes the pending secret to the active one and removes the pending row (BD4: the enrolment is
    /// confirmed).
    /// </summary>
    /// <param name="userId">The user confirming the enrolment.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<Success>> PromotePendingSecretAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Removes every authenticator artefact of the user: both secrets and the recovery-code row.
    /// </summary>
    /// <param name="userId">The user disabling MFA.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<Success>> DeleteAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Whether the user has MFA enrolled — <c>users.two_factor_enabled</c>, the flag the routes gate on
    /// (BD4).
    /// </summary>
    /// <param name="userId">The user to read.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<bool>> IsEnrolledAsync(Guid userId, CancellationToken ct);
}
