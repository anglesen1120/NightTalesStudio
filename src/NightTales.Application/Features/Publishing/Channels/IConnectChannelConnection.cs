using System.Globalization;
using System.Text;
using ErrorOr;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Features.Publishing.Channels;

/// <summary>
/// The 06.1.2-T1 → 06.1.2-T2 hand-off of the callback (06.1.2-T2 task file §Design): the channel, the
/// tokens and the pinned client go in, the connection row's id comes out.
/// </summary>
/// <remarks>
/// <para>
/// It is declared here because the callback's unit of work owns the call order, while the write itself —
/// the row lock, the insert or the reconnect, the token protection under the connection's own purpose and
/// the external identity — is 06.1.2-T2's. <b>T2 ships the implementation</b>; no production
/// implementation is registered yet, so the callback resolves this port lazily
/// (<c>IServiceProvider.GetService</c>) and treats its absence as a failure inside unit #2. That keeps
/// the Api's start-up validation and the build-time OpenAPI generator booting while the
/// <c>youtube_publish</c> flag stays off.
/// </para>
/// <para>
/// <b>The implementation must not flush.</b> It joins the caller's unit of work #2 and never commits: the
/// callback answers every non-connected outcome as an <c>ErrorOr</c> error, so the pipeline rolls that
/// transaction back and whatever the implementation tracked vanishes with it. An explicit
/// <c>SaveChanges</c> or <c>ExecuteUpdate</c> before the answer is still wrong, for two narrower reasons:
/// it bypasses the single <c>SaveChangesAsync</c> the pipeline owns, and a unique-index violation inside it
/// aborts the Npgsql transaction (25P02), which moves the 23505 mapping out of <c>EfUnitOfWork</c>'s commit
/// mapping into <c>ConnectAsync</c>. There the refusal must be answered as
/// <c>channel_already_connected</c>, never thrown.
/// </para>
/// </remarks>
public interface IConnectChannelConnection
{
    /// <summary>Inserts the connection, or reconnects the one this account already has.</summary>
    /// <param name="request">The account, the channel, the tokens and the pinned client.</param>
    /// <param name="ct">The request cancellation token.</param>
    /// <returns>
    /// The connection row's id, or an error: 409 <c>channel_already_connected</c> when the unique
    /// <c>(user, platform)</c> index refuses the insert — or the channel is already connected to another
    /// account and 06.1.2-T2's <c>oauth_client_in_use</c> rule applies.
    /// </returns>
    Task<ErrorOr<Guid>> ConnectAsync(ConnectChannelConnectionRequest request, CancellationToken ct);
}

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
public sealed record ConnectChannelConnectionRequest(
    Guid UserId,
    NightTales.Domain.Publishing.Platform Platform,
    GoogleChannel Channel,
    OAuthTokens Tokens,
    string OAuthClientId,
    DateTimeOffset Now)
{
    /// <summary>
    /// Prints the ids, the channel and the instant only (R8/F4). The compiler-generated list would put the
    /// nested <see cref="Tokens"/> — the access and refresh tokens — into every log line, exception message
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
