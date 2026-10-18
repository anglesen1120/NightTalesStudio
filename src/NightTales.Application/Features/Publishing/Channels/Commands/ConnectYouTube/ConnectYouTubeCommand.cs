using System.Globalization;
using System.Text;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Messaging;

namespace NightTales.Application.Features.Publishing.Channels.Commands.ConnectYouTube;

/// <summary>
/// <c>POST /api/v1/channels/{platform}/connect</c> (06 README D2/D3): mints one single-use state, PKCE
/// verifier and browser nonce, stores only their hashes/protected forms and answers Google's
/// authorization URL.
/// </summary>
/// <remarks>
/// <para>
/// It is <see cref="NotAuditedAttribute"/> on purpose: a connect writes no <c>audit_logs</c> row. That
/// connects nothing and the only durable trace of the attempt is the <c>oauth_states</c> row itself; the
/// audit row of this flow belongs to the callback, which records <c>publishing.channel_connected</c> once
/// a channel really changes (06 README D17, 06.1.2-T1 task §Handlers).
/// </para>
/// <para>
/// The route is authenticated (there is no <c>AllowAnonymousRequest</c> here): the caller's bearer token,
/// a re-authentication within five minutes and a current legal acceptance are the three gates of
/// <c>plan.md</c> §4 UC1, in that order, and the connect binds the caller's session family so the
/// callback can refuse a session that has since died.
/// </para>
/// </remarks>
/// <param name="Platform">The route's platform segment, parsed by the handler (06 README D3).</param>
[NotAudited(
    "A connect changes no channel: it stores one oauth_states row and hands back the authorization URL. "
    + "The callback writes publishing.channel_connected, and only once a channel is really connected.")]
public sealed record ConnectYouTubeCommand(string Platform) : ICommand<ErrorOr<ConnectYouTubeResponse>>;

/// <summary>
/// The connect response of 06 README D3: where to send the browser, when the state stops being usable, and
/// the browser nonce the endpoint turns into the <c>__Host-nts_oauth</c> cookie.
/// </summary>
/// <remarks>
/// <see cref="BrowserNonce"/> is deliberately not part of the wire body: the endpoint reads it into a
/// <c>Set-Cookie</c> header and serialises only <c>authorizationUrl</c> and <c>expiresAt</c>, so the nonce
/// never reaches a body, a log or a <c>Location</c> (06 README D3, YR10).
/// </remarks>
/// <param name="AuthorizationUrl">The absolute Google authorization URL.</param>
/// <param name="ExpiresAt">When the state stops being usable (ten minutes by default).</param>
/// <param name="BrowserNonce">The plaintext browser nonce; the row keeps its SHA-256 hex only.</param>
public sealed record ConnectYouTubeResponse(string AuthorizationUrl, DateTimeOffset ExpiresAt, string BrowserNonce)
{
    /// <summary>
    /// Prints the expiry only (R8/F4): the compiler-generated list would put
    /// <see cref="AuthorizationUrl"/> (the single-use <c>state</c> and the PKCE code challenge) and the
    /// plaintext <see cref="BrowserNonce"/> into every log line, exception message and debugger string
    /// (06 README D3, YR10).
    /// </summary>
    /// <param name="builder">The record's string builder.</param>
    /// <returns><see langword="true"/>, because members were printed.</returns>
    private bool PrintMembers(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Append(CultureInfo.InvariantCulture, $"ExpiresAt = {ExpiresAt}");
        return true;
    }
}
