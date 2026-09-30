namespace NightTales.Infrastructure.Persistence.Entities;

/// <summary>
/// Why a <c>refresh_tokens</c> row stopped being usable (09.3.2-T1 Design, BD3). Stored as snake_case
/// text under a closed <c>CHECK</c> (09 README D8), which is what makes the "was this theft, or just a
/// stale cookie?" question answerable from the row alone ([RULED — README §B10 WT36]).
/// </summary>
/// <remarks>
/// The two families that matter to the rotation algorithm are <see cref="Rotated"/> — which is the only
/// reason a reuse detection fires on — and everything else, which answers 401 <c>session_revoked</c>
/// without an alert.
/// </remarks>
public enum RevocationReason
{
    /// <summary>The row was consumed by a successful rotation; presenting it again is theft (WT36).</summary>
    Rotated,

    /// <summary>Logout revoked the whole family (WF12).</summary>
    Logout,

    /// <summary>A rotated row was presented again: the family is revoked and an alert is sent (BD3, WT36).</summary>
    ReuseDetected,

    /// <summary>The family's absolute lifetime ran out (BD3: 12 h owner/admin, 14 days user).</summary>
    SessionExpired,

    /// <summary>The owner/admin idle window closed (BD3: 30 min).</summary>
    SessionIdle,

    /// <summary>09.3.3-T1's password reset revoked every session.</summary>
    PasswordReset,

    /// <summary>09.10.4-T1's MFA change revoked every session.</summary>
    MfaChange,

    /// <summary>09.10.5-T1's administrative revocation (suspend, force sign-out).</summary>
    AdminRevoke,

    /// <summary>Refresh found the account suspended or deleted (09.3.2-T1 Design step of the re-check).</summary>
    AccountNotActive,
}

/// <summary>
/// The <c>revoked_reason</c> wire vocabulary: the exact snake_case strings the column's <c>CHECK</c>
/// allows, produced and parsed in one place so SQL and the entity cannot drift apart.
/// </summary>
public static class RevocationReasons
{
    /// <summary>The value stored for <paramref name="reason"/> (the same conversion the CHECK used).</summary>
    /// <param name="reason">A revocation reason.</param>
    public static string ToWire(this RevocationReason reason) =>
        Conventions.EnumSnakeCase.Convert(reason.ToString());

    /// <summary>
    /// Reads a stored <c>revoked_reason</c> back. An unknown value is a programming error, not client
    /// input: it means the caller invented a reason the column's CHECK would refuse at write time.
    /// </summary>
    /// <param name="wire">The snake_case value read from the column.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is not one of the enum's wire values.</exception>
    public static RevocationReason Parse(string wire)
    {
        ArgumentNullException.ThrowIfNull(wire);

        foreach (var candidate in Enum.GetValues<RevocationReason>())
        {
            if (string.Equals(candidate.ToWire(), wire, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        throw new ArgumentOutOfRangeException(
            nameof(wire),
            wire,
            $"'{wire}' is not a revoked_reason; the column's CHECK allows only the enum's wire values.");
    }
}
