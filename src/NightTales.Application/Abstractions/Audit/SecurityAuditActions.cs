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
