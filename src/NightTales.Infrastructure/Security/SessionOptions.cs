using NightTales.Application.Abstractions.Security;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The session configuration of 09.3.2-T1 Design: the refresh cookie's name and the four
/// <c>Auth:Session:*</c> values that mirror <see cref="SessionLimits"/>.
/// </summary>
/// <remarks>
/// <para>
/// Bound to the <c>Auth</c> section like <see cref="JwtOptions"/>; the start-up validation of
/// <c>SecurityServiceCollectionExtensions</c> refuses a configuration that widens the owner/admin bounds
/// beyond BD3 (absolute ≤ 24 h, idle ≤ 30 min) and a cookie name that does not carry the
/// <c>__Secure-</c> prefix the wire contract relies on.
/// </para>
/// <para>
/// A role's effective limits are <see cref="LimitsFor"/>, never the raw fields: the accessors keep the
/// "owner and admin share a policy, users have only an absolute ceiling" rule in one place.
/// </para>
/// </remarks>
public sealed class SessionOptions
{
    /// <summary>The configuration section both of this task's options types bind to.</summary>
    public const string SectionName = JwtOptions.SectionName;

    /// <summary>The BD3 refresh cookie name (BD3: <c>__Secure-nts_rt</c>).</summary>
    public const string DefaultCookieName = "__Secure-nts_rt";

    /// <summary>The prefix a cookie name must carry to get a browser's prefix protection.</summary>
    public const string SecurePrefix = "__Secure-";

    /// <summary>The widest absolute lifetime an owner/admin family may be configured with (BD3).</summary>
    public static readonly TimeSpan OwnerAbsoluteCeiling = TimeSpan.FromHours(24);

    /// <summary>The widest idle window an owner/admin family may be configured with (BD3).</summary>
    public static readonly TimeSpan OwnerIdleCeiling = TimeSpan.FromMinutes(30);

    /// <summary>The <c>Auth:Session:*</c> members.</summary>
    public SessionSection Session { get; set; } = new();

    /// <summary>The <c>Auth:Refresh:*</c> members.</summary>
    public RefreshSection Refresh { get; set; } = new();

    /// <summary>The effective limits of <paramref name="role"/>.</summary>
    /// <param name="role">The role the family was opened for.</param>
    public SessionLimits LimitsFor(PlatformRole role) => role is PlatformRole.Owner or PlatformRole.Admin
        ? new SessionLimits(
            TimeSpan.FromHours(Session.OwnerAbsoluteHours),
            TimeSpan.FromMinutes(Session.OwnerIdleMinutes))
        : new SessionLimits(TimeSpan.FromDays(Session.UserAbsoluteDays), null);

    /// <summary>The session-lifetime values; the defaults are <see cref="SessionLimits.For"/>'s.</summary>
    public sealed class SessionSection
    {
        /// <summary>Absolute hours of an owner/admin family (BD3: 12).</summary>
        public double OwnerAbsoluteHours { get; set; } = 12;

        /// <summary>Idle minutes of an owner/admin family (BD3: 30).</summary>
        public double OwnerIdleMinutes { get; set; } = 30;

        /// <summary>Absolute days of a user family (BD3: 14; users have no idle timeout).</summary>
        public double UserAbsoluteDays { get; set; } = 14;
    }

    /// <summary>The refresh-cookie settings.</summary>
    public sealed class RefreshSection
    {
        /// <summary>The cookie name; must keep the <c>__Secure-</c> prefix.</summary>
        public string CookieName { get; set; } = DefaultCookieName;
    }
}
