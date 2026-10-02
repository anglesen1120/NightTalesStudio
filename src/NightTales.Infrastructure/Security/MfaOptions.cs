using NightTales.Application.Abstractions.Security;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The MFA and re-authentication configuration of 09.10.4-T1: <c>Auth:Mfa:*</c> and
/// <c>Auth:Reauth:*</c> (09.10.4-T1 Outputs, BD4).
/// </summary>
/// <remarks>
/// <para>
/// Bound to the <c>Auth</c> section like <see cref="SessionOptions"/> and <see cref="JwtOptions"/>, and
/// validated at start-up by <c>SecurityServiceCollectionExtensions</c>: the re-auth window may be
/// shortened but never widened past BD4's five minutes, the attempt ceiling is BD4's five (the
/// <c>refresh_tokens.reauth_attempts</c> column's <c>CHECK</c> ends at 5), the issuer must be
/// non-empty and the step must be positive.
/// </para>
/// <para>
/// The step and the issuer are read by both the verifier and the <c>otpauth://</c> URI builder, so the
/// label an authenticator application shows and the codes the API accepts can never disagree.
/// </para>
/// </remarks>
public sealed class MfaOptions
{
    /// <summary>The configuration section both of this task's option groups bind to (BD3's <c>Auth:*</c>).</summary>
    public const string SectionName = JwtOptions.SectionName;

    /// <summary>The widest re-authentication window a deployment may configure (BD4: 5 minutes).</summary>
    public static readonly TimeSpan ReauthWindowCeiling = TimeSpan.FromMinutes(5);

    /// <summary>The number of wrong attempts that revoke a family (BD4: 5).</summary>
    public const int ReauthAttemptCeiling = 5;

    /// <summary>The authenticator defaults (BD4: <c>NightTales</c>, 30 s).</summary>
    public static readonly int DefaultStepSeconds = Rfc6238.DefaultStepSeconds;

    /// <summary>The <c>Auth:Mfa:*</c> members.</summary>
    public MfaSection Mfa { get; set; } = new();

    /// <summary>The <c>Auth:Reauth:*</c> members.</summary>
    public ReauthSection Reauth { get; set; } = new();

    /// <summary>The re-authentication window the routes use when they do not fix their own.</summary>
    public TimeSpan ReauthWindow => TimeSpan.FromMinutes(Reauth.WindowMinutes);

    /// <summary>The authenticator settings.</summary>
    public sealed class MfaSection
    {
        /// <summary>The <c>issuer</c> parameter and label prefix of the <c>otpauth://</c> URI (BD4).</summary>
        public string Issuer { get; set; } = "NightTales";

        /// <summary>The TOTP step in seconds (RFC 6238, BD4: 30).</summary>
        public int StepSeconds { get; set; } = DefaultStepSeconds;
    }

    /// <summary>The re-authentication settings.</summary>
    public sealed class ReauthSection
    {
        /// <summary>How long a re-authentication satisfies <see cref="IReauthGuard"/> (BD4: 5).</summary>
        public double WindowMinutes { get; set; } = 5;

        /// <summary>Wrong attempts that revoke the family (BD4: 5).</summary>
        public int MaxAttempts { get; set; } = ReauthAttemptCeiling;
    }
}
