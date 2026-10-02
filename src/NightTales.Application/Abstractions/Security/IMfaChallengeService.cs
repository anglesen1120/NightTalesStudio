using ErrorOr;
using NightTales.Domain.Common;

namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// The MFA challenge port of the login flow (09 README BD4), <em>declared</em> by 09.3.2-T1 because
/// <c>POST /auth/login</c> is its first caller, while its implementation and its table
/// (<c>auth_challenges</c>) belong to 09.3.2-T3.
/// </summary>
/// <remarks>
/// <para>
/// 09.3.2-T1 declared the type empty and without an implementation: [RULED — README §B10 WT22, WT39]
/// there is no interim <c>PendingMfaChallengeService</c>. This task adds the two members and the
/// Infrastructure implementation, and edits the same login flow to call <see cref="BeginAsync"/>.
/// </para>
/// <para>
/// <b>The success-typed outcome is the point.</b> A wrong code, a dead token and a locked window all
/// leave committed state behind (the attempt counter, the possible challenge death, the audit rows), so
/// <see cref="CompleteAsync"/> answers a <see cref="MfaChallengeOutcome"/> value for them and the endpoint
/// maps it to 401/429 ([RULED — README §B10 WT29]); an error could never be answered after those writes,
/// because the pipeline's unit of work rolls the transaction back with it. Only a decision that leaves the
/// database alone — an account that is no longer active, a database that cannot answer — is an error.
/// </para>
/// </remarks>
public interface IMfaChallengeService
{
    /// <summary>
    /// Opens one challenge for <paramref name="userId"/> and supersedes every live challenge of that
    /// account, so at most one row is ever completable.
    /// </summary>
    /// <remarks>
    /// The two secrets travel in the answer exactly once and are never stored in plaintext: only
    /// SHA-256 hex hashes of the <c>mfaToken</c> and of the pre-auth cookie reach the row (BD16).
    /// </remarks>
    /// <param name="userId">The account that just proved its password.</param>
    /// <param name="info">The caller metadata recorded on the row (the login step's address).</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<MfaChallenge>> BeginAsync(Guid userId, RequestInfo info, CancellationToken ct);

    /// <summary>
    /// Exchanges one challenge plus a TOTP or recovery code for the session's tokens.
    /// </summary>
    /// <param name="request">
    /// The two submitted secrets and the code: the <c>mfaToken</c> from the login body, the pre-auth
    /// cookie the Api read, and exactly one of the two codes (the validator enforces that).
    /// </param>
    /// <param name="info">The caller metadata the audit rows and the new family record.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<MfaChallengeOutcome>> CompleteAsync(
        MfaChallengeRequest request,
        RequestInfo info,
        CancellationToken ct);
}

/// <summary>
/// The challenge document a password step hands back (09.3.2-T3 Design): the single-use <c>mfaToken</c>
/// that goes into the 401 body and the separate <see cref="PreAuthCookie"/> the Api writes into
/// <c>__Secure-nts_mfa</c>.
/// </summary>
/// <remarks>
/// Both values are plaintext and returned once — only their hashes are stored. Requiring both is what
/// makes a stolen body useless to a caller without the browser's cookie, and a stolen cookie useless
/// without the body.
/// </remarks>
/// <param name="ChallengeId">The row id (UUIDv7), for logs and tests; never client input.</param>
/// <param name="UserId">The account the challenge belongs to.</param>
/// <param name="MfaToken">The 32-byte base64url token of the 401 body; never logged or audited.</param>
/// <param name="PreAuthCookie">The 32-byte base64url secret the cookie carries; never logged.</param>
/// <param name="ExpiresAt">When the challenge dies (<c>Auth:Mfa:ChallengeMinutes</c>, BD4: five minutes).</param>
public sealed record MfaChallenge(
    Guid ChallengeId,
    Guid UserId,
    string MfaToken,
    string PreAuthCookie,
    DateTimeOffset ExpiresAt);

/// <summary>
/// What one completion attempt presented (09.3.2-T3 Design).
/// </summary>
/// <remarks>
/// <see cref="PreAuthCookie"/> is ambient data the Api reads from the request, exactly as
/// <see cref="RequestInfo"/> is: it is never part of the JSON body, so a client cannot invent one.
/// </remarks>
/// <param name="MfaToken">The token of the login step's 401 body.</param>
/// <param name="Code">The six-digit authenticator code, or null when a recovery code was presented.</param>
/// <param name="RecoveryCode">The recovery code, or null when an authenticator code was presented.</param>
/// <param name="PreAuthCookie">The plaintext value of the <c>__Secure-nts_mfa</c> cookie.</param>
public sealed record MfaChallengeRequest(string MfaToken, string? Code, string? RecoveryCode, string PreAuthCookie);

/// <summary>What one completion attempt answered (09.3.2-T3 Design, [RULED — README §B10 WT29]).</summary>
public enum MfaOutcome
{
    /// <summary>200: the challenge is consumed and the session's tokens were issued.</summary>
    Tokens,

    /// <summary>401 <c>invalid_mfa_code</c>: the code was wrong; the attempt was charged and committed.</summary>
    WrongCode,

    /// <summary>401 <c>mfa_token_invalid</c>: unknown, consumed, expired or revoked challenge.</summary>
    TokenInvalid,

    /// <summary>429 <c>rate_limited</c>: the per-user wrong-code window blocks the sign-in (WT35).</summary>
    RateLimited,
}

/// <summary>
/// The completion's outcome (09.3.2-T3 Design, extended by the success-typed ruling of [RULED — README
/// §B10 WT29]).
/// </summary>
/// <remarks>
/// <para>
/// Only <see cref="MfaOutcome.Tokens"/> fills <see cref="Access"/>, <see cref="Refresh"/>,
/// <see cref="Role"/> and the real <see cref="UserId"/>/<see cref="Method"/>: the tokens are issued by
/// the same flow, inside the same transaction, so the challenge's death and the new family commit
/// together — the design's endpoint paragraph ("issue tokens through <c>ITokenIssuer</c> +
/// <c>IRefreshTokenService</c>") is the flow's own tail, not a second service the handler has to
/// coordinate.
/// </para>
/// <para>
/// The three failure outcomes carry the <see cref="Failure"/> the endpoint maps — 401
/// <c>invalid_mfa_code</c>, 401 <c>mfa_token_invalid</c>, or 429 <c>rate_limited</c> with the
/// <c>Retry-After</c> of the wrong-code window — and their <see cref="UserId"/> is the account the
/// attempt belonged to, or <see cref="Guid.Empty"/> when the presented token resolved to no row.
/// </para>
/// </remarks>
/// <param name="Outcome">Which of the four answers the attempt produced.</param>
/// <param name="UserId">The account the challenge belonged to; <see cref="Guid.Empty"/> when unknown.</param>
/// <param name="Method">The method that completed the login (<c>otp</c>/<c>rc</c>); <c>pwd</c> otherwise.</param>
/// <param name="Role">The authenticated account's role; <c>User</c> when nothing was issued.</param>
/// <param name="Access">The session's access token, or null on every failure outcome.</param>
/// <param name="Refresh">The new family's plaintext token, or null on every failure outcome.</param>
/// <param name="Failure">The error the endpoint answers with; null only for <see cref="MfaOutcome.Tokens"/>.</param>
public sealed record MfaChallengeOutcome(
    MfaOutcome Outcome,
    Guid UserId,
    AuthMethod Method,
    PlatformRole Role,
    IssuedAccessToken? Access,
    IssuedRefreshSession? Refresh,
    Error? Failure);
