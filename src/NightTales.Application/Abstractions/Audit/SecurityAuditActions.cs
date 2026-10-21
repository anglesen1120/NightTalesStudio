namespace NightTales.Application.Abstractions.Audit;

/// <summary>
/// The <c>audit_logs.action</c> values of 09.10.4-T1's security rows (09 README BD15).
/// </summary>
/// <remarks>
/// <para>
/// The four MFA keys are the ones the task's endpoint table fixes; the last two are this task's own
/// [INFERENCE] additions, because the Design names the rows without freezing their <c>messageKey</c>.
/// They are the values the S-phase activity allow-list (09 README C3.4) and the owner e-mail templates
/// key on, so they live in one place instead of being re-spelled by each caller and each test.
/// </para>
/// <para>
/// Every one of them is a <c>Category.AccountSecurity</c> row.
/// </para>
/// </remarks>
public static class SecurityAuditActions
{
    /// <summary>An enrolment started: a fresh pending secret was generated.</summary>
    public const string MfaSetupStarted = "mfa.setup_started";

    /// <summary>The enrolment was confirmed: the secret is active and recovery codes were issued.</summary>
    public const string MfaEnrolled = "mfa.enrolled";

    /// <summary>All recovery codes were replaced.</summary>
    public const string MfaRecoveryCodesRegenerated = "mfa.recovery_codes_regenerated";

    /// <summary>MFA was disabled: the secrets and codes were removed and the other families revoked.</summary>
    public const string MfaDisabled = "mfa.disabled";

    /// <summary>
    /// The per-user wrong-code window tripped (WT35): the single row that marks the 429 the user now
    /// receives. [INFERENCE key]
    /// </summary>
    public const string MfaCodeGuessingLocked = "mfa.code_guessing_locked";

    /// <summary>
    /// A login completed (09.3.2-T1/T3). 09.3.2-T3 writes it on the MFA step, where the session is
    /// actually issued; its <c>data</c> carries <c>{ method }</c>.
    /// </summary>
    /// <remarks>
    /// The name is the frozen §C3.4 activity-feed <c>messageKey</c> (<c>account.login_succeeded</c>), not
    /// the plan's shorthand <c>login.succeeded</c>: the feed projects this vocabulary and a released key
    /// cannot be renamed (09 README §C3.4), and 09.3.2-T1's <c>LoginCommand</c> already names it.
    /// </remarks>
    public const string LoginSucceeded = "account.login_succeeded";

    /// <summary>
    /// A wrong authenticator or recovery code on the MFA login step (09.3.2-T3 Design); its <c>data</c>
    /// carries <c>{ attempts }</c>.
    /// </summary>
    /// <remarks>
    /// The frozen §C3.4 key is <c>account.mfa_failed</c> — the same key 09.3.3-T3 reuses at its own
    /// threshold — rather than the plan's shorthand <c>login.mfa_failed</c>.
    /// </remarks>
    public const string MfaFailed = "account.mfa_failed";

    /// <summary>
    /// The caller's address changed between the password step and the MFA step (BD4): audited only,
    /// never rejected, and the <c>data</c> carries the two address hashes, never an address
    /// (09.3.2-T3 Design).
    /// </summary>
    /// <remarks>
    /// The row is <c>visibility = internal</c>: it is an operational note about a legitimate sign-in, not
    /// something the owner's activity feed shows — which is also why the name needs no §C3.4 row or an
    /// <c>activity.*</c> translation (09 README §C3.4 rule).
    /// </remarks>
    public const string MfaIpChanged = "mfa.ip_changed";

    /// <summary>A re-authentication failed (BD4). [INFERENCE key]</summary>
    public const string ReauthFailed = "account.reauth_failed";

    /// <summary>
    /// A reset link was requested for a live account (09.3.3-T1; BD15/C3.4's frozen
    /// <c>account.password_reset_requested</c>). Written only when the account exists and is active, so an
    /// unknown address leaves no row that would differentiate it from a known one.
    /// </summary>
    public const string PasswordResetRequested = "account.password_reset_requested";

    /// <summary>
    /// A password was changed through a mailed reset link (09.3.3-T1; BD15/C3.4's frozen
    /// <c>account.password_reset</c>).
    /// </summary>
    public const string PasswordReset = "account.password_reset";
}
