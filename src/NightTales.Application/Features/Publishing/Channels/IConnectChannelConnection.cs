using System.Globalization;
using System.Text;
using ErrorOr;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Security;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Features.Publishing.Channels;

/// <summary>
/// The 06.1.2-T1 → 06.1.2-T2 hand-off of the callback (06.1.2-T2 task file §Design): the channel, the tokens
/// and the pinned client go in, the connection row's id and whether it replaced a live row come out.
/// </summary>
/// <remarks>
/// <para>
/// It is declared here because the callback's unit of work owns the call order, while the write itself — the
/// row lock, the insert or the reconnect, the token protection under the connection's own purpose and the
/// external identity — is 06.1.2-T2's. The implementation is
/// <c>ConnectChannelConnectionHandler</c>; the callback resolves this port lazily
/// (<c>IServiceProvider.GetService</c>) and treats its absence as a failure inside unit #2, which keeps the
/// Api's start-up validation and the build-time OpenAPI generator booting while the <c>youtube_publish</c> flag
/// stays off.
/// </para>
/// <para>
/// <b>The implementation must not flush.</b> It joins the caller's unit of work #2 and never commits: the
/// callback answers every non-connected outcome as an <c>ErrorOr</c> error, so the pipeline rolls that
/// transaction back and whatever the implementation tracked vanishes with it. An explicit
/// <c>SaveChanges</c> or <c>ExecuteUpdate</c> before the answer is wrong even when nothing failed: it bypasses
/// the single <c>SaveChangesAsync</c> the pipeline owns, and a unique-index violation inside it aborts the
/// Npgsql transaction (25P02), which moves the 23505 mapping out of <c>EfUnitOfWork</c>'s commit mapping into
/// this port. There the refusal must be answered as <c>channel_already_connected</c>, never thrown.
/// </para>
/// <para>
/// <b>What it writes, all inside that one unit</b> (06 CR-22): the connection row, the
/// <c>channel_connection_changed.v1</c> outbox message the aggregate raises, and one queued
/// <c>channel_connected</c> e-mail (<see cref="NightTales.Application.Abstractions.Email.IEmailSender"/> only —
/// never SMTP). A rollback therefore leaves no connection, no outbox row and no <c>email_messages</c> row.
/// </para>
/// </remarks>
public interface IConnectChannelConnection
{
    /// <summary>Inserts the connection, or reconnects the one this account already has.</summary>
    /// <param name="request">The account, the channel, the tokens and the pinned client.</param>
    /// <param name="ct">The request cancellation token.</param>
    /// <returns>
    /// The connection row's id and whether the write reconnected a live row of the caller, or an error:
    /// 409 <c>channel_already_connected</c> when a live row of this channel belongs to another account (or, for
    /// two simultaneous inserts, when <c>ux_channel_connections_account</c> refuses the loser), and
    /// <c>oauth_exchange_failed</c> when a refresh token is missing from the grant.
    /// </returns>
    Task<ErrorOr<ConnectChannelOutcome>> ConnectAsync(ConnectChannelConnectionRequest request, CancellationToken ct);
}

/// <summary>
/// What one connect wrote (06 CR-22): the connection row's id and whether it replaced a live row of the same
/// user — the value 06.1.2-T1's audit row records as <c>data.reconnected</c> instead of a hard-coded
/// <c>false</c>.
/// </summary>
/// <param name="ConnectionId">
/// The <c>channel_connections.id</c> the callback's audit row and log line name — the new row's id, or the
/// existing row's id on a reconnect (YR11: the id never changes under a reconnect).
/// </param>
/// <param name="Reconnected"><see langword="true"/> when an existing live row was updated instead of inserted.</param>
public sealed record ConnectChannelOutcome(Guid ConnectionId, bool Reconnected);

/// <summary>
/// What one connect hands to the connection writer (06.1.2-T2 task file §Design): the account, the
/// platform, the channel, the tokens, the client the state row pinned and the instant of the callback.
/// </summary>
/// <param name="UserId">
/// The account the connection belongs to — the <c>oauth_states.user_id</c> the anonymous callback claimed,
/// never anything the request carried.
/// </param>
/// <param name="Platform">The platform the connect was started for.</param>
/// <param name="Channel">The channel the grant belongs to.</param>
/// <param name="Tokens">The granted tokens; the implementation protects them, this layer never stores them.</param>
/// <param name="OAuthClientId">The client the state row pinned (06 README D5/F19).</param>
/// <param name="Now">The callback's clock reading.</param>
/// <param name="Info">
/// The caller metadata the flow already holds — the callback's own <see cref="RequestInfo"/>, used for the
/// e-mail's <c>device</c> value. It travels here instead of being read from an ambient accessor because this
/// handler is also composed by the Worker (which has no HTTP request): a null value is a direct Mediator
/// dispatch with no caller, and the e-mail then says <c>unknown device</c>.
/// </param>
public sealed record ConnectChannelConnectionRequest(
    Guid UserId,
    NightTales.Domain.Publishing.Platform Platform,
    GoogleChannel Channel,
    OAuthTokens Tokens,
    string OAuthClientId,
    DateTimeOffset Now,
    RequestInfo? Info = null)
{
    /// <summary>
    /// Prints the ids, the channel and the instant only (R8/F4). The compiler-generated list would put
    /// the nested <see cref="Tokens"/> — the access and refresh tokens — into every log line, exception message
    /// and debugger string.
    /// </summary>
    /// <param name="builder">The record's string builder.</param>
    /// <returns><see langword="true"/>, because members were printed.</returns>
    private bool PrintMembers(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Append(
            CultureInfo.InvariantCulture,
            $"UserId = {UserId}, Platform = {Platform}, OAuthClientId = {OAuthClientId}, Now = {Now}");
        return true;
    }
}
