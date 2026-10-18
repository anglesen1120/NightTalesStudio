using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Publishing.Channels.Commands.ConnectYouTube;
using NightTales.Application.Publishing;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Features.Publishing.Channels.Commands.CompleteYouTubeConnect;

/// <summary>
/// Finishes one YouTube connect (06 README D3, <c>plan.md</c> §4 UC2): claims the state row in a unit of
/// work of its own, then exchanges the code, reads the channel, hands both to the channel writer and
/// records the audit row — all inside a second unit of work that commits or rolls back as one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two units of work, deliberately.</b> Unit #1 is the atomic <c>UPDATE … RETURNING</c> claim, committed
/// before any Google call, so a replayed callback can never claim the row again whatever happens next; a
/// crash between the two units burns the state and creates nothing, which is harmless, while a replayable
/// state is not. Unit #2 opens its own transaction after that commit and holds the exchange, the
/// connection row, the audit row and the outbox row of the connect, so a failure after the claim leaves the
/// state spent and no other trace. The command therefore answers a success-typed outcome and never an
/// error: an <c>ErrorOr</c> error would roll the whole unit of work back on 09 D5, undoing the claim.
/// </para>
/// <para>
/// <b>No oracle.</b> An unknown state, a spent state, an expired state, a state from another user, a
/// missing cookie, a cookie minted by another connect, a dead session family and an unreadable verifier
/// cipher all answer <see cref="ConnectYouTubeOutcome.StateInvalid"/> with the same empty body. Only what
/// Google itself said (a refusal, a failed exchange, an already-connected channel) gets its own member.
/// </para>
/// <para>
/// <b>Nothing secret is logged.</b> The code, the state, the cookie, the tokens and the client secret never
/// reach a log line; the lines here carry ids and stable reason labels only (06 README D17).
/// </para>
/// </remarks>
/// <param name="unitOfWork">Drives the two boundaries by hand; the pipeline commits unit #2.</param>
/// <param name="states">The claim of <c>oauth_states</c> (raw SQL, unit of work #1).</param>
/// <param name="families">Whether the session family that started the connect still has a live row.</param>
/// <param name="resolver">Resolves the client the state row pinned — never the active id (F19).</param>
/// <param name="youTube">The code exchange and the channel read.</param>
/// <param name="protector">Reads the PKCE verifier back from the <c>oauth.v1</c> purpose.</param>
/// <param name="audit">The one writer of <c>audit_logs</c>; it adds to unit #2's transaction.</param>
/// <param name="clock">The scope's clock; one instant is used for the claim, the connection and the audit row.</param>
/// <param name="services">Resolves the channel writer lazily: 06.1.2-T2 ships it, and the Api must boot without it.</param>
/// <param name="options">The bound <c>YouTube</c> section: the redirect URI and the PKCE flag.</param>
/// <param name="logger">Logs ids and reason labels only.</param>
public sealed partial class CompleteYouTubeConnectHandler(
    IUnitOfWork unitOfWork,
    IOAuthStateStore states,
    ISessionFamilyReader families,
    IOAuthClientResolver resolver,
    IYouTubeClient youTube,
    ITokenProtector protector,
    IAuditWriter audit,
    IClock clock,
    IServiceProvider services,
    YouTubeOptions options,
    ILogger<CompleteYouTubeConnectHandler> logger)
    : ICommandHandler<CompleteYouTubeConnectCommand, ErrorOr<ConnectYouTubeOutcome>>
{
    /// <summary>The frozen audit action of a finished connect (06 README D17).</summary>
    public const string ChannelConnectedAction = "publishing.channel_connected";

    /// <summary>The audit row's entity kind: the connection the callback created or reconnected (09 README BD15).</summary>
    public const string ChannelConnectionEntityType = "channel_connection";

    /// <summary>Google's refusal error of the authorization endpoint (06 README D2 failure table).</summary>
    public const string AccessDenied = "access_denied";

    /// <summary>
    /// The <c>User-Agent</c> ceiling of the hand-written audit writers (the Api's jobs-dashboard writer and
    /// 09.2.6-T3's refresh rows use the same bound); the column is unbounded <c>text</c>, but a client header
    /// must not be able to grow a row without limit.
    /// </summary>
    private const int UserAgentMaxLength = 512;

    private static readonly JsonSerializerOptions DataJson = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async ValueTask<ErrorOr<ConnectYouTubeOutcome>> Handle(
        CompleteYouTubeConnectCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var now = clock.UtcNow;

        // ---- Unit of work #1: the claim, committed alone, before any Google call (YR10). ----
        // A callback with no state at all still travels this path: an empty hash matches no row, so it can
        // never be a second way to look a state up.
        var claim = await states
            .ClaimAsync(HashOfState(command.State), now, cancellationToken)
            .ConfigureAwait(false);

        var claimed = await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (claimed.IsError)
        {
            // Nothing was claimed and nothing is left behind: the state is still spendable, so the user
            // simply restarts the connect. The failure is answered as an envelope error, not as an outcome,
            // because unit #1's rollback may have closed the transaction the pipeline opened: returning a
            // success value here would make the behaviour's own CommitAsync throw
            // (EfUnitOfWork.CommitAsync:108). The endpoint turns any error into the fixed failure redirect.
            LogClaimNotCommitted(claimed.FirstError.Code);

            return claimed.Errors;
        }

        // ---- Unit of work #2: everything the connect writes, committed as one by the pipeline. ----
        // BeginAsync opens a fresh transaction: unit #1 closed the pipeline's first one at its commit.
        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        if (claim is null)
        {
            return Refuse("state-not-claimed");
        }

        if (!NonceMatches(command.BrowserNonce, claim.BrowserNonceHash))
        {
            // A connect started in another browser (or in another tab profile) cannot finish here. The state
            // is already claimed, so it stays spent; this is the one failure the design deliberately makes
            // indistinguishable from the others (YR10).
            return Refuse("browser-nonce");
        }

        if (string.Equals(command.Error, AccessDenied, StringComparison.Ordinal))
        {
            return ConnectYouTubeOutcome.Denied;
        }

        if (command.Code is not { Length: > 0 } code)
        {
            // No code and no refusal: Google answered something this flow does not understand.
            return Refuse("no-code");
        }

        var family = await families.GetReauthStateAsync(claim.RefreshFamilyId, cancellationToken)
            .ConfigureAwait(false);
        if (family.IsError)
        {
            LogFamilyReadFailed(claim.RefreshFamilyId, family.FirstError.Code);

            return ConnectYouTubeOutcome.ExchangeFailed;
        }

        if (family.Value.Revoked)
        {
            // The session that started the connect is gone: the grant must not be adopted by a session the
            // user no longer controls.
            return Refuse("family-revoked");
        }

        if (!protector.TryUnprotect(ConnectYouTubeHandler.OAuthPurpose, claim.CodeVerifierProtected, out var verifier))
        {
            return Refuse("verifier-cipher");
        }

        // The state's client, never the active one: a rotation between the two calls must not break a
        // connect that is already in flight (06 README D5/F19).
        var client = await resolver.ResolveAsync(claim.OAuthClientId, cancellationToken).ConfigureAwait(false);
        if (client.IsError)
        {
            LogClientUnavailable(claim.OAuthClientId, client.FirstError.Code);

            return ConnectYouTubeOutcome.ExchangeFailed;
        }

        // The verifier travels only when the flow is PKCE: Google refuses a verifier it never saw a
        // challenge for, and the connect sent the challenge only when YouTube:Pkce was true.
        var tokens = await youTube
            .ExchangeCodeAsync(client.Value, code, options.Pkce ? verifier : null, options.RedirectUri, cancellationToken)
            .ConfigureAwait(false);
        if (tokens.IsError)
        {
            LogExchangeFailed(claim.OAuthClientId, tokens.FirstError.Code);

            return ConnectYouTubeOutcome.ExchangeFailed;
        }

        if (tokens.Value.RefreshToken is not { Length: > 0 })
        {
            // Offline access is the point of the connect: without a refresh token the connection would stop
            // working after an hour (06 README D2 failure table).
            LogExchangeFailed(claim.OAuthClientId, "refresh_token_missing");

            return ConnectYouTubeOutcome.ExchangeFailed;
        }

        var channel = await youTube.GetMyChannelAsync(tokens.Value.AccessToken, cancellationToken)
            .ConfigureAwait(false);
        if (channel.IsError)
        {
            LogChannelReadFailed(channel.FirstError.Code);

            return ConnectYouTubeOutcome.ExchangeFailed;
        }

        // 06.1.2-T2's writer, resolved lazily: it is not registered until that task lands, and the Api must
        // boot and validate without it while the flag stays off. A missing writer is a failure inside unit
        // #2 (nothing was written), never a 500.
        if (services.GetService<IConnectChannelConnection>() is not { } writer)
        {
            LogChannelWriterMissing();

            return ConnectYouTubeOutcome.ExchangeFailed;
        }

        var connected = await writer
            .ConnectAsync(
                new ConnectChannelConnectionRequest(
                    claim.UserId,
                    claim.Platform,
                    channel.Value,
                    tokens.Value,
                    claim.OAuthClientId,
                    now),
                cancellationToken)
            .ConfigureAwait(false);
        if (connected.IsError)
        {
            var outcome = MapConnectFailure(connected.Errors);

            // The writer's own failure table: a refused unique index is the 409 the caller sees, anything
            // else is a failed exchange. The reason label is the refused code only — never a message.
            LogConnectWriteRefused(connected.FirstError.Code);

            return outcome;
        }

        // Step 10: the one audit row of the flow, written here because the pipeline slot is off for this
        // command and would also fire on the four failure outcomes.
        await audit
            .WriteAsync(
                Entry(command, claim.UserId, connected.Value, claim.Platform.ToWire(), claim.OAuthClientId, channel.Value.Id, now),
                cancellationToken)
            .ConfigureAwait(false);

        LogConnected(claim.UserId, connected.Value, channel.Value.Id, claim.OAuthClientId);

        return ConnectYouTubeOutcome.Connected;
    }

    /// <summary>
    /// The one indistinguishable refusal: one outcome, one log label, no row, no exchange.
    /// </summary>
    /// <param name="reason">A stable internal label for the support log; never part of the response.</param>
    private ConnectYouTubeOutcome Refuse(string reason)
    {
        LogRefused(reason);

        return ConnectYouTubeOutcome.StateInvalid;
    }

    /// <summary>
    /// The stored hash of the presented state, or the empty string when the callback carried none: the
    /// claim's <c>WHERE</c> then matches no row instead of the callback inventing a second lookup path.
    /// </summary>
    private static string HashOfState(string? state) =>
        state is { Length: > 0 } presented ? HubTicketSecret.HashOf(presented) : string.Empty;

    /// <summary>
    /// Whether the presented cookie is the one this state was minted for. The comparison is
    /// constant-time over the two hashes (09 README BD16), and the plaintext cookie never leaves this
    /// method.
    /// </summary>
    /// <param name="presented">The raw <c>__Host-nts_oauth</c> value, or null.</param>
    /// <param name="storedHash">The row's <c>browser_nonce_hash</c>.</param>
    private static bool NonceMatches(string? presented, string storedHash)
    {
        if (presented is not { Length: > 0 })
        {
            return false;
        }

        var presentedHash = HubTicketSecret.HashOf(presented);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(presentedHash),
            Encoding.ASCII.GetBytes(storedHash));
    }

    /// <summary>
    /// The writer's refusal as the endpoint's outcome: a refused <c>ux_channel_connections_account</c> is
    /// the already-connected 409; every other refusal is a failed exchange.
    /// </summary>
    /// <param name="errors">The errors the channel writer answered.</param>
    private static ConnectYouTubeOutcome MapConnectFailure(List<Error> errors) =>
        errors.Any(error => string.Equals(error.Code, ErrorCodes.ChannelAlreadyConnected, StringComparison.Ordinal))
            ? ConnectYouTubeOutcome.ChannelAlreadyConnected
            : ConnectYouTubeOutcome.ExchangeFailed;

    /// <summary>The one audit row of the flow (09 README BD15, 06 README D17).</summary>
    private static AuditEntry Entry(
        CompleteYouTubeConnectCommand command,
        Guid userId,
        Guid connectionId,
        string platform,
        string oauthClientId,
        string externalAccountId,
        DateTimeOffset now) =>
        new(
            ActorUserId: userId,
            ActorType: ActorType.User,
            SubjectUserId: userId,
            Action: ChannelConnectedAction,
            Category: Category.Publishing,
            Visibility: Visibility.Internal,
            EntityType: ChannelConnectionEntityType,
            EntityId: connectionId,
            DataJson: JsonSerializer.Serialize(
                new ChannelConnectedData(platform, oauthClientId, externalAccountId, Reconnected: false),
                DataJson),
            Ip: ParseIp(command.Info.Ip),
            UserAgent: Truncate(command.Info.UserAgent),
            ActorLabel: null,
            OccurredAt: now);

    /// <summary>The caller's address, or null when it is absent or unparsable.</summary>
    private static IPAddress? ParseIp(string? ip) =>
        !string.IsNullOrWhiteSpace(ip) && IPAddress.TryParse(ip, out var parsed) ? parsed : null;

    /// <summary>The caller's bounded <c>User-Agent</c>; a client header may not grow a row without limit.</summary>
    private static string? Truncate(string? userAgent) =>
        string.IsNullOrEmpty(userAgent) ? null
        : userAgent.Length <= UserAgentMaxLength ? userAgent
        : userAgent[..UserAgentMaxLength];

    /// <summary>
    /// The safe <c>data</c> payload of the audit row: the four values 06 README D17 freezes and nothing
    /// else — no token, no code, no state, no secret.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Reconnected</c> is the one value this handler cannot observe: 06.1.2-T2's frozen port answers the
    /// connection id only (<c>ErrorOr&lt;Guid&gt;</c>), so a reconnect and a first connect are
    /// indistinguishable here. It is written as <c>false</c> and recorded as a GAP with its fix: the port's
    /// answer must carry the fact (a <c>ConnectChannelConnectionResult(Guid ConnectionId, bool
    /// Reconnected)</c>) before this field can be truthful.
    /// </para>
    /// <para>
    /// <c>oauthClientId</c> is pinned by name: the camel-case policy in use lowercases only the first
    /// character, so the member <c>OAuthClientId</c> would serialize as <c>oAuthClientId</c> and break the
    /// frozen member list of 06.1.2-T1's audit row.
    /// </para>
    /// </remarks>
    private sealed record ChannelConnectedData(
        string Platform,
        [property: JsonPropertyName("oauthClientId")] string OAuthClientId,
        string ExternalAccountId,
        bool Reconnected);

    [LoggerMessage(
        EventId = 6381,
        Level = LogLevel.Warning,
        Message = "The OAuth state claim of a YouTube callback did not commit ({ErrorCode}); the state stays spendable.")]
    private partial void LogClaimNotCommitted(string errorCode);

    [LoggerMessage(
        EventId = 6382,
        Level = LogLevel.Warning,
        Message = "A YouTube callback was refused before any Google call (reason {Reason}).")]
    private partial void LogRefused(string reason);

    [LoggerMessage(
        EventId = 6383,
        Level = LogLevel.Warning,
        Message = "The session family {FamilyId} of a YouTube callback could not be read ({ErrorCode}).")]
    private partial void LogFamilyReadFailed(Guid familyId, string errorCode);

    [LoggerMessage(
        EventId = 6384,
        Level = LogLevel.Warning,
        Message = "The OAuth client {OAuthClientId} of a YouTube callback is no longer resolvable ({ErrorCode}).")]
    private partial void LogClientUnavailable(string oauthClientId, string errorCode);

    [LoggerMessage(
        EventId = 6385,
        Level = LogLevel.Warning,
        Message = "The code exchange of a YouTube callback failed for client {OAuthClientId} ({ErrorCode}).")]
    private partial void LogExchangeFailed(string oauthClientId, string errorCode);

    [LoggerMessage(
        EventId = 6386,
        Level = LogLevel.Warning,
        Message = "The channel read of a YouTube callback failed ({ErrorCode}).")]
    private partial void LogChannelReadFailed(string errorCode);

    [LoggerMessage(
        EventId = 6387,
        Level = LogLevel.Error,
        Message = "A YouTube callback could not finish: no IConnectChannelConnection is registered (06.1.2-T2 has not shipped).")]
    private partial void LogChannelWriterMissing();

    [LoggerMessage(
        EventId = 6388,
        Level = LogLevel.Warning,
        Message = "The channel write of a YouTube callback was refused ({ErrorCode}).")]
    private partial void LogConnectWriteRefused(string errorCode);

    [LoggerMessage(
        EventId = 6389,
        Level = LogLevel.Information,
        Message = "A YouTube channel was connected for user {UserId}: connection {ConnectionId}, channel {ChannelId}, client {OAuthClientId}.")]
    private partial void LogConnected(Guid userId, Guid connectionId, string channelId, string oauthClientId);
}
