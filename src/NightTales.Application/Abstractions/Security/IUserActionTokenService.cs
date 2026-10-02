using ErrorOr;

namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// The single-use action tokens of 09 README BD3 (WF11), table <c>user_action_tokens</c>: 32 random bytes
/// whose SHA-256 — never the token — is stored, at most one live token per <c>(user, kind)</c>.
/// </summary>
/// <remarks>
/// <para>
/// The port is declared here, with the consumer that needs it first (09 README BD1): 09.3.3-T1 owns the
/// table, the handlers and the implementation (<c>EfUserActionTokenService</c>), and 09.2.6-T3 consumes the
/// send-time member from <c>PasswordResetTokenMinter</c> inside <c>SendEmailJob</c> — the reset token is
/// created when the message is actually rendered, never in the request path that queues it (09 README §B10
/// WT17, WT30). Declaring the whole port here is what keeps the dependency from being circular: 09.3.3-T1
/// is listed as blocked by this task, so it can consume this file instead of adding it (09.2.6-T3 Notes).
/// </para>
/// <para>
/// The signatures are the ones BD3 and 09.3.3-T1 fix, including the request metadata of the request-path
/// call; a Worker job has no request and passes an empty <see cref="RequestInfo"/>.
/// </para>
/// </remarks>
public interface IUserActionTokenService
{
    /// <summary>
    /// Issues one token of <paramref name="kind"/> and returns its plaintext — the only time the plaintext
    /// exists — after invalidating the user's previous unused tokens of the same kind.
    /// </summary>
    /// <param name="userId">The user the token authorizes.</param>
    /// <param name="kind">The flow the token belongs to; a consume of another kind must fail.</param>
    /// <param name="info">The caller metadata of the request path; empty for the send-time call.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The base64url plaintext token, or an error.</returns>
    Task<ErrorOr<string>> IssueAsync(
        Guid userId,
        UserActionKind kind,
        RequestInfo info,
        CancellationToken cancellationToken);

    /// <summary>
    /// Issues one token bound to a subject with an explicit expiry, for the subject-scoped flows
    /// (06.2.2-T2's publication cancel link, 06 CR-6/YR8): invalidation is limited to
    /// <c>(user, kind, subject)</c> and <see cref="ConsumeAsync"/> answers the subject for the caller to
    /// compare.
    /// </summary>
    /// <param name="userId">The user the token authorizes.</param>
    /// <param name="kind">The flow the token belongs to.</param>
    /// <param name="subjectId">The row the token is about; it never travels in the token itself.</param>
    /// <param name="expiresAt">The explicit expiry, decided by the flow's owner.</param>
    /// <param name="info">The caller metadata of the request path; empty for a send-time call.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The base64url plaintext token, or an error.</returns>
    Task<ErrorOr<string>> IssueAsync(
        Guid userId,
        UserActionKind kind,
        Guid subjectId,
        DateTimeOffset expiresAt,
        RequestInfo info,
        CancellationToken cancellationToken);

    /// <summary>
    /// Spends one token: a single replay-proof round trip (<c>UPDATE … WHERE used_at IS NULL AND
    /// expires_at &gt; now() RETURNING user_id</c>), so two callers cannot both succeed.
    /// </summary>
    /// <param name="token">The plaintext token the caller presented.</param>
    /// <param name="kind">The flow the caller is performing; a token of another kind is refused.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>The user the token authorized, or <c>validation_failed</c> for a used, expired or unknown
    /// token — the same answer for all three (09 §4).</returns>
    Task<ErrorOr<Guid>> ConsumeAsync(string token, UserActionKind kind, CancellationToken cancellationToken);
}
