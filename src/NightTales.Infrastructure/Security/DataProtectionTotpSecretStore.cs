using System.Security.Cryptography;
using ErrorOr;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The authenticator secrets of 09 README BD4, Data-Protection-protected, in Identity's
/// <c>user_tokens</c>.
/// </summary>
/// <remarks>
/// <para>
/// The protector is created <em>directly</em> from <see cref="IDataProtectionProvider"/> under the
/// purpose <c>identity.totp.v1</c>, never through <c>ITokenProtector</c> ([RULED — README §B10 WT43]):
/// that port's root purpose is <c>tokens.v1/</c>, and a TOTP secret must not share a purpose root with a
/// channel connection's token — the two would become readable under one another.
/// </para>
/// <para>
/// Rows are the shape Identity's own store uses, so no new table and no migration is needed: the
/// provider is <c>[AspNetUserStore]</c> and the names are <see cref="ActiveSecretName"/> (verified),
/// <see cref="PendingSecretName"/> [INFERENCE] (enrolment started, code not yet confirmed) and
/// <see cref="RecoveryCodesName"/> (owned by <c>CrockfordBase32RecoveryCodes</c>).
/// </para>
/// <para>
/// Every statement runs inside the caller's transaction (09 README D5), so the secret and the
/// <c>two_factor_enabled</c> flag the flow writes beside it commit together.
/// </para>
/// </remarks>
/// <param name="database">The scope's write model.</param>
/// <param name="protection">The host's key ring (09.9.4-T1, WF15).</param>
public sealed class DataProtectionTotpSecretStore(
    AppDbContext database,
    IDataProtectionProvider protection) : ITotpSecretStore
{
    /// <summary>The Data-Protection purpose of every authenticator secret (BD4, WT43).</summary>
    public const string Purpose = "identity.totp.v1";

    /// <summary>The <c>login_provider</c> Identity's own store writes (BD4).</summary>
    public const string LoginProvider = "[AspNetUserStore]";

    /// <summary>The name of the verified secret (BD4).</summary>
    public const string ActiveSecretName = "totp_secret";

    /// <summary>The name of the unverified enrolment secret [INFERENCE].</summary>
    public const string PendingSecretName = "totp_secret_pending";

    /// <summary>The name of the recovery-code row (BD4).</summary>
    public const string RecoveryCodesName = "recovery_codes";

    private readonly IDataProtector _protector = protection.CreateProtector(Purpose);

    /// <inheritdoc />
    public Task<ErrorOr<string?>> GetActiveSecretAsync(Guid userId, CancellationToken ct) =>
        ReadSecretAsync(userId, ActiveSecretName, ct);

    /// <inheritdoc />
    public Task<ErrorOr<string?>> GetPendingSecretAsync(Guid userId, CancellationToken ct) =>
        ReadSecretAsync(userId, PendingSecretName, ct);

    /// <inheritdoc />
    public async Task<ErrorOr<Success>> SetPendingSecretAsync(
        Guid userId,
        string base32Secret,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base32Secret);

        string cipher;
        try
        {
            cipher = _protector.Protect(base32Secret);
        }
        catch (CryptographicException)
        {
            // WF15: a key ring that cannot protect is a 503, never a stored plaintext secret.
            return KeyRingUnavailable<Success>();
        }

        try
        {
            await SecuritySql.EnsureOpenAsync(database, ct).ConfigureAwait(false);

            await using var command = SecuritySql.Command(database, UpsertSql, requireTransaction: true);
            Bind(command, userId, PendingSecretName);
            command.Parameters.AddWithValue("value", cipher);

            _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            return Result.Success;
        }
        catch (Exception exception) when (SecuritySql.IsTransient(exception))
        {
            return SecuritySql.Unavailable<Success>();
        }
    }

    /// <inheritdoc />
    public async Task<ErrorOr<Success>> PromotePendingSecretAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            await SecuritySql.EnsureOpenAsync(database, ct).ConfigureAwait(false);

            // One statement consumes the pending row and writes exactly the payload it consumed as the
            // active one: a concurrent second promotion deletes 0 rows and therefore activates nothing,
            // so the two cannot both succeed against the same pending secret.
            await using var promote = SecuritySql.Command(database, PromoteSql, requireTransaction: true);
            Bind(promote, userId, PendingSecretName);
            promote.Parameters.AddWithValue("active_name", ActiveSecretName);

            var promoted = await promote.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (promoted == 0)
            {
                // Nothing to promote: the caller verified a pending secret that is not there any more.
                return MfaErrors.InvalidMfaCode("There is no pending authenticator secret to confirm.");
            }

            return Result.Success;
        }
        catch (Exception exception) when (SecuritySql.IsTransient(exception))
        {
            return SecuritySql.Unavailable<Success>();
        }
    }

    /// <inheritdoc />
    public async Task<ErrorOr<Success>> DeleteAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            await SecuritySql.EnsureOpenAsync(database, ct).ConfigureAwait(false);

            await using var command = SecuritySql.Command(database, DeleteAllSql, requireTransaction: true);
            Bind(command, userId, ActiveSecretName);
            command.Parameters.AddWithValue("pending_name", PendingSecretName);
            command.Parameters.AddWithValue("recovery_name", RecoveryCodesName);

            _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            return Result.Success;
        }
        catch (Exception exception) when (SecuritySql.IsTransient(exception))
        {
            return SecuritySql.Unavailable<Success>();
        }
    }

    /// <inheritdoc />
    public async Task<ErrorOr<bool>> IsEnrolledAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            await SecuritySql.EnsureOpenAsync(database, ct).ConfigureAwait(false);

            await using var command = SecuritySql.Command(database, SelectEnrolledSql, requireTransaction: false);
            command.Parameters.Add(new NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = userId });

            var enrolled = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

            return enrolled is true;
        }
        catch (Exception exception) when (SecuritySql.IsTransient(exception))
        {
            return SecuritySql.Unavailable<bool>();
        }
    }

    /// <summary>One secret row, unprotected, or null when the user has none.</summary>
    private async Task<ErrorOr<string?>> ReadSecretAsync(Guid userId, string name, CancellationToken ct)
    {
        string? cipher;
        try
        {
            await SecuritySql.EnsureOpenAsync(database, ct).ConfigureAwait(false);

            await using var command = SecuritySql.Command(database, SelectSql, requireTransaction: false);
            Bind(command, userId, name);

            cipher = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }
        catch (Exception exception) when (SecuritySql.IsTransient(exception))
        {
            return SecuritySql.Unavailable<string?>();
        }

        if (cipher is null)
        {
            // The cast picks the TValue conversion: a bare `null` is ambiguous between it and `Error`.
            return (string?)null;
        }

        try
        {
            return _protector.Unprotect(cipher);
        }
        catch (CryptographicException)
        {
            // A foreign key ring, a tampered row or a purpose change: 401 invalid_mfa_code, and the caller
            // raises the P1 alert (never a silent "MFA is off", 09.10.4-T1 failure handling).
            return MfaErrors.InvalidMfaCode("The stored authenticator secret could not be read.");
        }
    }

    /// <summary>Binds the three columns every statement of this store addresses.</summary>
    private static void Bind(NpgsqlCommand command, Guid userId, string name)
    {
        command.Parameters.Add(new NpgsqlParameter("user_id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = userId });
        command.Parameters.AddWithValue("login_provider", LoginProvider);
        command.Parameters.AddWithValue("name", name);
    }

    /// <summary>The 503 of a key ring that cannot protect (WF15).</summary>
    private static ErrorOr<T> KeyRingUnavailable<T>() =>
        Error.Failure(
            code: ErrorCodes.ServiceUnavailable,
            description: "The Data Protection key ring is unavailable; retry once it is restored.");

    private const string SelectSql =
        """
        SELECT value
        FROM user_tokens
        WHERE user_id = @user_id AND login_provider = @login_provider AND name = @name
        """;

    private const string UpsertSql =
        """
        INSERT INTO user_tokens (user_id, login_provider, name, value)
        VALUES (@user_id, @login_provider, @name, @value)
        ON CONFLICT (user_id, login_provider, name) DO UPDATE SET value = EXCLUDED.value
        """;

    // The pending row is consumed by the promotion itself: DELETE … RETURNING yields exactly the payload
    // that was verified, and the INSERT writes that payload as the active secret. Two promotions of the
    // same pending secret cannot both win — the second deletes nothing and inserts nothing.
    private const string PromoteSql =
        """
        WITH consumed AS (
            DELETE FROM user_tokens
            WHERE user_id = @user_id AND login_provider = @login_provider AND name = @name
            RETURNING user_id, login_provider, value
        )
        INSERT INTO user_tokens (user_id, login_provider, name, value)
        SELECT user_id, login_provider, @active_name, value FROM consumed
        ON CONFLICT (user_id, login_provider, name) DO UPDATE SET value = EXCLUDED.value
        """;

    private const string DeleteAllSql =
        """
        DELETE FROM user_tokens
        WHERE user_id = @user_id AND login_provider = @login_provider
              AND name IN (@name, @pending_name, @recovery_name)
        """;

    private const string SelectEnrolledSql =
        """
        SELECT two_factor_enabled
        FROM users
        WHERE id = @id
        """;
}
