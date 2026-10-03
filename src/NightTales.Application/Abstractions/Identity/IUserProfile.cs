using NightTales.Domain.Common;

namespace NightTales.Application.Abstractions.Identity;

/// <summary>
/// The account fields <c>GET /me</c> and <c>PATCH /me</c> show (09.3.4-T1): everything the UI needs and
/// nothing that is a secret — no security stamp, password hash, TOTP step or normalised e-mail.
/// </summary>
/// <param name="Id">The account id.</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="Email">The sign-in address (read-only here).</param>
/// <param name="UiLocale">The UI locale (<c>users.ui_locale</c>), carried as the shared <see cref="Language"/> (09 README D16).</param>
/// <param name="Role">The platform role (read-only here).</param>
/// <param name="MfaEnabled"><c>users.two_factor_enabled</c> (read-only here; 09.10.4-T1 owns the transitions).</param>
/// <param name="LastLoginAt"><c>users.last_login_at</c>.</param>
public sealed record UserProfile(
    Guid Id,
    string DisplayName,
    string Email,
    Language UiLocale,
    PlatformRole Role,
    bool MfaEnabled,
    DateTimeOffset? LastLoginAt);

/// <summary>The outcome of a profile write: the new profile and what the write changed.</summary>
/// <param name="Profile">The profile after the change.</param>
/// <param name="PreviousUiLocale">The locale before the write.</param>
/// <param name="DisplayNameChanged">Whether the display name differs from the stored one.</param>
public sealed record UserProfileUpdate(UserProfile Profile, Language PreviousUiLocale, bool DisplayNameChanged);

/// <summary>Reads one account's profile without tracking (09 README D8).</summary>
public interface IUserProfileReader
{
    /// <summary>The profile of <paramref name="userId"/>, or <see langword="null"/> when the account is missing or deleted.</summary>
    /// <param name="userId">The caller's id; there is never another user's id here.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    Task<UserProfile?> GetAsync(Guid userId, CancellationToken ct);
}

/// <summary>
/// Changes the writable profile fields on the tracked account inside the caller's transaction: it never
/// calls <c>SaveChangesAsync</c>, so <c>UnitOfWorkBehavior</c> commits the change with the audit row (09 README D5).
/// </summary>
public interface IUserProfileWriter
{
    /// <summary>Applies the non-null members; <see langword="null"/> when the account is missing or deleted.</summary>
    /// <param name="userId">The caller's id.</param>
    /// <param name="displayName">The new display name, or <see langword="null"/> to keep it.</param>
    /// <param name="uiLocale">The new locale, or <see langword="null"/> to keep it.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    Task<UserProfileUpdate?> UpdateAsync(Guid userId, string? displayName, Language? uiLocale, CancellationToken ct);
}
