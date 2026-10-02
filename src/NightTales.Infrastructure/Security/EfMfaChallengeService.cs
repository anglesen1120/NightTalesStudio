using System.Net;
using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Entities;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The MFA login challenge of 09.3.2-T3 (09 README BD1/BD4): the row <c>POST /auth/login</c> opens for an
/// account that always signs in with an authenticator, and the second step that consumes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Raw SQL on purpose</b>, like <see cref="EfRefreshTokenService"/> next to it. Both halves are
/// lock-then-decide algorithms — <c>BeginAsync</c> supersedes the account's live rows and inserts exactly
/// one, and <c>CompleteAsync</c> settles the outcome under the row's <c>FOR UPDATE</c> — and every write
/// has to commit with the command that asked for it or not at all (09 README D5). Nothing here is a
/// tracked-entity update.
/// </para>
/// <para>
/// <b>One lock order.</b> The platform's order is the per-user advisory lock, then any family lock, then
/// the <c>users</c> row. This flow takes the per-user lock first in both halves — before the challenge row
/// lock here, and before the supersede/insert pair there — so it cannot deadlock against the MFA
/// lifecycle (setup/verify/disable) or a re-authentication, which all take the same lock first.
/// </para>
/// <para>
/// <b>Failure is a value, not an error.</b> A wrong code, a dead challenge and a locked window all leave
/// committed state behind — the attempt counter, the challenge's death, the audit rows — so
/// <see cref="CompleteAsync"/> answers <see cref="MfaChallengeOutcome"/> and the endpoint maps it to
/// 401/429 ([RULED — README §B10 WT29]). Only a decision that writes nothing (an account that is no
/// longer active, a database that cannot answer, a verifier that answers its own 503 — F1) is an error.
/// </para>
/// <para>
/// Only SHA-256 hex hashes of the two secrets are ever stored; the plaintext pair exists in the login
/// response and the pre-auth cookie alone and is never logged (BD16).
/// </para>
/// </remarks>
/// <param name="database">The scope's write model: the account read, <c>last_login_at</c> and the connection.</param>
/// <param name="issuer">The 32-byte base64url secret factory and the SHA-256 hex of the stored forms.</param>
/// <param name="verifier">The replay-proof TOTP verifier (09.10.4-T1, WT31) — never Identity's.</param>
/// <param name="recoveryCodes">The single-use recovery codes (09.10.4-T1).</param>
/// <param name="refreshTokens">The family store that opens the session and revokes the others.</param>
/// <param name="guessWindow">The per-user wrong-code window (WT35).</param>
/// <param name="alerter">The P security alert (WT25).</param>
/// <param name="audit">The writer of this flow's <c>account_security</c> rows.</param>
/// <param name="metrics">The <c>auth_mfa_total{result}</c> counter (09.3.2-T3 Outputs).</param>
/// <param name="options">The challenge lifetime and the attempt ceiling (BD4: 5 minutes, 5 attempts).</param>
/// <param name="clock">The scope's clock; every stored instant comes from it.</param>
public sealed class EfMfaChallengeService(
    AppDbContext database,
    ITokenIssuer issuer,
    ITotpVerifier verifier,
    IRecoveryCodeService recoveryCodes,
    IRefreshTokenService refreshTokens,
    IMfaGuessWindow guessWindow,
    ISecurityAlerter alerter,
    IAuditWriter audit,
    AuthMetrics metrics,
    IOptions<MfaOptions> options,
    IClock clock) : IMfaChallengeService
{
    /// <inheritdoc />
    public async Task<ErrorOr<MfaChallenge>> BeginAsync(Guid userId, RequestInfo info, CancellationToken ct)
    {
        try
        {
            var now = clock.UtcNow;

            // The per-user advisory lock first: the supersede below and the insert after it are one
            // atomic unit per account, so two concurrent password steps of the same user can never leave
            // two completable challenges behind ("at most one challenge per user", BD4).
            await SecuritySql.LockUserAsync(database, userId, ct).ConfigureAwait(false);

            await SupersedeAsync(userId, now, ct).ConfigureAwait(false);

            var mfaToken = issuer.CreateSecret();
            var preAuthCookie = issuer.CreateSecret();
            var challengeId = Guid.CreateVersion7();
            var expiresAt = now + options.Value.Mfa.ChallengeLifetime;

            await InsertAsync(challengeId, userId, mfaToken, preAuthCookie, info, expiresAt, ct)
                .ConfigureAwait(false);

            return new MfaChallenge(challengeId, userId, mfaToken, preAuthCookie, expiresAt);
        }
        catch (Exception exception) when (SecuritySql.IsTransient(exception))
        {
            return SecuritySql.Unavailable<MfaChallenge>();
        }
    }

    /// <inheritdoc />
    public async Task<ErrorOr<MfaChallengeOutcome>> CompleteAsync(
        MfaChallengeRequest request,
        RequestInfo info,
        CancellationToken ct)
    {
        try
        {
            var tokenHash = issuer.HashToken(request.MfaToken);

            // The row's account is resolved without a lock first, because the per-user advisory lock has to
            // be taken before the row lock (the platform's one lock order). `user_id` is immutable on a
            // challenge row, so this read cannot name the wrong account.
            var userId = await FindUserIdAsync(tokenHash, ct).ConfigureAwait(false);
            if (userId is null)
            {
                // A consumed, superseded, expired-or-swept or invented token all answer the same 401: the
                // challenge is a bearer secret and telling the caller which half was wrong helps only an
                // attacker (BD4).
                return Invalid();
            }

            await SecuritySql.LockUserAsync(database, userId.Value, ct).ConfigureAwait(false);

            var row = await SelectForUpdateAsync(tokenHash, ct).ConfigureAwait(false);
            if (row is null)
            {
                return Invalid();
            }

            // 09.3.2-T3 F2: the decision's instant is read here, after the per-user advisory lock and the
            // row lock, so a completion that waited for another transaction of the same account judges the
            // challenge by the moment it actually verifies — not by the moment the request arrived. Every
            // later use of `now` (the window, the charge, the consume, the audit) is that same instant.
            var now = clock.UtcNow;

            if (row.Value.UsedAt is not null
                || row.Value.Attempts >= options.Value.Mfa.MaxAttempts
                || row.Value.ExpiresAt <= now)
            {
                return Invalid();
            }

            if (!MatchesBinding(row.Value, request, info))
            {
                return Invalid();
            }

            var user = await database.Users
                .FirstOrDefaultAsync(candidate => candidate.Id == row.Value.UserId, ct)
                .ConfigureAwait(false);

            if (user is null)
            {
                // The FK cascade deletes the challenge with the account, so this is the residual race
                // between the two steps; it answers what the deleted row would have (failure handling).
                return Invalid();
            }

            if (user.Status is not UserStatus.Active)
            {
                // The password step checked the status; the account may have been suspended in the
                // minutes between the two. This writes nothing, so an error (and its rollback) is the
                // honest answer — the same 401 the refresh flow gives an inactive account.
                return Error.Unauthorized(ErrorCodes.SessionRevoked, "The account is not active.");
            }

            // WT35: ten wrong codes inside fifteen minutes block the whole sign-in, so no code is even
            // verified once the window is tripped — a correct code included, exactly like `/auth/login`'s
            // gate before the password check.
            if (guessWindow.BlockedFor(user.Id, now) is not null)
            {
                return await BlockedAsync(user, info, now, ct).ConfigureAwait(false);
            }

            // BD4: the address is audited, never enforced. A different address between the two steps is
            // normal (Wi-Fi → mobile, CGNAT) and must not cost a code.
            if (IpChanged(row.Value.Ip, info))
            {
                await AuditIpChangedAsync(user, row.Value.Ip, info, now, ct).ConfigureAwait(false);
            }

            if (await VerifyAsync(request, user.Id, ct).ConfigureAwait(false) is { } failure)
            {
                // 09.3.2-T3 F1: only a code the verifier or the recovery service rejected is a guess and is
                // charged. An infrastructure failure (their 503) is not a wrong code: charging the
                // in-memory window for it would lock an innocent account behind a database bug, and on a
                // transaction PostgreSQL already aborted (a deadlock, 25P02) the charge's own UPDATE would
                // replace the documented 503 with a 500. Returning the error rolls the attempt back.
                if (failure.Code != ErrorCodes.InvalidMfaCode)
                {
                    return failure;
                }

                return await ChargeAsync(user, row.Value, failure, info, now, ct).ConfigureAwait(false);
            }

            var method = string.IsNullOrEmpty(request.Code) ? AuthMethod.Rc : AuthMethod.Otp;

            return await SucceedAsync(user, row.Value, method, info, now, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (SecuritySql.IsTransient(exception))
        {
            return SecuritySql.Unavailable<MfaChallengeOutcome>();
        }
    }

    /// <summary>Invalidates every live challenge of one account; runs under its advisory lock.</summary>
    private async Task SupersedeAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await using var command = SecuritySql.Command(database, SupersedeSql, requireTransaction: true);
        command.Parameters.Add(new NpgsqlParameter("user_id", NpgsqlDbType.Uuid) { Value = userId });
        command.Parameters.AddWithValue("used_at", now);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Writes the one live challenge; only the hashes of the two secrets reach the row.</summary>
    private async Task InsertAsync(
        Guid challengeId,
        Guid userId,
        string mfaToken,
        string preAuthCookie,
        RequestInfo info,
        DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        await using var command = SecuritySql.Command(database, InsertSql, requireTransaction: true);

        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = challengeId });
        command.Parameters.Add(new NpgsqlParameter("user_id", NpgsqlDbType.Uuid) { Value = userId });
        command.Parameters.AddWithValue("token_hash", issuer.HashToken(mfaToken));
        command.Parameters.AddWithValue("preauth_cookie_hash", issuer.HashToken(preAuthCookie));
        command.Parameters.Add(new NpgsqlParameter("user_agent", NpgsqlDbType.Text)
        {
            Value = Truncate(info.UserAgent) is { } agent ? agent : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("ip", NpgsqlDbType.Inet)
        {
            Value = ParseIp(info.Ip) is { } address ? address : DBNull.Value,
        });
        command.Parameters.AddWithValue("expires_at", expiresAt);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The account a presented token belongs to, or null when no row has that hash.</summary>
    private async Task<Guid?> FindUserIdAsync(string tokenHash, CancellationToken ct)
    {
        await SecuritySql.EnsureOpenAsync(database, ct).ConfigureAwait(false);

        await using var command = SecuritySql.Command(database, SelectUserIdSql, requireTransaction: false);
        command.Parameters.AddWithValue("token_hash", tokenHash);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false) ? reader.GetFieldValue<Guid>(0) : null;
    }

    /// <summary>The challenge row, locked for the rest of the transaction.</summary>
    private async Task<ChallengeRow?> SelectForUpdateAsync(string tokenHash, CancellationToken ct)
    {
        await using var command = SecuritySql.Command(database, SelectForUpdateSql, requireTransaction: true);
        command.Parameters.AddWithValue("token_hash", tokenHash);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new ChallengeRow(
            reader.GetFieldValue<Guid>(0),
            reader.GetFieldValue<Guid>(1),
            reader.GetString(2),
            await reader.IsDBNullAsync(3, ct).ConfigureAwait(false) ? null : reader.GetString(3),
            await reader.IsDBNullAsync(4, ct).ConfigureAwait(false) ? null : reader.GetFieldValue<IPAddress>(4),
            reader.GetInt16(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            await reader.IsDBNullAsync(7, ct).ConfigureAwait(false) ? null : reader.GetFieldValue<DateTimeOffset>(7));
    }

    /// <summary>
    /// Whether the completion repeats the two bound facts of the password step: the same
    /// <c>User-Agent</c> and the same pre-auth cookie (BD4).
    /// </summary>
    /// <remarks>
    /// The cookie comparison is <see cref="CryptographicOperations.FixedTimeEquals"/> over the two
    /// hashes, so it cannot leak the stored value through timing, and a submission that carries no
    /// <c>User-Agent</c> never matches a stored one.
    /// </remarks>
    private bool MatchesBinding(ChallengeRow row, MfaChallengeRequest request, RequestInfo info)
    {
        if (!string.Equals(row.UserAgent, Truncate(info.UserAgent), StringComparison.Ordinal))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(row.PreAuthCookieHash);
        var presented = Encoding.UTF8.GetBytes(issuer.HashToken(request.PreAuthCookie));

        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }

    /// <summary>
    /// Verifies the one submitted code, and answers the error the caller inspects: a code the store
    /// rejected (<c>invalid_mfa_code</c>, which the failure path charges) or an infrastructure failure
    /// (<c>service_unavailable</c>, which the caller returns uncharged — 09.3.2-T3 F1).
    /// </summary>
    /// <remarks>
    /// The TOTP branch passes <see cref="TotpSlot.Active"/> ([RULED — README §B10 WT31]): only the
    /// enrolment confirmation may read the unverified pending secret, so a leaked pending secret cannot
    /// sign a session in. The replay protection is the verifier's own <c>users.last_totp_step</c>
    /// compare-and-set (BD4).
    /// </remarks>
    private async Task<Error?> VerifyAsync(
        MfaChallengeRequest request,
        Guid userId,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(request.Code))
        {
            var verified = await verifier
                .VerifyAsync(userId, request.Code, TotpSlot.Active, ct)
                .ConfigureAwait(false);

            return verified.IsError ? verified.FirstError : null;
        }

        // The validator guarantees exactly one of the two fields, so this is the recovery-code branch.
        var redeemed = await recoveryCodes.RedeemAsync(userId, request.RecoveryCode!, ct).ConfigureAwait(false);

        return redeemed.IsError ? redeemed.FirstError : null;
    }

    /// <summary>
    /// Charges one wrong code: the per-user window, the challenge's attempt counter (and its death at
    /// the ceiling), the audit rows and the owner/admin alert.
    /// </summary>
    /// <remarks>
    /// The answer is 401 <c>invalid_mfa_code</c> — including on the fifth attempt, which is the one that
    /// kills the challenge: the caller learns that its code was wrong, and the <em>next</em> call is the
    /// one that answers <c>mfa_token_invalid</c>. Only a saturated window short-circuits to 429 before
    /// anything is charged, because that wrong code was never admitted ([RULED — README §B10 WT35]).
    /// </remarks>
    private async Task<MfaChallengeOutcome> ChargeAsync(
        AppUser user,
        ChallengeRow row,
        Error failure,
        RequestInfo info,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (guessWindow.RegisterWrongCode(user.Id, now) is GuessWindowCharge.Refused)
        {
            metrics.Mfa(ErrorCodes.RateLimited);

            return new MfaChallengeOutcome(
                MfaOutcome.RateLimited,
                user.Id,
                AuthMethod.Pwd,
                user.Role,
                null,
                null,
                MfaErrors.RateLimited(InMemoryMfaGuessWindow.RetryAfterSeconds));
        }

        var attempts = await ChargeAttemptAsync(row.Id, now, ct).ConfigureAwait(false);

        await audit
            .WriteAsync(
                SecurityAudit.EntryWithData(
                    SecurityAuditActions.MfaFailed,
                    user.Id,
                    info,
                    Visibility.User,
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["attempts"] = attempts },
                    now),
                ct)
            .ConfigureAwait(false);

        // The one WT35 row of the whole window is written by the first committing path that observes the
        // trip; this path writes (its counters commit), so it may claim it.
        if (guessWindow.BlockedFor(user.Id, now) is not null && guessWindow.TryClaimTripReport(user.Id))
        {
            await audit
                .WriteAsync(
                    SecurityAudit.Entry(
                        SecurityAuditActions.MfaCodeGuessingLocked,
                        user.Id,
                        info,
                        Visibility.Internal,
                        "wrong-code window tripped",
                        now),
                    ct)
                .ConfigureAwait(false);
        }

        // D9: the first wrong second factor of an owner or admin is the one an operator wants to hear
        // about; the later ones are the window's business (BD4).
        if (attempts == 1 && user.Role is PlatformRole.Admin or PlatformRole.Owner)
        {
            await alerter
                .AlertAsync(
                    new SecurityEvent(
                        SecurityEventKinds.MfaLoginFailed,
                        user.Id,
                        "The first MFA login attempt of an owner or admin failed."),
                    ct)
                .ConfigureAwait(false);
        }

        metrics.Mfa(ErrorCodes.InvalidMfaCode);

        return new MfaChallengeOutcome(
            MfaOutcome.WrongCode,
            user.Id,
            AuthMethod.Pwd,
            user.Role,
            null,
            null,
            failure);
    }

    /// <summary>Charges one attempt and kills the challenge when it reaches the ceiling (BD4).</summary>
    private async Task<int> ChargeAttemptAsync(Guid challengeId, DateTimeOffset now, CancellationToken ct)
    {
        await using var command = SecuritySql.Command(database, ChargeAttemptSql, requireTransaction: true);

        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = challengeId });
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("max_attempts", (short)options.Value.Mfa.MaxAttempts);

        var charged = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        return (int)charged!;
    }

    /// <summary>
    /// Consumes the challenge and issues the session, or revokes every other family when a recovery code
    /// paid for the sign-in.
    /// </summary>
    private async Task<ErrorOr<MfaChallengeOutcome>> SucceedAsync(
        AppUser user,
        ChallengeRow row,
        AuthMethod method,
        RequestInfo info,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using (var consume = SecuritySql.Command(database, ConsumeSql, requireTransaction: true))
        {
            consume.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = row.Id });
            consume.Parameters.AddWithValue("used_at", now);

            var consumed = await consume.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (consumed != 1)
            {
                // The row is locked by this transaction and was live a moment ago, so zero rows is a bug
                // rather than a race: fail closed instead of issuing a second session for one challenge.
                return Invalid();
            }
        }

        if (method is AuthMethod.Rc)
        {
            // BD4: a recovery code is the break-glass path, so it ends every session the account had —
            // there is no family yet at login, so nothing is spared. The stored reason is `mfa_change`: the
            // column's CHECK lists the RevocationReason members and this task may not migrate, and it is
            // not `rotated`, so a stale cookie still answers 401 session_revoked without a reuse alert
            // (WT36) — the same reading 09.10.4-T1's re-authentication failure uses.
            var revoked = await refreshTokens
                .RevokeAllFamiliesAsync(user.Id, RevocationReason.MfaChange.ToWire(), exceptFamilyId: null, ct)
                .ConfigureAwait(false);

            if (revoked.IsError)
            {
                return revoked.Errors;
            }
        }

        var session = await refreshTokens.StartSessionAsync(user, info, ct).ConfigureAwait(false);
        if (session.IsError)
        {
            return session.Errors;
        }

        // The last successful login is part of the account, so it is written through the tracked entity and
        // commits with the family the same transaction holds.
        user.LastLoginAt = now;

        await audit
            .WriteAsync(
                SecurityAudit.EntryWithData(
                    SecurityAuditActions.LoginSucceeded,
                    user.Id,
                    info,
                    Visibility.User,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["method"] = method.ToAmrClaim(),
                    },
                    now),
                ct)
            .ConfigureAwait(false);

        // The D9 event of a completed owner/admin sign-in: a recovery code is the break-glass path and the
        // frozen kind for it is `security.recovery_code_used` (README §B10 line 1108). A plain TOTP login is
        // the account's normal day, and the S-phase alert rules have no kind for it — the two anomalies the
        // frozen list does name, this one and `security.owner_login_new_ip` above, are exactly the two this
        // task can raise.
        if (method is AuthMethod.Rc && user.Role is PlatformRole.Admin or PlatformRole.Owner)
        {
            await alerter
                .AlertAsync(
                    new SecurityEvent(
                        SecurityEventKinds.RecoveryCodeUsed,
                        user.Id,
                        "An owner or admin signed in with a recovery code."),
                    ct)
                .ConfigureAwait(false);
        }

        metrics.Mfa("ok");

        return new MfaChallengeOutcome(
            MfaOutcome.Tokens,
            user.Id,
            method,
            user.Role,
            issuer.IssueAccessToken(user, session.Value.FamilyId, method, now),
            session.Value,
            null);
    }

    /// <summary>The 429 of a tripped window, written by the first committing path that observes it.</summary>
    private async Task<MfaChallengeOutcome> BlockedAsync(
        AppUser user,
        RequestInfo info,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (guessWindow.TryClaimTripReport(user.Id))
        {
            await audit
                .WriteAsync(
                    SecurityAudit.Entry(
                        SecurityAuditActions.MfaCodeGuessingLocked,
                        user.Id,
                        info,
                        Visibility.Internal,
                        "wrong-code window tripped",
                        now),
                    ct)
                .ConfigureAwait(false);
        }

        metrics.Mfa(ErrorCodes.RateLimited);

        return new MfaChallengeOutcome(
            MfaOutcome.RateLimited,
            user.Id,
            AuthMethod.Pwd,
            user.Role,
            null,
            null,
            MfaErrors.RateLimited(InMemoryMfaGuessWindow.RetryAfterSeconds));
    }

    /// <summary>
    /// Records the address change of BD4: one internal audit row carrying the two <em>hashes</em> — never
    /// the addresses, which are already in the row's <c>ip</c> column — plus the D9 event an owner's or
    /// admin's sign-in from a new address deserves (09 §4 story 09.3.2).
    /// </summary>
    private async Task AuditIpChangedAsync(
        AppUser user,
        IPAddress? previous,
        RequestInfo info,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var current = ParseIp(info.Ip);

        await audit
            .WriteAsync(
                SecurityAudit.EntryWithData(
                    SecurityAuditActions.MfaIpChanged,
                    user.Id,
                    info,
                    Visibility.Internal,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["previousIpHash"] = HashAddress(previous),
                        ["currentIpHash"] = HashAddress(current),
                    },
                    now),
                ct)
            .ConfigureAwait(false);

        if (user.Role is PlatformRole.Admin or PlatformRole.Owner)
        {
            await alerter
                .AlertAsync(
                    new SecurityEvent(
                        SecurityEventKinds.OwnerLoginNewIp,
                        user.Id,
                        "The second step of an owner's or admin's sign-in came from another address."),
                    ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Whether the completion's address differs from the password step's (BD4: audited only).</summary>
    private static bool IpChanged(IPAddress? stored, RequestInfo info) =>
        !Equals(stored, ParseIp(info.Ip));

    /// <summary>
    /// The SHA-256 hex of one address, or null when there was none: the audit payload never carries a raw
    /// address (BD16), and two events of the same address still match.
    /// </summary>
    private static string? HashAddress(IPAddress? address) =>
        address is null
            ? null
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(address.ToString())));

    /// <summary>The 401 every unusable challenge answers, whatever made it unusable (BD4).</summary>
    private MfaChallengeOutcome Invalid()
    {
        metrics.Mfa(ErrorCodes.MfaTokenInvalid);

        return new MfaChallengeOutcome(
            MfaOutcome.TokenInvalid,
            Guid.Empty,
            AuthMethod.Pwd,
            PlatformRole.User,
            null,
            null,
            MfaErrors.MfaTokenInvalid());
    }

    /// <summary>The caller's address, or null when it is absent or unparsable.</summary>
    private static IPAddress? ParseIp(string? ip) =>
        !string.IsNullOrWhiteSpace(ip) && IPAddress.TryParse(ip, out var parsed) ? parsed : null;

    /// <summary>
    /// The caller's <c>User-Agent</c>, normalised and bounded: the column is unbounded <c>text</c>, and
    /// an empty header is "no agent" rather than a value that could match a stored one (BD4).
    /// </summary>
    private static string? Truncate(string? userAgent) =>
        string.IsNullOrEmpty(userAgent) ? null
        : userAgent.Length <= RefreshToken.UserAgentMaxLength ? userAgent
        : userAgent[..RefreshToken.UserAgentMaxLength];

    /// <summary>The locked row as the flow needs it.</summary>
    private readonly record struct ChallengeRow(
        Guid Id,
        Guid UserId,
        string PreAuthCookieHash,
        string? UserAgent,
        IPAddress? Ip,
        short Attempts,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? UsedAt);

    // BD4: at most one completable challenge per account — every live row of the account dies before the
    // new one is inserted.
    private const string SupersedeSql =
        """
        UPDATE auth_challenges
        SET used_at = @used_at
        WHERE user_id = @user_id AND used_at IS NULL
        """;

    // Only the hashes and the caller's metadata are stored; the plaintext pair is returned once and never
    // written (BD16).
    private const string InsertSql =
        """
        INSERT INTO auth_challenges
            (id, user_id, token_hash, preauth_cookie_hash, user_agent, ip, attempts, expires_at, used_at)
        VALUES
            (@id, @user_id, @token_hash, @preauth_cookie_hash, @user_agent, @ip, 0, @expires_at, NULL)
        """;

    private const string SelectUserIdSql =
        """
        SELECT user_id
        FROM auth_challenges
        WHERE token_hash = @token_hash
        """;

    // The decision (consumed, expired, at the ceiling) and the consume-or-charge write must see one and
    // the same row even if a second request arrives with the same token.
    private const string SelectForUpdateSql =
        """
        SELECT id, user_id, preauth_cookie_hash, user_agent, ip, attempts, expires_at, used_at
        FROM auth_challenges
        WHERE token_hash = @token_hash
        FOR UPDATE
        """;

    // BD4: the fifth wrong code kills the challenge in the same statement that counts it, so no later call
    // can spend a sixth attempt even under concurrency.
    private const string ChargeAttemptSql =
        """
        UPDATE auth_challenges
        SET attempts = (attempts + 1)::smallint,
            used_at = CASE WHEN attempts + 1 >= @max_attempts THEN @now ELSE used_at END
        WHERE id = @id
        RETURNING attempts::integer
        """;

    private const string ConsumeSql =
        """
        UPDATE auth_challenges
        SET used_at = @used_at
        WHERE id = @id AND used_at IS NULL
        """;
}
