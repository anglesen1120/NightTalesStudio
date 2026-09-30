using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The access-token configuration of 09.3.2-T1 Design: the ES256 issuer/audience and the signing key, plus
/// the per-role lifetime the algorithm of BD3 uses.
/// </summary>
/// <remarks>
/// <para>
/// One options type per consumer, both bound to the <c>Auth</c> section: the issuer and the Api's bearer
/// scheme read <c>Auth:Jwt:*</c> and <c>Auth:Access:*</c> from here, while the refresh store and the
/// cookie layer read <c>Auth:Session:*</c> and <c>Auth:Refresh:*</c> from <see cref="SessionOptions"/>.
/// </para>
/// <para>
/// The signing key is a <em>secret</em> (<c>nts/&lt;env&gt;/api/JWT_SIGNING_KEY</c>): it is never
/// committed, and this type only carries it into the options pipeline. Its absence or malformation fails
/// start-up (<see cref="JwtSigningKey.TryCreate"/>), never a fallback algorithm (W2-O3).
/// </para>
/// </remarks>
public sealed class JwtOptions
{
    /// <summary>The configuration section both of this task's options types bind to.</summary>
    public const string SectionName = "Auth";

    /// <summary>The audience every platform token is issued for and validated against (BD3).</summary>
    public const string DefaultAudience = "nts-api";

    /// <summary>The five-minute lifetime of an owner/admin access token (BD3, WF12).</summary>
    public const int DefaultAdminTokenMinutes = 5;

    /// <summary>The fifteen-minute lifetime of a user access token (BD3).</summary>
    public const int DefaultUserTokenMinutes = 15;

    /// <summary>The validation tolerance of BD3; it never widens <c>expiresAt</c>.</summary>
    public const int DefaultClockSkewSeconds = 30;

    /// <summary>The <c>Auth:Jwt:*</c> members.</summary>
    public JwtSection Jwt { get; set; } = new();

    /// <summary>The <c>Auth:Access:*</c> members.</summary>
    public AccessSection Access { get; set; } = new();

    /// <summary>The token settings (issuer, audience, signing key).</summary>
    public sealed class JwtSection
    {
        /// <summary>
        /// The <c>iss</c> value. Left null in configuration: the composition root fills it with
        /// <c>nts-{environment}</c> when nothing is configured, which is BD3's documented default.
        /// </summary>
        public string? Issuer { get; set; }

        /// <summary>The <c>aud</c> value; <c>nts-api</c> unless a deployment says otherwise.</summary>
        public string Audience { get; set; } = DefaultAudience;

        /// <summary>
        /// The PEM of the ES256 private key, from the secret store. Never logged, never echoed in a
        /// validation message, never committed.
        /// </summary>
        public string? SigningKey { get; set; }
    }

    /// <summary>The lifetimes and the validation tolerance.</summary>
    public sealed class AccessSection
    {
        /// <summary>Minutes an owner/admin access token lives (BD3: 5).</summary>
        public int AdminTokenMinutes { get; set; } = DefaultAdminTokenMinutes;

        /// <summary>Minutes a user access token lives (BD3: 15).</summary>
        public int UserTokenMinutes { get; set; } = DefaultUserTokenMinutes;

        /// <summary>Seconds of allowed clock skew at validation (BD3: 30).</summary>
        public int ClockSkewSeconds { get; set; } = DefaultClockSkewSeconds;

        /// <summary>The lifetime BD3 gives <paramref name="role"/>.</summary>
        /// <param name="role">The role the token is issued for.</param>
        public TimeSpan LifetimeFor(PlatformRole role) => role is PlatformRole.Admin or PlatformRole.Owner
            ? TimeSpan.FromMinutes(AdminTokenMinutes)
            : TimeSpan.FromMinutes(UserTokenMinutes);
    }
}
