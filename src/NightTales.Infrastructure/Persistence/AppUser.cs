using Microsoft.AspNetCore.Identity;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Persistence;

/// <summary>
/// The Identity user of the platform (00 overview §6 <c>users</c>). The extra columns (display_name,
/// ui_locale, role, status, last_totp_step, owner_dispute_freeze_until, last_login_at,
/// email_verified_at) and their configuration are 09.2.2-T1's, mapped by
/// <see cref="ModelBuilderExtensions.ApplyIdentityModel"/> so both contexts share them.
/// </summary>
public sealed class AppUser : IdentityUser<Guid>, IAuditable
{
    /// <summary>The longest display name (plan §3: <c>display_name varchar(80)</c>).</summary>
    public const int DisplayNameMaxLength = 80;

    public string DisplayName { get; set; } = string.Empty;

    public UiLocale UiLocale { get; set; } = UiLocale.Vi;

    /// <summary>The authoritative platform role; the Identity role tables stay empty (plan §3).</summary>
    public PlatformRole Role { get; set; } = PlatformRole.User;

    public UserStatus Status { get; set; } = UserStatus.Active;

    public long? LastTotpStep { get; set; }

    public DateTimeOffset? OwnerDisputeFreezeUntil { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>Stamped by 09.3.1-T3's verify flow next to Identity's <c>email_confirmed</c>.</summary>
    public DateTimeOffset? EmailVerifiedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
