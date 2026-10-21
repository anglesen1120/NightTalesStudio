using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Compliance;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Application.Publishing;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Features.Publishing.Channels.Commands.ConnectYouTube;

/// <summary>
/// Starts one YouTube connect (06 README D2/D3, <c>plan.md</c> §4 UC1): the six gates in their frozen
/// order, the purge of the caller's own finished states, and one inserted <c>oauth_states</c> row whose
/// hashes and protected verifier are the only durable trace of the attempt.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order is the contract.</b> Platform, re-authentication, legal acceptance and client resolution all
/// fail before any write, so a refused connect leaves no row to purge later; the purge then removes only
/// the caller's own <em>finished</em> states, which is what keeps a second call from accumulating rows
/// without ever deleting a state the user is still holding.
/// </para>
/// <para>
/// <b>Three secrets, three stored forms.</b> The state and the browser nonce are stored as lowercase
/// SHA-256 hex (<c>ITokenIssuer.HashToken</c>'s form; see <see cref="HubTicketSecret.HashOf"/>, the
/// Application-reachable implementation of it) and the PKCE verifier under the token protector's
/// <c>oauth.v1</c> purpose. None of the three plaintexts reaches the row, a log line or the response body;
/// the response carries the URL and the nonce for the endpoint's cookie, nothing else.
/// </para>
/// <para>
/// <b>PKCE is configuration.</b> With <c>YouTube:Pkce=false</c> (the F12 fallback) the authorization URL
/// carries no <c>code_challenge</c>/<c>code_challenge_method</c> and the callback sends no verifier. The
/// verifier is still minted and protected, because <c>oauth_states.code_verifier_protected</c> is
/// <c>NOT NULL</c> and the domain property is non-nullable: the column cannot be omitted, and a column
/// value that is never sent is harmless (task-file GAP, recorded in the task Notes).
/// </para>
/// </remarks>
/// <param name="reauth">The re-authentication gate: a connect within five minutes of a fresh proof of identity.</param>
/// <param name="legal">The legal-acceptance gate; its error is returned untouched (409, <c>errors.documents</c>).</param>
/// <param name="currentUser">The caller: their id is the row's owner, their <c>sid</c> the bound session family.</param>
/// <param name="resolver">Supplies the active client id and its secret; a failure is returned untouched.</param>
/// <param name="database">The tracked write model; the row insert joins the command's transaction.</param>
/// <param name="protector">Protects the PKCE verifier under the <c>oauth.v1</c> purpose.</param>
/// <param name="clock">The scope's clock; one instant is read for the row's window.</param>
/// <param name="options">The bound <c>YouTube</c> section: the redirect URI, the state lifetime and the PKCE flag.</param>
/// <param name="youTube">Builds the authorization URL; it makes no network call.</param>
/// <param name="logger">Logs one line per connect with ids only.</param>
public sealed partial class ConnectYouTubeHandler(
    IReauthGuard reauth,
    ILegalAcceptance legal,
    ICurrentUser currentUser,
    IOAuthClientResolver resolver,
    IAppDbContext database,
    ITokenProtector protector,
    IClock clock,
    YouTubeOptions options,
    IYouTubeClient youTube,
    ILogger<ConnectYouTubeHandler> logger)
    : ICommandHandler<ConnectYouTubeCommand, ErrorOr<ConnectYouTubeResponse>>
{
    /// <summary>The token protector's purpose for the PKCE verifier (<c>tokens.v1/oauth.v1</c>, 06 README A4).</summary>
    public const string OAuthPurpose = "oauth.v1";

    /// <summary>How recent the caller's re-authentication must be (09 README BD4, <c>plan.md</c> §3.1).</summary>
    public static readonly TimeSpan ReauthWindow = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public async ValueTask<ErrorOr<ConnectYouTubeResponse>> Handle(
        ConnectYouTubeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // 1. The platform is a route segment, so an unserved one is a 404 (06 README D3) and never a 400
        //    that would tell a probe which platforms exist.
        if (!PlatformExtensions.TryParse(command.Platform, out var platform))
        {
            return Errors.NotFound();
        }

        // 2. A fresh proof of identity (401 reauth_required / session_revoked), unchanged.
        var recent = await reauth.RequireRecentAsync(ReauthWindow, cancellationToken).ConfigureAwait(false);
        if (recent.IsError)
        {
            return recent.Errors;
        }

        // The authorization behaviour already refused an anonymous caller and step 2 refused a dead
        // family, so the id and the family are set; a missing one is a wiring error of the route, not a
        // request outcome, and it must fail closed rather than mint an unbound state.
        if (currentUser.Id is not { } userId || currentUser.SessionId is not { } familyId)
        {
            return Error.Unauthorized(
                ErrorCodes.ReauthRequired,
                "A recent re-authentication of a live session is required.");
        }

        // 3. A current legal acceptance (409 legal_acceptance_required with errors.documents), untouched.
        var accepted = await legal.EnsureCurrentAsync(userId, cancellationToken).ConfigureAwait(false);
        if (accepted.IsError)
        {
            return accepted.Errors;
        }

        // 4. The client this connect pins. A missing secret is 503 here and never a row.
        var client = await resolver.ResolveAsync(resolver.ActiveClientId, cancellationToken).ConfigureAwait(false);
        if (client.IsError)
        {
            return client.Errors;
        }

        var now = clock.UtcNow;

        // 5. This user's own finished states only: a live state the caller is still holding survives, so a
        //    second connect cannot invalidate the URL of the first (06.1.2-T1 task §Tests).
        await database.OAuthStates
            .Where(state => state.UserId == userId && (state.UsedAt != null || state.ExpiresAt <= now))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        // 6. Three independent 256-bit values. Only their hashes/protected form is stored.
        var state = HubTicketSecret.NewTicket();
        var verifier = HubTicketSecret.NewTicket();
        var nonce = HubTicketSecret.NewTicket();

        var row = OAuthState.Create(
            userId,
            platform,
            HubTicketSecret.HashOf(state),
            protector.Protect(OAuthPurpose, verifier),
            HubTicketSecret.HashOf(nonce),
            client.Value.ClientId,
            familyId,
            now,
            TimeSpan.FromMinutes(options.OAuthStateMinutes));

        // 7. The insert joins the command's transaction: the pipeline saves and commits it once.
        database.OAuthStates.Add(row);

        // 8. The URL the browser is sent to. With Pkce=false the two PKCE parameters are omitted.
        var authorizationUrl = youTube.BuildAuthorizationUrl(
            client.Value,
            new OAuthAuthorizationRequest(options.RedirectUri, options.Pkce),
            state,
            options.Pkce ? PkceChallenge(verifier) : null);

        LogConnectStarted(userId, row.OAuthClientId, row.ExpiresAt);

        // 9. The endpoint turns the nonce into the __Host-nts_oauth cookie and drops it from the body.
        return new ConnectYouTubeResponse(authorizationUrl, row.ExpiresAt, nonce);
    }

    /// <summary>
    /// The RFC 7636 S256 challenge of <paramref name="verifier"/>: unpadded base64url over its SHA-256
    /// digest. Base64url never pads, which is what Google expects (F12).
    /// </summary>
    /// <param name="verifier">The 43-character verifier minted for this attempt.</param>
    /// <returns>The 43-character challenge that travels in the authorization URL.</returns>
    private static string PkceChallenge(string verifier) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    [LoggerMessage(
        EventId = 6380,
        Level = LogLevel.Information,
        Message = "A YouTube connect was started for user {UserId} with client {OAuthClientId}; "
            + "the state expires at {ExpiresAt}.")]
    private partial void LogConnectStarted(Guid userId, string oauthClientId, DateTimeOffset expiresAt);
}
