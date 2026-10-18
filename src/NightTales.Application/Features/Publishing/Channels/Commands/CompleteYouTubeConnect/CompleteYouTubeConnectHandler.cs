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
/// records the audit row — the write and its records inside a second unit of work that commits or rolls
/// back as one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two units of work, and the second spans the write only.</b> Unit #1 is the atomic
/// <c>UPDATE … RETURNING</c> claim, committed by the handler before any Google call, so a replayed callback
/// can never claim the row again whatever happens next; a crash between the two units burns the state and
/// creates nothing, which is harmless, while a replayable state is not. The family liveness read is the one
/// database step of that stretch, and it runs <em>inside</em> unit #1, before the commit that releases the
/// scope's connection (<c>EfRefreshTokenService.EnsureConnectionOpenAsync</c> raw-opens it and nothing closes
/// it again, so a read after the commit would pin a pooled connection for the rest of the request). What is
/// left between the units — the verifier unprotect, the client and writer resolution, the code exchange, the
/// scope check, the refresh-token check and the channel read — touches the database not at all, so no pooled
/// connection is held, and no <c>idle in transaction</c> backend exists, across Google's two round trips (P1).
/// Unit #2 is opened immediately before <c>IConnectChannelConnection.ConnectAsync</c> and holds the
/// connection row, the audit row and the connect's outbox and e-mail rows, so the write and its records
/// commit together or vanish together.
/// </para>
/// <para>
/// <b>Only <see cref="ConnectYouTubeOutcome.Connected"/> is a value; every other outcome is an error.</b>
/// The four failures are returned as <c>ErrorOr</c> errors carrying the catalogue's own codes, so
/// <c>UnitOfWorkBehavior</c> rolls unit #2 back instead of committing it (R1/F1) and <c>LoggingBehavior</c>
/// records the failure as a failure rather than a 100 % <c>outcome=ok</c> (R11). Returning them as
/// success-typed values — the earlier shape — made the pipeline <c>CommitAsync</c> whatever the writer had
/// already tracked, and a bare rollback is not an option either: unit #1's commit has closed the
/// transaction the pipeline opened, and <c>EfUnitOfWork.CommitAsync</c> throws when none is open.
/// </para>
/// <para>
/// <b>The writer is resolved before the exchange (F2).</b> A missing
/// <see cref="IConnectChannelConnection"/> — the production Api's state until 06.1.2-T2 ships — fails
/// <em>before</em> Google spends the code, so no live grant is minted that this app can neither store nor
/// revoke.
/// </para>
/// <para>
/// <b>The granted scope must cover the required set (F9).</b> Granular consent lets the user untick
/// <c>youtube.upload</c>; the exchange then succeeds while every later upload would 403. The granted
/// <c>scope</c> is compared as a set against <c>YouTubeOptions.EffectiveScopes</c>, and a missing scope —
/// or a blank granted value, which the client no longer defaults — is a failed exchange.
/// </para>
/// <para>
/// <b><c>YouTube:Pkce</c> and <c>YouTube:RedirectUri</c> are restart-only configuration.</b> The exchange
/// re-reads both at callback time while the client id is pinned to the state row (F19): flipping either key
/// between connect and callback fails the in-flight flow closed (a verifier Google never saw a challenge
/// for, or a <c>redirect_uri</c> mismatch) as <c>oauth_exchange_failed</c>. Operators change them with a
/// restart, never with a live reload (R9/F10).
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
        // A callback that carried no state can never match a 64-hex hash, so the claim is skipped: the junk
        // path then costs one empty commit instead of a pointless UPDATE, and the answer is the same
        // indistinguishable refusal (P5). The route's platform is the claim's second predicate, so a state
        // minted for another platform can never be completed here (F8).
        var claim = command.State is { Length: > 0 } presented
            ? await states
                .ClaimAsync(HubTicketSecret.HashOf(presented), command.Platform, now, cancellationToken)
                .ConfigureAwait(false)
            : null;

        // The session-family liveness read happens INSIDE unit #1 (P1), before the commit that releases this
        // scope's connection. It is the only database step between the claim and the first Google call that
        // runs outside a transaction, and it opens the connection raw
        // (EfRefreshTokenService.EnsureConnectionOpenAsync: it calls OpenAsync and nothing closes it again),
        // so doing it here is what keeps a pooled connection from being pinned across the two Google round
        // trips, which may take up to 2 x YouTube:Http:TimeoutSeconds. It runs only when the request could
        // reach Google at all — a claimed state, this browser's nonce, a code to exchange — and its answer
        // keeps its place in the order of the checks below.
        var nonceMatches = claim is not null && NonceMatches(command.BrowserNonce, claim.BrowserNonceHash);
        var family = claim is not null && nonceMatches && command.Code is { Length: > 0 }
            ? await families.GetReauthStateAsync(claim.RefreshFamilyId, cancellationToken).ConfigureAwait(false)
            : default;

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

        // From here on every failure is an ErrorOr error and no transaction is open: unit #1's commit closed
        // the pipeline's first one and released this scope's connection, and nothing below touches the
        // database again until unit #2 is opened immediately before the write (P1). UnitOfWorkBehavior rolls
        // back only when the response is an error, which is exactly why these answers are errors rather than
        // the success-typed outcomes of the earlier shape (R1/F1/R11).
        if (claim is null)
        {
            return Refuse("state-not-claimed");
        }

        if (!nonceMatches)
        {
            // A connect started in another browser (or in another tab profile) cannot finish here. The state
            // is already claimed, so it stays spent; this is the one failure the design deliberately makes
            // indistinguishable from the others (YR10).
            return Refuse("browser-nonce");
        }

        if (string.Equals(command.Error, AccessDenied, StringComparison.Ordinal))
        {
            return Denied();
        }

        if (command.Code is not { Length: > 0 } code)
        {
            // No code and no refusal: Google answered something this flow does not understand.
            return Refuse("no-code");
        }

        // Read above, inside unit #1, under exactly the `nonceMatches && code present` predicate that guards
        // this line, so it is always the family of a state whose flow can still reach Google.
        if (family.IsError)
        {
            LogFamilyReadFailed(claim.RefreshFamilyId, family.FirstError.Code);

            return ExchangeFailed();
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

            return ExchangeFailed();
        }

        // 06.1.2-T2's writer, resolved lazily: it is not registered until that task lands, and the Api must
        // boot and validate without it while the flag stays off. It is resolved BEFORE the exchange (F2), so
        // a missing writer fails while the authorization code is still unspent and no grant is minted at
        // Google that this app could neither store nor revoke.
        if (services.GetService<IConnectChannelConnection>() is not { } writer)
        {
            LogChannelWriterMissing();

            return ExchangeFailed();
        }

        // The verifier travels only when the flow is PKCE: Google refuses a verifier it never saw a
        // challenge for, and the connect sent the challenge only when YouTube:Pkce was true.
        var tokens = await youTube
            .ExchangeCodeAsync(client.Value, code, options.Pkce ? verifier : null, options.RedirectUri, cancellationToken)
            .ConfigureAwait(false);
        if (tokens.IsError)
        {
            LogExchangeFailed(claim.OAuthClientId, tokens.FirstError.Code);

            return ExchangeFailed();
        }

        // F9: granular consent may grant less than the connect asked for, and a connection stored without a
        // required scope would fail every later call that needs it. A blank granted scope is not granted.
        if (!ScopesGranted(tokens.Value.Scope, options.EffectiveScopes()))
        {
            LogExchangeFailed(claim.OAuthClientId, "scope_not_granted");

            return ExchangeFailed();
        }

        if (tokens.Value.RefreshToken is not { Length: > 0 })
        {
            // Offline access is the point of the connect: without a refresh token the connection would stop
            // working after an hour (06 README D2 failure table).
            LogExchangeFailed(claim.OAuthClientId, "refresh_token_missing");

            return ExchangeFailed();
        }

        var channel = await youTube.GetMyChannelAsync(tokens.Value.AccessToken, cancellationToken)
            .ConfigureAwait(false);
        if (channel.IsError)
        {
            LogChannelReadFailed(channel.FirstError.Code);

            return ExchangeFailed();
        }

        // ---- Unit of work #2: opened here, immediately before the write, so the transaction spans the
        // write and its records only, and the pooled connection is out of the pool for that span alone (P1).
        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

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
            // The writer's own failure table: a refused unique index is the 409 the caller sees, anything
            // else is a failed exchange. The reason label is the refused code only — never a message. The
            // error is what makes the pipeline roll unit #2 back, so whatever the writer tracked before it
            // answered vanishes with the transaction (R1/F1).
            LogConnectWriteRefused(connected.FirstError.Code);

            return MapConnectFailure(connected.Errors);
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
    /// The one indistinguishable refusal: one error code, one log label, no row, no exchange.
    /// </summary>
    /// <param name="reason">A stable internal label for the support log; never part of the response.</param>
    /// <returns>The <c>oauth_state_invalid</c> error the endpoint maps to the expired-or-used redirect.</returns>
    private Error Refuse(string reason)
    {
        LogRefused(reason);

        return Error.Validation(ErrorCodes.OauthStateInvalid, StateInvalidDescription);
    }

    /// <summary>Google's own refusal: the user declined consent, so no code was exchanged.</summary>
    private static Error Denied() => Error.Failure(ErrorCodes.OauthDenied, DeniedDescription);

    /// <summary>Everything Google-side that went wrong, and every local precondition the flow needs.</summary>
    private static Error ExchangeFailed() => Error.Failure(ErrorCodes.OauthExchangeFailed, ExchangeFailedDescription);

    /// <summary>
    /// Whether the cookie the caller presented is the one this state was minted for. The comparison is
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
    /// Whether Google granted every scope the connect asked for (F9): the granted <c>scope</c> is a
    /// space-separated list compared as a set, and a blank or missing value grants nothing — the client no
    /// longer substitutes the requested list for an empty answer.
    /// </summary>
    /// <param name="granted">The token response's <c>scope</c>, or null.</param>
    /// <param name="required">The scopes the connect requested (<c>YouTubeOptions.EffectiveScopes</c>).</param>
    private static bool ScopesGranted(string? granted, IReadOnlyList<string> required)
    {
        if (granted is not { Length: > 0 })
        {
            return false;
        }

        var grantedSet = granted
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        return required.All(grantedSet.Contains);
    }

    /// <summary>
    /// The writer's refusal as the error the endpoint maps back to an outcome: a refused
    /// <c>ux_channel_connections_account</c> is the already-connected 409; every other refusal is a failed
    /// exchange.
    /// </summary>
    /// <param name="errors">The errors the channel writer answered.</param>
    private static Error MapConnectFailure(List<Error> errors) =>
        errors.Any(error => string.Equals(error.Code, ErrorCodes.ChannelAlreadyConnected, StringComparison.Ordinal))
            ? Error.Conflict(ErrorCodes.ChannelAlreadyConnected, ChannelAlreadyConnectedDescription)
            : Error.Failure(ErrorCodes.OauthExchangeFailed, ExchangeFailedDescription);

    /// <summary>The fixed, client-never-sees-it description of the indistinguishable refusal.</summary>
    private const string StateInvalidDescription =
        "The OAuth state is unknown, spent, expired or not this browser's.";

    /// <summary>The fixed description of Google's own refusal.</summary>
    private const string DeniedDescription = "The user refused consent at Google.";

    /// <summary>The fixed description of every other flow failure.</summary>
    private const string ExchangeFailedDescription = "The code exchange or the channel read did not complete.";

    /// <summary>The fixed description of the already-connected refusal.</summary>
    private const string ChannelAlreadyConnectedDescription =
        "This account already has a live connection for that channel.";

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
