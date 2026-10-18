using System.Globalization;
using System.Text;
using ErrorOr;
using Mediator;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Features.Publishing.Channels.Commands.ConnectChannelConnection;

/// <summary>
/// Writes one channel connection inside the caller's unit of work — the implementation of
/// <see cref="IConnectChannelConnection"/> 06.1.2-T1's callback runs (06.1.2-T2 task §Design steps 1-5, 06
/// README D3/D4/D5/D13/D17/D18/YR11).
/// </summary>
/// <remarks>
/// <para>
/// <b>One unit, and it is the caller's.</b> The row lock, the insert or the reconnect, the queued e-mail and the
/// <c>channel_connection_changed.v1</c> outbox row the aggregate raises all join the transaction the caller
/// already opened (the callback's unit #2, or <c>UnitOfWorkBehavior</c>'s when the command is dispatched
/// through Mediator). This handler performs <b>no</b> <c>SaveChanges</c>, no <c>ExecuteUpdate</c> and no
/// <c>BeginAsync</c>: one <c>SaveChangesAsync</c> writes everything, so a refusal — or the pipeline's rollback
/// of any error answer — leaves no connection, no outbox row and no <c>email_messages</c> row behind (06 CR-22).
/// </para>
/// <para>
/// <b>The order is the failure table's.</b> The locked read decides insert versus reconnect <em>before</em> any
/// token is protected or any e-mail queued, so the losing transaction of a cross-account race never queues mail.
/// The read is <see cref="IChannelConnectionLocker.LockLiveByAccountAsync"/>: one indexed statement, owner-agnostic
/// (the callback is anonymous, so a tenant-filtered read would see nothing at all), under
/// <see cref="ChannelConnectionStatus.Active"/>… i.e. every status but <c>revoked</c>, which is the partial unique
/// index's own predicate. The unique index — not this read — is the authority for "one live owner per channel".
/// </para>
/// <para>
/// <b>Tokens.</b> Both are protected with <see cref="ITokenProtector"/> under the per-connection purpose
/// <c>tokens.v1/{connectionId}</c> (06 README D4) and only the ciphertext reaches the row; a reconnect protects
/// under the <em>existing</em> id (YR11), so ciphertext already stored for that connection keeps its purpose and an
/// in-flight <see cref="ITokenService"/> read is never re-keyed. Nothing here logs, records or e-mails a token,
/// the channel title, the account's e-mail address or the pinned client secret; the two log lines carry ids only.
/// </para>
/// </remarks>
/// <param name="locker">The row lock of <c>channel_connections</c>; joins the caller's transaction.</param>
/// <param name="database">The caller's write model, so the connection row is tracked into the same unit.</param>
/// <param name="protector">Protects both tokens under this connection's own purpose.</param>
/// <param name="email">Queues the <c>channel_connected</c> message; never talks to SMTP (09.2.6-T3).</param>
/// <param name="profiles">The claiming account's display name and UI locale (09 README D16).</param>
/// <param name="logger">The two ids-only lines of this write.</param>
public sealed partial class ConnectChannelConnectionHandler(
    IChannelConnectionLocker locker,
    IAppDbContext database,
    ITokenProtector protector,
    IEmailSender email,
    IUserProfileReader profiles,
    ILogger<ConnectChannelConnectionHandler> logger)
    : ICommandHandler<ConnectChannelConnectionCommand, ErrorOr<ConnectChannelOutcome>>, IConnectChannelConnection
{
    /// <summary>The e-mail template of a successful connect (06 README D7; cataloged by 09.2.6-T3).</summary>
    public const string EmailTemplate = "channel_connected";

    /// <summary>
    /// The static account-security path the e-mail points at. It is a plain path, not a minted link: this
    /// template carries no token (06.1.2-T2 task §"E-mail template").
    /// </summary>
    public const string SecurityLink = "/account/security";

    /// <summary>
    /// What the e-mail says when the scope has no <c>User-Agent</c> at all — a direct Mediator dispatch in a test,
    /// never a browser navigation. A value, not a sentence: the template's own copy wraps it.
    /// </summary>
    public const string UnknownDevice = "unknown device";

    /// <summary>How much of a <c>User-Agent</c> the e-mail keeps before it is cut short.</summary>
    private const int MaxDeviceLength = 120;

    private const string ChannelAlreadyConnectedDescription =
        "This account already has a live connection for that channel.";

    private const string RefreshTokenMissingDescription =
        "The grant carried no refresh token, so the connection could not be stored.";

    private const string AccountMissingDescription =
        "The account this connect belongs to no longer exists.";

    /// <inheritdoc />
    public ValueTask<ErrorOr<ConnectChannelOutcome>> Handle(
        ConnectChannelConnectionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return new ValueTask<ErrorOr<ConnectChannelOutcome>>(ConnectAsync(command.Request, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<ErrorOr<ConnectChannelOutcome>> ConnectAsync(
        ConnectChannelConnectionRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // D2: an `active` row without a refresh token would stop working within the hour, and the token column is
        // nullable only for the revoked state. 06.1.2-T1 rejects this before the exchange is adopted; this is the
        // guard for every other caller of the port.
        var refreshToken = request.Tokens.RefreshToken;
        if (refreshToken is not { Length: > 0 })
        {
            LogRefreshTokenMissing(request.UserId);

            return Error.Failure(ErrorCodes.OauthExchangeFailed, RefreshTokenMissingDescription);
        }

        // The e-mail's recipient and locale come from the account row, not from the command (09 README D16). A
        // deleted account cannot own a live connection: the row would be unreachable and the mail unsendable.
        var profile = await profiles.GetAsync(request.UserId, ct).ConfigureAwait(false);
        if (profile is null)
        {
            LogAccountMissing(request.UserId);

            return Error.Failure(ErrorCodes.OauthExchangeFailed, AccountMissingDescription);
        }

        // Step 1: the locked read decides insert versus reconnect, before a token is written or mail queued. The
        // answer is owner-agnostic: another account's live row for this channel is the already-connected refusal,
        // and it is left exactly as it was (tracked, unchanged, nothing written, nothing logged about its owner).
        var existing = await locker
            .LockLiveByAccountAsync(request.Platform, request.Channel.Id, ct)
            .ConfigureAwait(false);
        if (existing is not null && existing.UserId != request.UserId)
        {
            LogChannelOwnedByAnotherUser(request.UserId);

            return Error.Conflict(ErrorCodes.ChannelAlreadyConnected, ChannelAlreadyConnectedDescription);
        }

        // Step 2: the id first, then the ciphertext — a reconnect protects under the existing id (YR11), so the
        // purpose string of already-stored ciphertext never changes. `Guid.ToString()` is the "D" form both sides
        // of the protector's purpose use.
        var connectionId = existing?.Id ?? Guid.CreateVersion7();
        var purpose = connectionId.ToString();
        var accessTokenEnc = protector.Protect(purpose, request.Tokens.AccessToken);
        var refreshTokenEnc = protector.Protect(purpose, refreshToken);

        // Steps 3-4: the row itself. `Connect` raises channel_connection_changed.v1 (D13) and `Reconnect` does the
        // same on the existing row; both are written by the caller's one SaveChanges, with the outbox row.
        if (existing is null)
        {
            database.ChannelConnections.Add(
                ChannelConnection.Connect(
                    connectionId,
                    request.UserId,
                    request.Platform,
                    request.Channel.Id,
                    request.Channel.Title,
                    accessTokenEnc,
                    refreshTokenEnc,
                    request.Tokens.ExpiresAt,
                    request.Tokens.Scope,
                    request.OAuthClientId,
                    request.Now));
        }
        else
        {
            existing.Reconnect(
                request.Channel.Title,
                accessTokenEnc,
                refreshTokenEnc,
                request.Tokens.ExpiresAt,
                request.Tokens.Scope,
                request.OAuthClientId,
                request.Now);
        }

        // Step 3 continued: the mail is queued (never sent) after the upsert, so a refusal above cannot leave a
        // message behind. Its own failure is an error answer: the pipeline rolls the whole unit back, and the
        // callback answers its fixed redirect.
        var queued = await email
            .QueueAsync(
                new EmailMessage(request.UserId, EmailTemplate, profile.UiLocale, EmailParameters(request, profile)),
                ct)
            .ConfigureAwait(false);
        if (queued.IsError)
        {
            LogEmailQueueFailed(request.UserId, queued.FirstError.Code);

            return queued.Errors;
        }

        LogConnected(request.UserId, connectionId, reconnected: existing is not null);

        return new ConnectChannelOutcome(connectionId, Reconnected: existing is not null);
    }

    /// <summary>
    /// The five substitution values of the <c>channel_connected</c> template (06.1.2-T2 task §Design step 3):
    /// the account's display name, the channel title, the instant, a short device summary and the static security
    /// path. No token, no address, no client id — the template is not token-bearing (06 README D7).
    /// </summary>
    /// <param name="request">The connect that is being written.</param>
    /// <param name="profile">The claiming account, for the display name.</param>
    /// <returns>The values the catalog declares for <see cref="EmailTemplate"/>.</returns>
    private static Dictionary<string, string> EmailParameters(ConnectChannelConnectionRequest request, UserProfile profile) =>
        new(StringComparer.Ordinal)
        {
            ["displayName"] = profile.DisplayName,
            ["channelTitle"] = request.Channel.Title,
            ["occurredAt"] = request.Now.ToString("O", CultureInfo.InvariantCulture),
            ["device"] = DeviceSummary(request.Info?.UserAgent),
            ["securityLink"] = SecurityLink,
        };

    /// <summary>
    /// A short, non-secret summary of the caller's <c>User-Agent</c> for the e-mail: the first parenthesised
    /// platform clause (<c>Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)</c>), the whole value when it has none,
    /// cut at <see cref="MaxDeviceLength"/> with an ellipsis. A blank or absent header — a direct dispatch with no
    /// request — answers <see cref="UnknownDevice"/>.
    /// </summary>
    /// <param name="userAgent">The header value, or null.</param>
    /// <returns>The value the e-mail renders as the device.</returns>
    private static string DeviceSummary(string? userAgent)
    {
        if (userAgent is not { Length: > 0 } agent)
        {
            return UnknownDevice;
        }

        var platformClauseEnd = agent.IndexOf(')');
        var summary = platformClauseEnd >= 0 ? agent[..(platformClauseEnd + 1)] : agent;

        return summary.Length <= MaxDeviceLength
            ? summary
            : string.Concat(summary.AsSpan(0, MaxDeviceLength - 1), "…");
    }

    [LoggerMessage(
        EventId = 6421,
        Level = LogLevel.Information,
        Message = "Channel connection {ConnectionId} was written for user {UserId} (reconnected: {Reconnected}).")]
    private partial void LogConnected(Guid userId, Guid connectionId, bool reconnected);

    [LoggerMessage(
        EventId = 6422,
        Level = LogLevel.Warning,
        Message = "A channel connect for user {UserId} was refused: the channel has a live connection of another account.")]
    private partial void LogChannelOwnedByAnotherUser(Guid userId);

    [LoggerMessage(
        EventId = 6423,
        Level = LogLevel.Warning,
        Message = "The channel connect e-mail of user {UserId} was not queued ({ErrorCode}).")]
    private partial void LogEmailQueueFailed(Guid userId, string errorCode);

    [LoggerMessage(
        EventId = 6424,
        Level = LogLevel.Warning,
        Message = "A channel connect for user {UserId} was refused: the account no longer exists.")]
    private partial void LogAccountMissing(Guid userId);

    [LoggerMessage(
        EventId = 6425,
        Level = LogLevel.Warning,
        Message = "A channel connect for user {UserId} was refused: the grant carried no refresh token.")]
    private partial void LogRefreshTokenMissing(Guid userId);
}
