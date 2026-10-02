using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ErrorOr;
using Microsoft.Extensions.Options;
using Npgsql;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The RFC 6238 algorithm of 09 README BD4, as pure functions: HMAC-SHA1, a fixed step, six digits and a
/// ± one-step window.
/// </summary>
/// <remarks>
/// <para>
/// The functions are pure on purpose: the RFC's published vectors, the window and the dynamic truncation
/// are unit-testable without a database, while <see cref="Rfc6238TotpVerifier"/> adds the storage and the
/// replay-proof compare-and-set on top.
/// </para>
/// <para>
/// The window absorbs the clock skew of a phone (± 30 s at the default step) without widening the replay
/// protection: the step a code was accepted for is what <c>users.last_totp_step</c> records, so the same
/// code cannot be presented twice even inside the window.
/// </para>
/// </remarks>
public static class Rfc6238
{
    /// <summary>The digits of a code (BD4: 6).</summary>
    public const int Digits = 6;

    /// <summary>The step RFC 6238 and BD4 fix (30 s).</summary>
    public const int DefaultStepSeconds = 30;

    /// <summary>The candidate steps, nearest first: the current one, then its two neighbours.</summary>
    public static readonly int[] WindowOffsets = [0, -1, 1];

    /// <summary>The step <paramref name="now"/> belongs to.</summary>
    /// <param name="now">The clock reading.</param>
    /// <param name="stepSeconds">The configured step.</param>
    public static long StepAt(DateTimeOffset now, int stepSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepSeconds);

        return now.ToUnixTimeSeconds() / stepSeconds;
    }

    /// <summary>The six-digit code of one step, zero-padded.</summary>
    /// <param name="key">The shared secret; never logged.</param>
    /// <param name="step">The step to compute.</param>
    /// <param name="digits">How many digits the code carries.</param>
    public static string Compute(ReadOnlySpan<byte> key, long step, int digits = Digits)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(digits);

        Span<char> destination = stackalloc char[MaxDigits];
        WriteCode(key, step, digits, destination[..digits]);

        return new string(destination[..digits]);
    }

    /// <summary>
    /// Writes the zero-padded code of one step into <paramref name="destination"/>, without allocating.
    /// </summary>
    /// <param name="key">The shared secret; never logged.</param>
    /// <param name="step">The step to compute.</param>
    /// <param name="digits">How many digits the code carries.</param>
    /// <param name="destination">The buffer to fill; must be at least <paramref name="digits"/> long.</param>
    public static void WriteCode(ReadOnlySpan<byte> key, long step, int digits, Span<char> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(digits);
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, digits);

        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);

        Span<byte> hash = stackalloc byte[20];

        // RFC 6238 defines TOTP over HMAC-SHA1, and every authenticator application implements that
        // profile: a stronger HMAC here would refuse every code a user's phone produces. The digest is a
        // one-way MAC over an 8-byte counter with a 160-bit secret — not a collision-sensitive hash — so
        // CA5350's warning does not apply to this use.
#pragma warning disable CA5350 // HMAC-SHA1 is the algorithm RFC 6238 and BD4 fix for TOTP.
        HMACSHA1.HashData(key, counter, hash);
#pragma warning restore CA5350

        // The dynamic truncation of RFC 4226 §5.3: the low nibble of the last byte selects the four bytes
        // of the 31-bit integer, whose top bit is cleared.
        var offset = hash[^1] & 0x0F;
        var truncated = ((hash[offset] & 0x7F) << 24)
            | ((hash[offset + 1] & 0xFF) << 16)
            | ((hash[offset + 2] & 0xFF) << 8)
            | (hash[offset + 3] & 0xFF);

        var modulus = 1;
        for (var index = 0; index < digits; index++)
        {
            modulus *= 10;
        }

        var value = truncated % modulus;
        for (var index = digits - 1; index >= 0; index--)
        {
            destination[index] = (char)('0' + (value % 10));
            value /= 10;
        }
    }

    /// <summary>
    /// Whether <paramref name="code"/> matches one of the candidate steps, compared in constant time.
    /// </summary>
    /// <param name="key">The shared secret; never logged.</param>
    /// <param name="step">The step of <paramref name="now"/>.</param>
    /// <param name="stepSeconds">The configured step, used to reject a step below zero.</param>
    /// <param name="code">The submitted code.</param>
    /// <param name="matchedStep">The step the accepted code belongs to.</param>
    /// <returns><see langword="true"/> when one candidate matches.</returns>
    public static bool TryMatch(
        ReadOnlySpan<byte> key,
        long step,
        int stepSeconds,
        string code,
        out long matchedStep)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepSeconds);
        ArgumentNullException.ThrowIfNull(code);

        matchedStep = 0;

        if (code.Length != Digits)
        {
            // Every candidate is six digits; a code of another length cannot match. The length is not a
            // secret, so failing early here leaks nothing (the comparison below stays constant-time).
            return false;
        }

        var submitted = MemoryMarshal.AsBytes(code.AsSpan());
        Span<char> computed = stackalloc char[Digits];

        foreach (var offset in WindowOffsets)
        {
            var candidate = step + offset;
            if (candidate < 0)
            {
                continue;
            }

            WriteCode(key, candidate, Digits, computed);

            if (CryptographicOperations.FixedTimeEquals(submitted, MemoryMarshal.AsBytes(computed)))
            {
                matchedStep = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>The widest code this algorithm writes: the buffer bound of <see cref="WriteCode"/> callers.</summary>
    private const int MaxDigits = 10;
}

/// <summary>
/// The TOTP verifier of 09 README BD4: it resolves the slot's secret, runs <see cref="Rfc6238"/> and then
/// makes replay impossible with one compare-and-set.
/// </summary>
/// <remarks>
/// <para>
/// The compare-and-set is the whole point (W6): Identity's own
/// <c>VerifyTwoFactorTokenAsync</c> would accept the same code for the rest of its step, so a code that
/// was observed once — by a shoulder, a proxy, a log — would sign a second session in. Here the accepted
/// step is written to <c>users.last_totp_step</c> only when it is newer than the recorded one; a second
/// presentation of the same code finds the row already at that step, updates nothing and is answered
/// 401 <c>invalid_mfa_code</c>.
/// </para>
/// <para>
/// A secret that cannot be read back (a broken key ring, WF15) is answered 401 <c>invalid_mfa_code</c>
/// plus one <see cref="SecurityEventKinds.TotpSecretUnreadable"/> alert — never by silently turning the
/// account's MFA off.
/// </para>
/// </remarks>
/// <param name="database">The scope's write model; the compare-and-set runs in its transaction.</param>
/// <param name="secrets">The store that holds and decrypts the two secrets (WT31).</param>
/// <param name="options">The configured step and issuer.</param>
/// <param name="clock">The scope's clock reading.</param>
/// <param name="alerter">The P1 alert of an unreadable secret (WT25).</param>
public sealed class Rfc6238TotpVerifier(
    AppDbContext database,
    ITotpSecretStore secrets,
    IOptions<MfaOptions> options,
    IClock clock,
    ISecurityAlerter alerter) : ITotpVerifier
{
    /// <inheritdoc />
    public async Task<ErrorOr<long>> VerifyAsync(
        Guid userId,
        string code,
        TotpSlot slot,
        CancellationToken ct)
    {
        var stored = slot is TotpSlot.Pending
            ? await secrets.GetPendingSecretAsync(userId, ct).ConfigureAwait(false)
            : await secrets.GetActiveSecretAsync(userId, ct).ConfigureAwait(false);

        if (stored.IsError)
        {
            // A 401 from the store means the payload exists but cannot be read (a foreign key ring or a
            // tampered row): that is the WF15 P1 case, not "MFA is off".
            if (stored.FirstError.Code == ErrorCodes.InvalidMfaCode)
            {
                await alerter
                    .AlertAsync(
                        new SecurityEvent(
                            SecurityEventKinds.TotpSecretUnreadable,
                            userId,
                            "The stored authenticator secret could not be read."),
                        ct)
                    .ConfigureAwait(false);
            }

            return stored.Errors;
        }

        if (string.IsNullOrWhiteSpace(stored.Value))
        {
            // No row at all — an unconfirmed enrolment, or one whose pending secret is already gone. That
            // is an ordinary "no code can match" answer, not the WF15 condition: nothing exists that could
            // not be read, so no alert is raised (the secret-unreadable alert is for a payload that is
            // there and cannot be opened).
            return Invalid();
        }

        Span<byte> key = stackalloc byte[Base32.SecretBytes];
        if (!Base32.TryDecode(stored.Value, key, out var keyLength))
        {
            await alerter
                .AlertAsync(
                    new SecurityEvent(
                        SecurityEventKinds.TotpSecretUnreadable,
                        userId,
                        "The stored authenticator secret is not an RFC 4648 base32 value."),
                    ct)
                .ConfigureAwait(false);

            return Invalid();
        }

        var stepSeconds = options.Value.Mfa.StepSeconds;
        var step = Rfc6238.StepAt(clock.UtcNow, stepSeconds);

        if (!Rfc6238.TryMatch(key[..keyLength], step, stepSeconds, code, out var matchedStep))
        {
            return Invalid();
        }

        if (slot is TotpSlot.Pending)
        {
            // The pending slot has no replay column of its own, and this task may add none (the Design
            // rules out a migration). It needs none: the only thing a pending code can do is confirm the
            // enrolment, and confirming promotes the secret and deletes the pending row — so a second
            // presentation finds no pending secret and is refused, while a *fresh* pending secret (a new
            // setup) can still be confirmed inside the same step the previous confirmation used.
            return matchedStep;
        }

        try
        {
            await SecuritySql.EnsureOpenAsync(database, ct).ConfigureAwait(false);

            await using var command = SecuritySql.Command(database, CompareAndSetSql, requireTransaction: true);
            command.Parameters.Add(new NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = userId });
            command.Parameters.AddWithValue("step", matchedStep);

            var updated = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // No row updated means the recorded step is already this one or newer: the code was used.
            return updated == 0 ? Invalid() : matchedStep;
        }
        catch (Exception exception) when (SecuritySql.IsTransient(exception))
        {
            return SecuritySql.Unavailable<long>();
        }
    }

    /// <summary>The one answer for a wrong code, an unreadable secret and a replay — no oracle.</summary>
    private static Error Invalid() =>
        MfaErrors.InvalidMfaCode("The authenticator code is not valid.");

    // BD4's compare-and-set, verbatim: the step is written only when it is newer than the recorded one, so
    // a replayed code updates no row and is refused.
    private const string CompareAndSetSql =
        """
        UPDATE users SET last_totp_step = @step, updated_at = now()
        WHERE id = @id AND (last_totp_step IS NULL OR last_totp_step < @step)
        """;
}
