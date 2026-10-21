using ErrorOr;

namespace NightTales.Application.Abstractions.Security;

/// <summary>What one <c>POST /auth/reauth</c> attempt answered (09.10.4-T1 Design).</summary>
/// <remarks>
/// <see cref="Failed"/> is a <em>success-typed</em> outcome on purpose ([RULED — README §B10 WT29]):
/// a failed re-authentication still increments <c>refresh_tokens.reauth_attempts</c>, may revoke the
/// family and always writes an audit row, so the handler must return a value the pipeline commits
/// instead of an error that rolls the transaction back. The endpoint maps it to the 401 the
/// <see cref="ReauthResult.Failure"/> carries.
/// </remarks>
public enum ReauthOutcome
{
    /// <summary>200 <c>{ reauthenticatedAt }</c>: the family's <c>reauthenticated_at</c> was stamped.</summary>
    Succeeded,

    /// <summary>401: the attempt failed and what it wrote has already committed.</summary>
    Failed,
}

/// <summary>
/// The result the <c>/auth/reauth</c> endpoint turns into a response (09.10.4-T1 Design).
/// </summary>
/// <param name="Outcome">Which of the two answers the attempt produced.</param>
/// <param name="ReauthenticatedAt">The instant the family was stamped, or null for a failure.</param>
/// <param name="Failure">
/// The error the endpoint maps to 401 — <c>invalid_credentials</c>, <c>invalid_mfa_code</c> or
/// <c>session_revoked</c> (the fifth failure) — or null when the attempt succeeded.
/// </param>
public sealed record ReauthResult(
    ReauthOutcome Outcome,
    DateTimeOffset? ReauthenticatedAt,
    Error? Failure = null);

/// <summary>
/// The re-authentication flow behind <c>POST /auth/reauth</c> (09 README BD4).
/// </summary>
/// <remarks>
/// <para>
/// Behind a port for the same layer reason as <see cref="IAuthSessionService"/>: it verifies the password
/// through <c>ICredentialVerifier</c> and the code through <see cref="ITotpVerifier"/>, both of which
/// hand back an <c>AppUser</c> (an Infrastructure type). The Application handler stays a thin delegator.
/// </para>
/// <para>
/// The whole attempt — the counter, the family revocation, the audit row and the alert — runs inside the
/// caller's transaction (09 README D5), which is what makes WT29's "commits before the 401" hold.
/// </para>
/// </remarks>
public interface IReauthService
{
    /// <summary>Attempts one re-authentication of the caller's family.</summary>
    /// <param name="password">The submitted password; never logged.</param>
    /// <param name="code">
    /// The submitted TOTP code, or null when the account has no authenticator enrolled (BD4).
    /// </param>
    /// <param name="info">The caller metadata the audit row records.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<ReauthResult>> ReauthenticateAsync(
        string password,
        string? code,
        RequestInfo info,
        CancellationToken ct);
}
