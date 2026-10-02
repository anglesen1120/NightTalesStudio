using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The Crockford base32 alphabet of 09 README BD4: the recovery codes' alphabet, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Crockford's alphabet is <c>0123456789ABCDEFGHJKMNPQRSTVWXYZ</c> — no <c>I</c>, <c>L</c>, <c>O</c> or
/// <c>U</c>, so a code read aloud or typed by hand cannot be confused with <c>1</c>, <c>0</c> or a
/// profanity. The authenticator secret uses RFC 4648 base32 instead (<see cref="Base32"/>): the two
/// alphabets assign different values to the same characters, so a secret encoded with one would decode to
/// a different key under the other, and every authenticator application implements RFC 4648.
/// </para>
/// </remarks>
public static class CrockfordBase32
{
    /// <summary>Crockford's alphabet, in its canonical order (09 README BD4).</summary>
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>
    /// <paramref name="characters"/> characters, each drawn independently from the alphabet by
    /// <see cref="RandomNumberGenerator.GetInt32(int)"/> — five unbiased bits per character.
    /// </summary>
    /// <param name="characters">How many characters to draw (BD4's recovery code: 10 → 50 bits).</param>
    public static string NewCode(int characters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(characters);

        var builder = new StringBuilder(characters);
        for (var index = 0; index < characters; index++)
        {
            builder.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }

        return builder.ToString();
    }
}

/// <summary>
/// The single-use recovery codes of 09 README BD4: ten codes of ten Crockford characters (50 bits each),
/// stored as the <c>;</c>-joined SHA-256 hex list in the one <c>recovery_codes</c> row of Identity's
/// <c>user_tokens</c>.
/// </summary>
/// <remarks>
/// <para>
/// The row is the unit of replacement, which is why the whole set lives in one <c>value</c>: regenerating
/// swaps it atomically, and redeeming takes the row <c>FOR UPDATE</c> so two simultaneous redemptions of
/// the same code cannot both win — the loser sees the set without that hash.
/// </para>
/// <para>
/// Nothing is ever stored in plaintext: a code exists in plaintext only in the response that issued it
/// (09 README BD16).
/// </para>
/// </remarks>
/// <param name="database">The scope's write model; the caller's transaction holds the row lock.</param>
public sealed class CrockfordBase32RecoveryCodes(AppDbContext database) : IRecoveryCodeService
{
    /// <summary>The number of codes one regeneration issues (BD4: 10).</summary>
    public const int CodeCount = 10;

    /// <summary>The characters per code (BD4: 10 → 50 bits).</summary>
    public const int CodeLength = 10;

    /// <inheritdoc />
    public async Task<ErrorOr<IReadOnlyList<string>>> RegenerateAsync(Guid userId, CancellationToken ct)
    {
        var codes = new List<string>(CodeCount);
        for (var index = 0; index < CodeCount; index++)
        {
            codes.Add(CrockfordBase32.NewCode(CodeLength));
        }

        try
        {
            await SecuritySql.EnsureOpenAsync(database, ct).ConfigureAwait(false);

            // Lock the current row first (when there is one) and only then replace it: a redemption that
            // is in flight either sees the old set or the new one, never a set being written.
            _ = await ReadForUpdateAsync(userId, ct).ConfigureAwait(false);

            await using var write = SecuritySql.Command(database, UpsertSql, requireTransaction: true);
            write.Parameters.AddWithValue("user_id", userId);
            write.Parameters.AddWithValue("login_provider", DataProtectionTotpSecretStore.LoginProvider);
            write.Parameters.AddWithValue("name", DataProtectionTotpSecretStore.RecoveryCodesName);
            write.Parameters.AddWithValue("value", Join(codes.Select(Hash)));

            _ = await write.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            return codes;
        }
        catch (Exception exception) when (SecuritySql.IsTransient(exception))
        {
            return SecuritySql.Unavailable<IReadOnlyList<string>>();
        }
    }

    /// <inheritdoc />
    public async Task<ErrorOr<Success>> RedeemAsync(Guid userId, string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return MfaErrors.InvalidMfaCode("The recovery code is not valid.");
        }

        var presented = Hash(Normalize(code));

        try
        {
            var stored = await ReadForUpdateAsync(userId, ct).ConfigureAwait(false);
            if (stored is null)
            {
                return MfaErrors.InvalidMfaCode("The recovery code is not valid.");
            }

            var hashes = Split(stored);

            // Both sides are encoded once, outside the loop: doing it per iteration adds no constant-time
            // property and allocates on every redemption. Every stored hash is still compared, with no
            // early exit, so the loop's cost must not say which code matched (BD16).
            var presentedBytes = Encoding.UTF8.GetBytes(presented);
            var storedBytes = new byte[hashes.Count][];
            for (var index = 0; index < hashes.Count; index++)
            {
                storedBytes[index] = Encoding.UTF8.GetBytes(hashes[index]);
            }

            var matched = -1;
            for (var index = 0; index < storedBytes.Length; index++)
            {
                if (CryptographicOperations.FixedTimeEquals(presentedBytes, storedBytes[index]))
                {
                    matched = index;
                }
            }

            if (matched < 0)
            {
                return MfaErrors.InvalidMfaCode("The recovery code is not valid.");
            }

            await using var write = SecuritySql.Command(database, UpdateSql, requireTransaction: true);
            write.Parameters.AddWithValue("user_id", userId);
            write.Parameters.AddWithValue("login_provider", DataProtectionTotpSecretStore.LoginProvider);
            write.Parameters.AddWithValue("name", DataProtectionTotpSecretStore.RecoveryCodesName);
            write.Parameters.AddWithValue(
                "value",
                Join(hashes.Where((_, index) => index != matched)));

            _ = await write.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            return Result.Success;
        }
        catch (Exception exception) when (SecuritySql.IsTransient(exception))
        {
            return SecuritySql.Unavailable<Success>();
        }
    }

    /// <inheritdoc />
    public async Task<int> CountRemainingAsync(Guid userId, CancellationToken ct)
    {
        await SecuritySql.EnsureOpenAsync(database, ct).ConfigureAwait(false);

        await using var command = SecuritySql.Command(database, SelectSql, requireTransaction: false);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("login_provider", DataProtectionTotpSecretStore.LoginProvider);
        command.Parameters.AddWithValue("name", DataProtectionTotpSecretStore.RecoveryCodesName);

        var stored = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;

        return stored is null ? 0 : Split(stored).Count;
    }

    /// <summary>The stored hash list of the user's row, locked for the rest of the transaction.</summary>
    private async Task<string?> ReadForUpdateAsync(Guid userId, CancellationToken ct)
    {
        await using var command = SecuritySql.Command(database, SelectForUpdateSql, requireTransaction: true);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("login_provider", DataProtectionTotpSecretStore.LoginProvider);
        command.Parameters.AddWithValue("name", DataProtectionTotpSecretStore.RecoveryCodesName);

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    /// <summary>The stored form of one code: its SHA-256 as lowercase hex (BD16).</summary>
    private static string Hash(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    /// <summary>
    /// The submitted code, as the alphabet spells it: trimmed, upper-cased and without the separators a
    /// user may copy from a password manager.
    /// </summary>
    private static string Normalize(string code) =>
        code.Trim().Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

    private static List<string> Split(string stored) =>
        [.. stored.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static string Join(IEnumerable<string> hashes) => string.Join(';', hashes);

    private const string SelectSql =
        """
        SELECT value
        FROM user_tokens
        WHERE user_id = @user_id AND login_provider = @login_provider AND name = @name
        """;

    private const string SelectForUpdateSql =
        """
        SELECT value
        FROM user_tokens
        WHERE user_id = @user_id AND login_provider = @login_provider AND name = @name
        FOR UPDATE
        """;

    private const string UpdateSql =
        """
        UPDATE user_tokens
        SET value = @value
        WHERE user_id = @user_id AND login_provider = @login_provider AND name = @name
        """;

    // The PK of user_tokens is (user_id, login_provider, name), so the upsert is one statement and the
    // row can never be duplicated by two concurrent regenerations.
    private const string UpsertSql =
        """
        INSERT INTO user_tokens (user_id, login_provider, name, value)
        VALUES (@user_id, @login_provider, @name, @value)
        ON CONFLICT (user_id, login_provider, name) DO UPDATE SET value = EXCLUDED.value
        """;
}
