namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// What a row of <c>user_action_tokens</c> authorizes (09 README BD3, WF11): the kind is stored next to
/// the hash and is part of every consume, so a token of one flow can never be spent on another.
/// </summary>
/// <remarks>
/// Declared by 09.3.3-T1, which owns the table and the handlers, and consumed at send time by
/// 09.2.6-T3's <c>PasswordResetTokenMinter</c> (09 README §B10 WT17): the reset token is created when the
/// message is actually rendered, never in the request path that queues it. The db CHECK of the table
/// carries the same closed set.
/// </remarks>
public enum UserActionKind
{
    /// <summary>A password reset, 60 minutes (BD3); consumed by <c>POST /auth/reset-password</c>.</summary>
    PasswordReset,

    /// <summary>An e-mail verification, 24 hours (BD3, S phase); consumed by 09.3.1-T3's verify route.</summary>
    EmailVerify,

    /// <summary>
    /// A publication veto cancel link, valid until the veto window ends or the upload starts — added by
    /// 06.2.2-T2 (06 CR-6), which mints and consumes it.
    /// </summary>
    PublicationCancel,
}
