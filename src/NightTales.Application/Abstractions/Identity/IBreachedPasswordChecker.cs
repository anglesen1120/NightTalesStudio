using ErrorOr;

namespace NightTales.Application.Abstractions.Identity;

/// <summary>
/// How a password is checked against known-breached passwords
/// (<c>Security:BreachedPasswords:Mode</c>, 09 README BD2 and 09.3-auth/plan.md §5).
/// </summary>
public enum BreachedPasswordMode
{
    /// <summary>
    /// k-anonymity range query: only the first 5 hex characters of <c>SHA1(password)</c> leave the
    /// server. 09.3.1-T2 (W3-O3) makes it fail open to the bundled top-list when the provider cannot answer.
    /// </summary>
    Range,

    /// <summary>The bundled top-list resource, matched locally. The P default (no network, no privacy decision).</summary>
    List,

    /// <summary>The check is skipped — an explicit operator choice, never a silent fallback.</summary>
    Off,
}

/// <summary>
/// Answers whether a candidate password appears in a breached-password source (09 README BD2).
/// </summary>
/// <remarks>
/// The password itself is never sent anywhere in <see cref="BreachedPasswordMode.Range"/> mode: the
/// implementation sends the k-anonymity prefix only (09.3-auth plan §9:
/// <c>BreachedPasswordCheckerTests</c> asserts that with a hand-written handler).
/// </remarks>
public interface IBreachedPasswordChecker
{
    /// <summary>
    /// <c>true</c> when the password is known to be breached. An unreachable provider in
    /// <see cref="BreachedPasswordMode.Range"/> returns 503 <c>service_unavailable</c> instead of
    /// accepting a possibly-breached password.
    /// </summary>
    /// <param name="password">The candidate password; never logged.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<bool>> IsBreachedAsync(string password, CancellationToken ct);
}
