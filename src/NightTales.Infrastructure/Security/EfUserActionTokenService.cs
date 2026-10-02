using System.Data;
using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Entities;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The <c>user_action_tokens</c> store of 09.3.3-T1 (09 README BD3/WF11): the invalidating issue and the
/// replay-proof consume, each one statement.
/// </summary>
/// <remarks>
/// <para>
/// <b>Raw SQL on purpose.</b> An issue has to invalidate the user's previous live tokens of the same flow
/// <em>and</em> insert the new one, and two issues of one scope must never both leave a live row: under
/// <c>ReadCommitted</c> a single <c>WITH … UPDATE … INSERT</c> is not enough, because two callers that each
/// see no live row both update zero rows and both insert. The issue therefore takes the scope's
/// transaction-scoped advisory lock before the update and holds it through the insert. The two callers
/// differ: the reset consume runs inside a command's unit of work (09 README D5), while the send-time mint
/// runs in <c>SendEmailJob</c>, which owns no explicit transaction — so the issue opens one there. The
/// consume is the single <c>UPDATE … RETURNING</c> of the design, so two callers presenting one token can
/// never both win.
/// </para>
/// <para>
/// <b>No tracked entity.</b> Nothing here goes through <see cref="AppDbContext"/>'s change tracker: the
/// table is infrastructure-only, exactly like <c>refresh_tokens</c>, and the statements join the caller's
/// transaction when there is one.
/// </para>
/// <para>
/// <b>The crypto is BD3's, spelled here because the send-time caller is the Worker.</b> A token is 32
/// random bytes in unpadded base64url and only its lowercase hex SHA-256 is stored — the same encoding and
/// hash <c>Es256TokenIssuer</c> uses for the refresh token. This type cannot depend on that issuer: it is
/// the Api's web signing key holder, and the Worker (which mints the reset token inside
/// <c>SendEmailJob</c>) must boot without one.
/// </para>
/// </remarks>
/// <param name="database">The scope's write model; its connection and transaction carry every statement.</param>
/// <param name="options">The per-kind lifetimes (<c>Auth:ActionToken:*</c>).</param>
/// <param name="clock">The scope's clock; every stored instant comes from it.</param>
public sealed class EfUserActionTokenService(
    AppDbContext database,
    IOptions<UserActionTokenOptions> options,
    IClock clock) : IUserActionTokenService
{
    /// <summary>The number of random bytes in one token (BD3: 256 bits).</summary>
    public const int TokenBytes = 32;

    /// <inheritdoc />
    public async Task<ErrorOr<string>> IssueAsync(
        Guid userId,
        UserActionKind kind,
        RequestInfo info,
        CancellationToken cancellationToken)
    {
        // The caller metadata is part of the port for the request-path call, but BD3's row stores no IP
        // and no user agent: the token's audit trail is the consume, not the issue.
        ArgumentNullException.ThrowIfNull(info);

        var expiresAt = clock.UtcNow + options.Value.TtlFor(kind);

        return await IssueAsync(userId, kind, subjectId: null, expiresAt, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ErrorOr<string>> IssueAsync(
        Guid userId,
        UserActionKind kind,
        Guid subjectId,
        DateTimeOffset expiresAt,
        RequestInfo info,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("A subject-scoped token needs a real subject id.", nameof(subjectId));
        }

        return await IssueAsync(userId, kind, subjectId, expiresAt, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ErrorOr<Guid>> ConsumeAsync(
        string token,
        UserActionKind kind,
        CancellationToken cancellationToken)
    {
        try
        {
            // An absent token is the same answer as a used, expired or unknown one (09 §4): the caller must
            // not be able to tell the four apart, and no statement runs for a value that cannot match.
            if (string.IsNullOrWhiteSpace(token))
            {
                return InvalidToken();
            }

            await SecuritySql.EnsureOpenAsync(database, cancellationToken).ConfigureAwait(false);

            await using var command = SecuritySql.Command(database, ConsumeSql, requireTransaction: false);
            command.Parameters.AddWithValue("token_hash", Hash(token));
            command.Parameters.AddWithValue("kind", kind.ToWire());
            command.Parameters.AddWithValue("now", clock.UtcNow);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            // Zero rows is the single answer for used, expired and unknown (09 §4 validation_failed, 400).
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? reader.GetFieldValue<Guid>(0)
                : InvalidToken();
        }
        catch (Exception exception) when (SecuritySql.IsTransient(exception))
        {
            // 09 README D5: a transient database failure is 503, never an unhandled 500.
            return SecuritySql.Unavailable<Guid>();
        }
    }

    /// <summary>
    /// The one statement of an issue: invalidate the user's live tokens of the flow — scoped to the same
    /// subject, so a plain flow never touches a subject-scoped flow's rows — and insert the new hash.
    /// </summary>
    private async Task<ErrorOr<string>> IssueAsync(
        Guid userId,
        UserActionKind kind,
        Guid? subjectId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A token is always issued for a real user.", nameof(userId));
        }

        try
        {
            await SecuritySql.EnsureOpenAsync(database, cancellationToken).ConfigureAwait(false);

            // The lock of a scope must outlive its update-and-insert, so it needs a transaction. The reset
            // handler runs inside the command's unit of work (09 README D5); the Worker's send-time mint
            // owns none and gets one here, committed together with the insert.
            await using var transaction = database.Database.CurrentTransaction is null
                ? await database.Database
                    .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                    .ConfigureAwait(false)
                : null;

            var now = clock.UtcNow;
            var plaintext = NewToken();

            // Before the update reads its snapshot: without this lock two concurrent issues of the same
            // scope would each see no live row, each update zero rows and each insert one (BD3/WF11).
            await SecuritySql
                .LockActionTokenScopeAsync(database, userId, kind.ToWire(), subjectId, cancellationToken)
                .ConfigureAwait(false);

            await using var command = SecuritySql.Command(database, IssueSql, requireTransaction: true);
            command.Parameters.AddWithValue("id", Guid.CreateVersion7());
            command.Parameters.AddWithValue("user_id", userId);
            command.Parameters.AddWithValue("kind", kind.ToWire());
            command.Parameters.Add(new NpgsqlParameter("subject_id", NpgsqlDbType.Uuid)
            {
                Value = subjectId is { } subject ? subject : DBNull.Value,
            });
            command.Parameters.AddWithValue("token_hash", Hash(plaintext));
            command.Parameters.AddWithValue("created_at", now);
            command.Parameters.AddWithValue("expires_at", expiresAt);
            command.Parameters.AddWithValue("now", now);

            var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (inserted != 1)
            {
                throw new InvalidOperationException(
                    "The action-token insert did not write exactly one row; the caller would otherwise hold a link nobody can spend.");
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return plaintext;
        }
        catch (Exception exception) when (SecuritySql.IsTransient(exception))
        {
            return SecuritySql.Unavailable<string>();
        }
    }

    /// <summary>The lowercase hex SHA-256 of a token — the only form that reaches the database.</summary>
    private static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>32 random bytes in unpadded base64url (BD3's token encoding).</summary>
    private static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));

    /// <summary>base64url without padding, the encoding BD3 names for both the token and its hash input.</summary>
    private static string Base64Url(byte[] bytes) => Convert
        .ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static Error InvalidToken() =>
        Error.Validation(
            code: ErrorCodes.ValidationFailed,
            description: "The link is invalid, already used or expired.",
            metadata: new Dictionary<string, object> { [ErrorMetadata.Field] = "token" });

    // One statement, so the invalidation and the insert can never be split: `IS NOT DISTINCT FROM` makes
    // the same SQL cover a plain flow (subject_id NULL) and a subject-scoped one (06 CR-6/YR8).
    private const string IssueSql =
        """
        WITH invalidated AS (
            UPDATE user_action_tokens
            SET used_at = @now
            WHERE user_id = @user_id
                  AND kind = @kind
                  AND subject_id IS NOT DISTINCT FROM @subject_id
                  AND used_at IS NULL
        )
        INSERT INTO user_action_tokens (id, user_id, kind, subject_id, token_hash, created_at, expires_at, used_at)
        VALUES (@id, @user_id, @kind, @subject_id, @token_hash, @created_at, @expires_at, NULL)
        """;

    // The replay-proof consume: the row is spent and its owner returned in one round trip, so a second
    // caller with the same token updates zero rows and gets the same 400 as an expired or unknown token.
    private const string ConsumeSql =
        """
        UPDATE user_action_tokens
        SET used_at = @now
        WHERE token_hash = @token_hash
              AND kind = @kind
              AND used_at IS NULL
              AND expires_at > @now
        RETURNING user_id
        """;
}
