using NightTales.Application.Abstractions.Security;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The lifetime of every <c>user_action_tokens</c> kind (09 README BD3, 09.3.3-T1 Design): the
/// <c>Auth:ActionToken:*</c> values, bound and validated at start-up.
/// </summary>
/// <remarks>
/// <para>
/// The kind fixes the lifetime, so the issue call carries no TTL: a password reset is 60 minutes and an
/// e-mail verification 24 hours, and a caller cannot widen either. The subject-scoped overload of
/// <c>IUserActionTokenService</c> is the exception — 06.2.2-T2's publication cancel link passes its own
/// explicit expiry, because its window ends with the veto.
/// </para>
/// <para>
/// The defaults are BD3's, so a host that omits the section behaves exactly as the contract says.
/// </para>
/// </remarks>
public sealed class UserActionTokenOptions
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string SectionName = "Auth:ActionToken";

    /// <summary>BD3's password-reset lifetime.</summary>
    public const int DefaultPasswordResetMinutes = 60;

    /// <summary>BD3's e-mail-verification lifetime (consumed by 09.3.1-T3).</summary>
    public const int DefaultEmailVerifyHours = 24;

    /// <summary>The <c>Auth:ActionToken:PasswordResetMinutes</c> value (BD3: 60).</summary>
    public int PasswordResetMinutes { get; set; } = DefaultPasswordResetMinutes;

    /// <summary>The <c>Auth:ActionToken:EmailVerifyHours</c> value (BD3: 24, S phase).</summary>
    public int EmailVerifyHours { get; set; } = DefaultEmailVerifyHours;

    /// <summary>The effective password-reset lifetime.</summary>
    public TimeSpan PasswordResetTtl => TimeSpan.FromMinutes(PasswordResetMinutes);

    /// <summary>The effective e-mail-verification lifetime.</summary>
    public TimeSpan EmailVerifyTtl => TimeSpan.FromHours(EmailVerifyHours);

    /// <summary>The lifetime of <paramref name="kind"/>.</summary>
    /// <param name="kind">The flow the token belongs to.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The kind has no configured lifetime — <see cref="UserActionKind.PublicationCancel"/> is
    /// subject-scoped and must use the explicit-expiry overload (06 CR-6).
    /// </exception>
    public TimeSpan TtlFor(UserActionKind kind) => kind switch
    {
        UserActionKind.PasswordReset => PasswordResetTtl,
        UserActionKind.EmailVerify => EmailVerifyTtl,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            $"'{kind}' has no configured lifetime; a subject-scoped token passes its expiry explicitly (06 CR-6)."),
    };
}
