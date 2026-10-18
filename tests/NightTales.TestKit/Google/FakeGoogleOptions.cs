using System.Net;
using NightTales.Application.Abstractions.Publishing;

namespace NightTales.TestKit.Google;

/// <summary>
/// What <see cref="FakeGoogleServer"/> answers and which OAuth clients it knows (README D19): the two
/// client identities a test can pin a state row to, the channel <c>channels.list</c> returns, and the
/// failure switches (<c>invalid_grant</c>/<c>invalid_client</c>/<c>unauthorized_client</c>, a token
/// endpoint 500, a channels status, a missing refresh token).
/// </summary>
/// <remarks>
/// The values are deliberately low-entropy canaries: a real client id or secret never appears in git
/// (gitleaks), and a test that leaks one proves it by searching a captured sink for the canary
/// (<c>ConnectChannelTests.Callback_NoTokensOrCodeInLogs</c>). The class is mutable because a test sets
/// its switches after the server started and before the request under test.
/// </remarks>
public sealed class FakeGoogleOptions
{
    /// <summary>The client the host's <c>YouTube:ActiveClientId</c> is pointed at.</summary>
    public string ActiveClientId { get; set; } = "canary-oauth-client-primary";

    /// <summary>The <see cref="ActiveClientId"/> client's secret, resolved through the secret port.</summary>
    public string ActiveClientSecret { get; set; } = "canary-oauth-primary-aaaa";

    /// <summary>A second client, so a test can prove the exchange uses the client pinned to the state row.</summary>
    public string SecondClientId { get; set; } = "canary-oauth-client-secondary";

    /// <summary>The <see cref="SecondClientId"/> client's secret.</summary>
    public string SecondClientSecret { get; set; } = "canary-oauth-secondary-bbbb";

    /// <summary>The channel <c>channels.list</c> answers with.</summary>
    public GoogleChannel Channel { get; set; } = new("canary-channel-one", "NightTales Canary Channel");

    /// <summary>
    /// How many channels <c>channels.list</c> returns: 0 is the "the exchange worked but the account has
    /// no channel" case (06.1.2-T1 failure handling: <c>oauth_exchange_failed</c>), 2 is the
    /// Brand-Account case (first one wins, the handler logs).
    /// </summary>
    public int ChannelCount { get; set; } = 1;

    /// <summary>The status <c>channels.list</c> answers with; not OK answers Google's error body.</summary>
    public HttpStatusCode ChannelStatusCode { get; set; } = HttpStatusCode.OK;

    /// <summary>
    /// The status the token endpoint answers with when <see cref="TokenError"/> is set. The switch only
    /// applies with an error body, so a bare server failure is <see cref="TokenStatusCode"/> = 500 plus a
    /// non-OAuth error value; with <see cref="TokenError"/> null a real exchange runs.
    /// </summary>
    public HttpStatusCode TokenStatusCode { get; set; } = HttpStatusCode.BadRequest;

    /// <summary>
    /// When set, every <c>POST /token</c> answers this OAuth error instead of tokens:
    /// <c>invalid_grant</c>, <c>invalid_client</c> or <c>unauthorized_client</c>. Leave null for a real
    /// exchange; set it with <see cref="TokenStatusCode"/> = 500 (and this null) for a server failure.
    /// </summary>
    public string? TokenError { get; set; }

    /// <summary>When true, a successful exchange omits <c>refresh_token</c> (a silent prior grant).</summary>
    public bool OmitRefreshToken { get; set; }

    /// <summary>
    /// When set, the <c>scope</c> every token response names, instead of the scope the authorization request
    /// asked for — Google's granular consent. Set it to a subset (the upload scope only) for a partial grant,
    /// or to <see cref="string.Empty"/> for a response that granted nothing; F9's comparison treats both as a
    /// refusal, and an empty value is exactly the case the client no longer papers over with the request's
    /// own scope list.
    /// </summary>
    public string? GrantedScope { get; set; }

    /// <summary>The secret of <paramref name="clientId"/>, or null when the fake does not know the client.</summary>
    /// <param name="clientId">The client id sent to the token endpoint.</param>
    public string? SecretFor(string? clientId) => clientId switch
    {
        null => null,
        var id when string.Equals(id, ActiveClientId, StringComparison.Ordinal) => ActiveClientSecret,
        var id when string.Equals(id, SecondClientId, StringComparison.Ordinal) => SecondClientSecret,
        _ => null,
    };
}
