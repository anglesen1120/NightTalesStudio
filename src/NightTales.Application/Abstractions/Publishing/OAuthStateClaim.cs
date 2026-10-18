using System.Globalization;
using System.Text;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Abstractions.Publishing;

/// <summary>
/// The columns the callback needs after it claimed an <c>oauth_states</c> row (06.1.2-T1, README D3):
/// the values the atomic claim returns, read outside any tracked entity and outside the tenant filter.
/// </summary>
/// <remarks>
/// <see cref="BrowserNonceHash"/> is part of the claim's <c>RETURNING</c> list on purpose: the browser
/// check is the callback's second gate, and re-reading the row for it would be a second statement against
/// a row the same unit of work already knows (06.1.2-T1 deviation, recorded in the task Notes).
/// </remarks>
/// <param name="Id">The claimed row's id.</param>
/// <param name="UserId">The user who started the connect; the callback's tenant and audit subject.</param>
/// <param name="Platform">The platform the connect was started for.</param>
/// <param name="CodeVerifierProtected">The protected PKCE verifier, read back through the token protector.</param>
/// <param name="OAuthClientId">The client the connect pinned; the exchange must use this one.</param>
/// <param name="RefreshFamilyId">The session family the connect was started from.</param>
/// <param name="BrowserNonceHash">The stored hash of the <c>__Host-nts_oauth</c> cookie value.</param>
public sealed record OAuthStateClaim(
    Guid Id,
    Guid UserId,
    Platform Platform,
    string CodeVerifierProtected,
    string OAuthClientId,
    Guid RefreshFamilyId,
    string BrowserNonceHash)
{
    /// <summary>
    /// Prints the ids and the platform only (R8/F4): the compiler-generated list would put the protected
    /// PKCE verifier and the browser-nonce hash into every log line, exception message and debugger string
    /// (06.1.2-T1).
    /// </summary>
    /// <param name="builder">The record's string builder.</param>
    /// <returns><see langword="true"/>, because members were printed.</returns>
    private bool PrintMembers(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Append(
            CultureInfo.InvariantCulture,
            $"Id = {Id}, UserId = {UserId}, Platform = {Platform}, OAuthClientId = {OAuthClientId}, RefreshFamilyId = {RefreshFamilyId}");
        return true;
    }
}
